using System.IO;
using System.Text.Json.Serialization;
using PuppeteerSharp;
using CourseAutoLearner.Models;

namespace CourseAutoLearner.Services;

public class BrowserService : IAsyncDisposable
{
    private IBrowser? _browser;
    private IPage? _page;
    public bool IsOpen => _browser != null && !_browser.IsClosed;

    public BrowserService()
    {
    }

    public BrowserService(Models.AppSettings settings) => UpdateSettings(settings);

    public event Action<string>? OnLog;

    private readonly static string RunLogFile = Path.Combine(
        AppContext.BaseDirectory, "run.log");

    private void Log(string msg)
    {
        var line = $"[{DateTime.Now:HH:mm:ss}] {msg}";
        OnLog?.Invoke(line);
        try { File.AppendAllText(RunLogFile, line + Environment.NewLine); }
        catch { }
    }

    private readonly static string UserDataDir = Path.Combine(
        AppContext.BaseDirectory, "browser-profile");

    private const string HomeUrl = "http://jxjycj.suda.edu.cn/ws/student/index";
    private const string LoginSelector = "#app > div > div.cj-login-con";
    private const string LoginIframeUrlPart = "http://jxjycj.suda.edu.cn/center/login";

    private const string CollegeSelector =
        "body > div.main.std-main > div.banner > div > ul > li:nth-child(2) > div > div.con > span";

    private const string ExpectedCollege = "计算机科学与技术学院";

    public event Func<Task>? OnLoginSuccess;

    public event Action? OnBrowserClosed;

    public event Func<Course, Task>? OnCourseChaptersFetched;

    public event Action<string, string, string>? OnQuizScored;

    private bool _loginWatcherRunning = false;

    private bool _hasLoggedInOnce = false;

    private CancellationTokenSource? _alertWatcherCts;

    private AiService? _ai;
    private string? _chromePath;

    public void UpdateSettings(Models.AppSettings settings)
    {
        _ai = settings.IsAiConfigured ? new AiService(settings) : null;
        _chromePath = settings.ChromePath;
    }

    private static string? DetectChromePath()
    {
        if (OperatingSystem.IsWindows())
        {
            var paths = new[]
            {
                @"C:\Program Files\Google\Chrome\Application\chrome.exe",
                @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe",
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    @"Google\Chrome\Application\chrome.exe"),
            };
            return paths.FirstOrDefault(File.Exists);
        }
        if (OperatingSystem.IsMacOS())
        {
            var path = "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome";
            return File.Exists(path) ? path : null;
        }
        if (OperatingSystem.IsLinux())
        {
            var paths = new[] { "/usr/bin/google-chrome", "/usr/bin/chromium-browser", "/usr/bin/chromium" };
            return paths.FirstOrDefault(File.Exists);
        }
        return null;
    }

    public void StartAlertWatcher()
    {
        StopAlertWatcher();
        _alertWatcherCts = new CancellationTokenSource();
        var ct = _alertWatcherCts.Token;
        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(10000, ct); }
                catch { break; }

                if (ct.IsCancellationRequested) break;
                try { await HandleStudyAlertAsync(); }
                catch
                {
                }
            }
        }, ct);
    }

    public void StopAlertWatcher()
    {
        _alertWatcherCts?.Cancel();
        _alertWatcherCts = null;
    }

    private async Task HandleStudyAlertAsync()
    {
        if (_page == null || _browser == null || _browser.IsClosed) return;

        var hasAlert = await _page.EvaluateFunctionAsync<bool>(@"() => {
            const content = document.querySelector('body > div.layui-layer.layui-layer-dialog > div.layui-layer-content');
            return content && content.textContent.includes('点击“确定”继续学习');
        }");

        if (!hasAlert) return;

        Log("⚠️  检测到30分钟学习提醒，自动点击继续...");

        try
        {
            await _page.ClickAsync(
                "body > div.layui-layer.layui-layer-dialog > div.layui-layer-btn.layui-layer-btn- > a");
            await Task.Delay(800);
            Log("  已点击继续学习");
        }
        catch (Exception ex)
        {
            Log($"  点击继续失败: {ex.Message}");
            return;
        }
        
        try
        {
            var mcHandle = await _page.QuerySelectorAsync("#mainContent");
            if (mcHandle == null) return;
            var mcFrame = await mcHandle.ContentFrameAsync();
            if (mcFrame == null) return;
            var mfHandle = await mcFrame.QuerySelectorAsync("#mainFrame");
            if (mfHandle == null) return;
            var mainFrame = await mfHandle.ContentFrameAsync();
            if (mainFrame == null) return;

            var isPaused = await mainFrame.EvaluateFunctionAsync<bool>(@"() => {
                const icon = document.querySelector('#player_pause_btn > a > i');
                if (icon && icon.classList.contains('icon-play-sp-fill')) return true;
                const v = document.querySelector('video');
                return v && v.paused && !v.ended;
            }");
            if (isPaused)
            {
                // Try custom play button first, then fallback to video.play()
                var clicked = await mainFrame.EvaluateFunctionAsync<bool>(@"() => {
                    const icon = document.querySelector('#player_pause_btn > a > i');
                    if (icon && icon.classList.contains('icon-play-sp-fill')) {
                        document.querySelector('#player_pause_btn > a').click();
                        return true;
                    }
                    const v = document.querySelector('video');
                    if (v && v.paused) { v.play(); return true; }
                    return false;
                }");
                if (clicked) Log("  已恢复视频播放");
            }
        }
        catch
        {
        }
    }

    public async Task OpenBrowserAsync()
    {
        if (IsOpen) return;

        Log("正在下载/检查浏览器...");
        Directory.CreateDirectory(UserDataDir);
        Log("正在打开浏览器...");
        var chromePath = _chromePath ?? DetectChromePath();
        if (string.IsNullOrEmpty(chromePath))
            Log("未找到 Chrome，将使用 PuppeteerSharp 自动下载的 Chromium");
        else
            Log($"使用浏览器: {chromePath}");

        var launchOptions = new LaunchOptions
        {
            Headless = false,
            DefaultViewport = null,
            UserDataDir = UserDataDir,
            Args = ["--start-maximized", "--enable-automation", "--no-default-browser-check"],
            IgnoredDefaultArgs = ["--enable-automation", "--enable-blink-features=IdleDetection"],
        };
        if (!string.IsNullOrEmpty(chromePath))
            launchOptions.ExecutablePath = chromePath;

        _browser = await Puppeteer.LaunchAsync(launchOptions);

        var pages = await _browser.PagesAsync();
        _page = pages.Length > 0 ? pages[0] : await _browser.NewPageAsync();

        _browser.Disconnected += OnBrowserDisconnected;

        foreach (var p in pages) RegisterDialogHandler(p);
        _browser.TargetCreated += OnTargetCreated;



        Log("正在打开首页...");
        await _page.GoToAsync(HomeUrl, new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle2],
            Timeout = 30000
        });

        await CheckLoginAsync();
    }

    private void OnBrowserDisconnected(object? sender, EventArgs e)
    {
        _page = null;
        _browser = null;
        _hasLoggedInOnce = false;
        OnBrowserClosed?.Invoke();
    }

    private void OnTargetCreated(object? sender, TargetChangedArgs e)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                var page = await e.Target.PageAsync();
                if (page != null) RegisterDialogHandler(page);
            }
            catch { }
        });
    }

    private static void RegisterDialogHandler(IPage page)
    {
        page.Dialog += (_, e) =>
        {
            _ = Task.Run(async () =>
            {
                try { await e.Dialog.Accept(); }
                catch { }
            });
        };
    }

    private void OnFrameNavigated(object? sender, FrameNavigatedEventArgs e)
    {
        if (e.Frame.ParentFrame != null) return;

        _ = Task.Run(async () =>
        {
            await Task.Delay(1500);
            if (_loginWatcherRunning) return;
            if (await IsLoggedInAsync()) return;
            if (!await IsLoginPageAsync()) return;

            Log("⚠️  检测到登录超时，请在浏览器中使用微信扫码重新登录");
            await WaitForLoginAsync();
        });
    }

    private async Task CheckLoginAsync()
    {
        await Task.Delay(1500);

        if (await IsLoggedInAsync())
        {
            Log("✅  已登录，正在读取用户信息...");
            if (!_hasLoggedInOnce)
            {
                _hasLoggedInOnce = true;
                _page!.FrameNavigated += OnFrameNavigated;
            }

            if (OnLoginSuccess != null) await OnLoginSuccess.Invoke();
            return;
        }

        if (await IsLoginPageAsync())
            Log("⚠️  检测到登录页面，请在浏览器中使用微信扫码登录");
        else
            Log("页面加载中，等待登录状态...");

        await WaitForLoginAsync();
    }

    private async Task WaitForLoginAsync()
    {
        if (_loginWatcherRunning) return;
        _loginWatcherRunning = true;

        try
        {
            Log("登录完成后将自动继续...");
            var deadline = DateTime.Now.AddMinutes(5);

            while (DateTime.Now < deadline)
            {
                await Task.Delay(2000);
                if (await IsLoggedInAsync())
                {
                    Log("✅  登录成功");
                    await Task.Delay(1000);
                    if (!_hasLoggedInOnce)
                    {
                        _hasLoggedInOnce = true;
                        _page!.FrameNavigated += OnFrameNavigated;
                    }

                    if (OnLoginSuccess != null) await OnLoginSuccess.Invoke();
                    return;
                }
            }

            Log("⚠️  等待登录超时，请手动完成登录后点击「↻」刷新用户信息");
        }
        finally
        {
            _loginWatcherRunning = false;
        }
    }

    private async Task<bool> IsLoggedInAsync()
    {
        try
        {
            var el = await _page!.QuerySelectorAsync(CollegeSelector);
            if (el == null) return false;
            var text = await el.EvaluateFunctionAsync<string>("el => el.textContent.trim()");
            return text == ExpectedCollege;
        }
        catch { return false; }
    }

    private async Task<bool> IsLoginPageAsync()
    {
        try
        {
            if (await _page!.QuerySelectorAsync(LoginSelector) != null) return true;

            var hasLoginFrame = await _page.EvaluateFunctionAsync<bool>(@"() => {
                return Array.from(document.querySelectorAll('iframe'))
                            .some(f => f.src && f.src.includes('/center/login'));
            }");
            return hasLoginFrame;
        }
        catch { return false; }
    }

    public async Task CloseBrowserAsync()
    {
        StopAlertWatcher();
        if (_browser != null)
        {
            _browser.Disconnected -= OnBrowserDisconnected;
            _browser.TargetCreated -= OnTargetCreated;
            if (_page != null)
                _page.FrameNavigated -= OnFrameNavigated;
            await _browser.CloseAsync();
            _browser = null;
            _page = null;
            Log("浏览器已关闭");
        }
    }

    public async Task<(string Name, string StudentId)> FetchUserInfoAsync()
    {
        EnsurePage();
        var name = await EvalPageTextAsync(
            "body > div.main.std-main > div.banner > div > div > div.wrap > div.name");
        var studentId = await EvalPageTextAsync(
            "body > div.main.std-main > div.banner > div > ul > li:nth-child(1) > div > div.con > span");
        return (name, studentId);
    }

    public async Task<List<Course>> NavigateToCourseListAsync()
    {
        EnsurePage();
        const int maxRetries = 50;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                await WaitForMainFrameAsync();

                var iframeSrc = await _page!.EvaluateFunctionAsync<string>(@"() => {
                    const f = document.querySelector('#mainRight');
                    return f ? f.src : '';
                }");

                if (!iframeSrc.Contains("/student/leftmenu/program", StringComparison.OrdinalIgnoreCase))
                {
                    Log("正在进入到「我的课程」...");
                    
                    await _page.WaitForSelectorAsync(
                        "#mCSB_1_container > div:nth-child(2) > li:nth-child(1) > a",
                        new WaitForSelectorOptions { Timeout = 8000 });

                    await _page.ClickAsync("#mCSB_1_container > div:nth-child(2) > li:nth-child(1) > a");

                    await _page.WaitForFunctionAsync(@"() => {
                        const f = document.querySelector('#mainRight');
                        return f && f.src && f.src.includes('/student/leftmenu/program');
                    }", new WaitForFunctionOptions { Timeout = 10000, PollingInterval = 500 });

                    Log("已进入课程列表页");
                }
                else
                {
                    Log("已在课程列表页，开始读取...");
                }

                await WaitForCourseFrameAsync();

                var courses = await ParseCourseTableAsync();
                if (courses.Count == 0 && attempt < maxRetries)
                {
                    Log($"  课程列表为空，第 {attempt} 次重试...");
                    await Task.Delay(2000);
                    continue;
                }

                if (OnCourseChaptersFetched != null)
                    foreach (var c in courses)
                        await OnCourseChaptersFetched(c);

                var mainRightHandle = await _page.QuerySelectorAsync("#mainRight");
                var courseFrame = mainRightHandle != null ? await mainRightHandle.ContentFrameAsync() : null;

                if (courseFrame != null)
                {
                    var availableCourses = courses.Where(c => c.Status == CourseStatus.Available).ToList();
                    Log($"开始读取 {availableCourses.Count} 门可学习课程的课件目录...");

                    foreach (var course in availableCourses)
                    {
                        await FetchCourseChaptersAsync(course, courseFrame);
                        if (OnCourseChaptersFetched != null)
                            await OnCourseChaptersFetched(course);
                        mainRightHandle = await _page.QuerySelectorAsync("#mainRight");
                        courseFrame = mainRightHandle != null ? await mainRightHandle.ContentFrameAsync() : null;
                        if (courseFrame == null) break;
                    }
                }

                return courses;
            }
            catch (Exception ex)
            {
                Log($"读取课程失败 (第 {attempt}/{maxRetries} 次): {ex.Message}");
                if (attempt < maxRetries)
                {
                    Log("等待 3 秒后重试...");
                    await Task.Delay(3000);
                }
            }
        }

        Log("读取课程失败，已达最大重试次数");
        return [];
    }

    private async Task WaitForMainFrameAsync()
    {
        try
        {
            await _page!.WaitForSelectorAsync("#mainRight",
                new WaitForSelectorOptions { Timeout = 10000 });
        }
        catch
        {
        }
    }

    private async Task WaitForCourseFrameAsync()
    {
        await Task.Delay(1500);

        var deadline = DateTime.Now.AddSeconds(15);
        while (DateTime.Now < deadline)
        {
            try
            {
                var iframeHandle = await _page!.QuerySelectorAsync("#mainRight");
                if (iframeHandle != null)
                {
                    var frame = await iframeHandle.ContentFrameAsync();
                    if (frame != null)
                    {
                        var hasTable = await frame.EvaluateFunctionAsync<bool>(
                            "() => !!document.querySelector('body > div > div > div.contents-body > table tbody tr')");
                        if (hasTable) return;
                    }
                }
            }
            catch { }

            await Task.Delay(500);
        }
    }

    private async Task FetchCourseChaptersAsync(Course course, IFrame courseFrame)
    {
        IPage? coursePage = null;
        try
        {
            Log($"  正在读取课件目录: {course.CourseName}");
            var clicked = await courseFrame.EvaluateFunctionAsync<bool>($@"() => {{
                const links = document.querySelectorAll('a.btn-ghost-solid-primary:not(.disabled)');
                for (const a of links) {{
                    if (a.href && a.href.includes('courseId=') && a.href.includes('{course.EntryUrl.Split("courseId=").LastOrDefault()?.Split("&").FirstOrDefault() ?? ""}')) {{
                        a.click();
                        return true;
                    }}
                }}
                return false;
            }}");

            if (!clicked)
            {
                Log($"    未找到「开始学习」按钮，跳过");
                return;
            }

            coursePage = await WaitForNewPageAsync(timeout: 10000);
            if (coursePage == null)
            {
                Log($"    新页面未打开，跳过");
                return;
            }

            await coursePage.WaitForNavigationAsync(new NavigationOptions
            {
                WaitUntil = [WaitUntilNavigation.Networkidle2],
                Timeout = 15000
            }).ContinueWith(_ => Task.CompletedTask);

            await Task.Delay(500);

            var unpublished = await coursePage.EvaluateFunctionAsync<bool>(@"() => {
                const el = document.querySelector('#learn > div > div > p');
                return el && el.textContent.includes('课件未发布');
            }");

            if (unpublished)
            {
                Log($"    课件未发布，标记课程");
                course.Status = CourseStatus.NotStarted;
                course.ActionText = "课件未发布";
                return;
            }

            var hasHelper = await coursePage.EvaluateFunctionAsync<bool>(
                "() => !!document.querySelector('#learn-helper-main')");
            if (hasHelper)
            {
                Log($"    关闭学习助手...");
                try
                {
                    var helperIframeHandle = await coursePage.QuerySelectorAsync("#learnHelperIframe");
                    var helperFrame = helperIframeHandle != null
                        ? await helperIframeHandle.ContentFrameAsync()
                        : null;

                    if (helperFrame != null)
                        await helperFrame.ClickAsync("#helper-page > div > div.helper-head > a > i");
                    else
                        await coursePage.ClickAsync("#learnHelperIframe");

                    await Task.Delay(800);
                }
                catch
                {
                }
            }
            await coursePage.WaitForSelectorAsync("#courseware_main_menu > a",
                new WaitForSelectorOptions { Timeout = 10000 });
            await coursePage.ClickAsync("#courseware_main_menu > a");
            Log($"    已点击课件目录");
            await coursePage.WaitForSelectorAsync("#mainContent",
                    new WaitForSelectorOptions { Timeout = 10000 })
                .ContinueWith(_ => Task.CompletedTask);

            var mcHandle = await coursePage.QuerySelectorAsync("#mainContent");
            var mcFrame = mcHandle != null ? await mcHandle.ContentFrameAsync() : null;
            if (mcFrame != null)
            {
                try
                {
                    await mcFrame.WaitForSelectorAsync("#learnMenu .s_point",
                        new WaitForSelectorOptions { Timeout = 12000 });
                }
                catch
                {
                }
            }

            await Task.Delay(300);
            
            course.Chapters = await ParseLearnMenuAsync(coursePage);
            Log($"    解析完成：{course.Chapters.Count} 章，" +
                $"{course.Chapters.SelectMany(c => c.Lessons).Count()} 讲，" +
                $"{course.Chapters.SelectMany(c => c.Lessons).SelectMany(l => l.Items).Count()} 个课件");
        }
        catch (Exception ex)
        {
            Log($"    读取课件目录失败: {ex.Message}");
        }
        finally
        {
            if (coursePage != null && !coursePage.IsClosed)
                await coursePage.CloseAsync();
        }
    }
    
    private async Task<IPage?> WaitForNewPageAsync(int timeout = 10000)
    {
        var tcs = new TaskCompletionSource<IPage>();

        void Handler(object? s, TargetChangedArgs e)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var page = await e.Target.PageAsync();
                    if (page != null) tcs.TrySetResult(page);
                }
                catch { }
            });
        }

        _browser!.TargetCreated += Handler;
        try
        {
            var winner = await Task.WhenAny(tcs.Task, Task.Delay(timeout));
            return winner == tcs.Task ? tcs.Task.Result : null;
        }
        finally
        {
            _browser.TargetCreated -= Handler;
        }
    }
    
    private async Task<List<Chapter>> ParseLearnMenuAsync(IPage page)
    {
        var mainContentHandle = await page.QuerySelectorAsync("#mainContent");
        var mainContentFrame = mainContentHandle != null
            ? await mainContentHandle.ContentFrameAsync()
            : null;

        if (mainContentFrame == null)
        {
            Log("    未找到 #mainContent iframe");
            return [];
        }

        var data = await mainContentFrame.EvaluateFunctionAsync<ChapterData[]>(@"() => {
            const chapters = [];
            const chapterEls = document.querySelectorAll('#learnMenu .s_chapter');

            for (const chEl of chapterEls) {
                const chId    = chEl.id;
                const chTitle = chEl.getAttribute('title') || chEl.textContent.trim();
                const chName  = chTitle;
                const lessons = [];

                let sectionList = chEl.nextElementSibling;
                if (!sectionList || !sectionList.classList.contains('s_sectionlist')) {
                    chapters.push({ chId, chName, lessons });
                    continue;
                }

                const sectionEls = sectionList.querySelectorAll('.s_section');
                for (const secEl of sectionEls) {
                    const secId    = secEl.id;
                    const secTitle = secEl.getAttribute('title') || secEl.textContent.trim();
                    const items    = [];

                    let wrap = secEl.nextElementSibling;
                    if (!wrap || !wrap.classList.contains('s_sectionwrap')) {
                        lessons.push({ secId, secTitle, items });
                        continue;
                    }

                    const pointEls = wrap.querySelectorAll('.s_point');
                    for (const pt of pointEls) {
                        const ptId      = pt.id;
                        const ptTitle   = pt.getAttribute('title') || '';
                        const itemtype  = pt.getAttribute('itemtype') || 'video';
                        const completed = pt.getAttribute('completestate') === '1';
                        const labelEl   = pt.querySelector('.s_pointti');
                        const label     = labelEl ? labelEl.textContent.trim() : ptTitle;
                        items.push({ ptId, label, itemtype, completed });
                    }

                    lessons.push({ secId, secTitle, items });
                }

                chapters.push({ chId, chName, lessons });
            }
            return chapters;
        }");

        if (data == null) return [];

        return data.Select(ch => new Chapter
        {
            ChapterId = ch.ChId,
            ChapterName = ch.ChName,
            Lessons = ch.Lessons.Select(sec => new Lesson
            {
                LessonId = sec.SecId,
                LessonName = sec.SecTitle,
                Items = sec.Items.Select(pt => new CourseItem
                {
                    ItemId = pt.PtId,
                    ItemName = pt.Label,
                    Type = pt.Itemtype == "test" ? CourseItemType.Quiz : CourseItemType.Video,
                    IsLearned = pt.Completed
                }).ToList()
            }).ToList()
        }).ToList();
    }
    
    private class ChapterData
    {
        [JsonPropertyName("chId")] public string ChId { get; set; } = "";
        [JsonPropertyName("chName")] public string ChName { get; set; } = "";
        [JsonPropertyName("lessons")] public LessonData[] Lessons { get; set; } = [];
    }

    private class LessonData
    {
        [JsonPropertyName("secId")] public string SecId { get; set; } = "";
        [JsonPropertyName("secTitle")] public string SecTitle { get; set; } = "";
        [JsonPropertyName("items")] public PointData[] Items { get; set; } = [];
    }

    private class PointData
    {
        [JsonPropertyName("ptId")] public string PtId { get; set; } = "";
        [JsonPropertyName("label")] public string Label { get; set; } = "";
        [JsonPropertyName("itemtype")] public string Itemtype { get; set; } = "";
        [JsonPropertyName("completed")] public bool Completed { get; set; }
    }
    
    private async Task<List<Course>> ParseCourseTableAsync()
    {
        var courses = new List<Course>();
        
        var iframeHandle = await _page!.QuerySelectorAsync("#mainRight");
        if (iframeHandle == null)
        {
            Log("未找到 #mainRight iframe 元素");
            return courses;
        }

        var courseFrame = await iframeHandle.ContentFrameAsync();
        if (courseFrame == null)
        {
            Log("未找到课程列表 iframe，请确认已进入「我的课程」页面");
            return courses;
        }

        var rows = await courseFrame.EvaluateFunctionAsync<List<CourseRow>>(@"() => {
            const trs = document.querySelectorAll(
                'body > div > div > div.contents-body > table tbody tr');
            const result = [];
            const colSpan = {};
            const colText = {}; // 记录每列当前 rowspan 覆盖的文本

            for (const tr of trs) {
                const rawCells = Array.from(tr.querySelectorAll('td'));
                const logicalCols = [];
                let rawIdx = 0;
                for (let col = 0; col < 6; col++) {
                    if (colSpan[col] > 0) {
                        logicalCols.push(colText[col]); // 沿用上方 rowspan 的文本
                        colSpan[col]--;
                    } else {
                        const cell = rawCells[rawIdx++];
                        if (!cell) { logicalCols.push(''); continue; }
                        const rs = parseInt(cell.getAttribute('rowspan') || '1');
                        const txt = (cell.textContent || '').trim();
                        if (rs > 1) { colSpan[col] = rs - 1; colText[col] = txt; }
                        logicalCols.push(txt);
                    }
                }

                const semester   = logicalCols[0] || '';
                const courseType = logicalCols[1] || '';
                const courseId   = logicalCols[2] || '';
                const rawName    = logicalCols[3] || '';
                const teachType  = logicalCols[4] || '';

                if (!courseId) continue;

                const courseName = rawName.includes('/')
                    ? rawName.split('/')[0].trim() : rawName;

                // action 列（第6列）每行都有独立 td，直接取最后一个
                const tds = tr.querySelectorAll('td');
                const actionCell = tds.length > 0 ? tds[tds.length - 1] : null;
                const a = actionCell ? actionCell.querySelector('a') : null;
                const href       = a ? (a.getAttribute('href') || '') : '';
                const actionText = a ? (a.textContent || '').trim() : '';
                const disabled   = !a || href === '#' || a.classList.contains('disabled');

                result.push({
                    courseId, courseName, semester, courseType, teachType,
                    actionText, href, disabled
                });
            }
            return result;
        }");
        if (rows == null || rows.Count == 0)
        {
            Log("未解析到课程，请检查页面是否加载完成");
            return courses;
        }

        foreach (var row in rows)
        {
            var status = row.ActionText switch
            {
                "面授学习" => CourseStatus.Offline,
                "开始学习" => CourseStatus.Available,
                _ => CourseStatus.NotStarted
            };

            var url = (!row.Disabled && row.Href != "#")
                ? (row.Href.StartsWith("http") ? row.Href : $"http://jxjycj.suda.edu.cn{row.Href}")
                : string.Empty;

            courses.Add(new Course
            {
                CourseId = row.CourseId,
                CourseName = row.CourseName,
                Semester = row.Semester,
                CourseType = row.CourseType,
                TeachType = row.TeachType,
                Status = status,
                ActionText = row.ActionText,
                EntryUrl = url,
            });

            Log($"  {row.Semester} | {row.CourseName} ({row.CourseId}) [{row.ActionText}]");
        }

        Log($"共读取到 {courses.Count} 门课程");
        return courses;
    }

    private class CourseRow
    {
        [JsonPropertyName("courseId")] public string CourseId { get; set; } = "";
        [JsonPropertyName("courseName")] public string CourseName { get; set; } = "";
        [JsonPropertyName("semester")] public string Semester { get; set; } = "";
        [JsonPropertyName("courseType")] public string CourseType { get; set; } = "";
        [JsonPropertyName("teachType")] public string TeachType { get; set; } = "";
        [JsonPropertyName("actionText")] public string ActionText { get; set; } = "";
        [JsonPropertyName("href")] public string Href { get; set; } = "";
        [JsonPropertyName("disabled")] public bool Disabled { get; set; }
    }

    private async Task<string> EvalPageTextAsync(string selector)
    {
        try
        {
            var el = await _page!.QuerySelectorAsync(selector);
            if (el == null) return string.Empty;
            return await el.EvaluateFunctionAsync<string>("el => el.textContent.trim()");
        }
        catch { return string.Empty; }
    }
    
    private async static Task<string> EvalTextAsync(IElementHandle handle, string selector, string? js = null)
    {
        try
        {
            var el = await handle.QuerySelectorAsync(selector);
            if (el == null) return string.Empty;
            return await el.EvaluateFunctionAsync<string>(js ?? "el => el.textContent.trim()");
        }
        catch { return string.Empty; }
    }

    private async static Task<string> EvalAttrAsync(IElementHandle handle, string selector, string attr)
    {
        try
        {
            var el = await handle.QuerySelectorAsync(selector);
            if (el == null) return string.Empty;
            return await el.EvaluateFunctionAsync<string>($"el => el.{attr}");
        }
        catch { return string.Empty; }
    }
    
    public async Task LearnItemAsync(CourseItem item, bool mute, string courseName, string courseId,
        CancellationToken ct)
    {
        EnsurePage();
        Log($"正在学习: {item.ItemName} ({item.Type})");
        
        IFrame? mainFrame = await WaitForMainFrameAsync(timeoutSeconds: 15);

        var pageType = await DetectLearnPageTypeAsync(mainFrame);
        Log($"  页面类型: {pageType}");

        switch (pageType)
        {
            case LearnPageType.Video:
                await WatchVideoAsync(mainFrame, mute, ct);
                break;
            case LearnPageType.Video2:
                await WatchVideo2Async(mainFrame, mute, ct);
                break;
            case LearnPageType.Quiz:
                await DoQuizAsync(mainFrame, courseName, courseId, item.ItemId, ct);
                break;
            default:
                Log("  未识别页面类型，跳过");
                break;
        }
    }

    private enum LearnPageType
    {
        Video,
        Video2,
        Quiz,
        Unknown
    }

    private async Task<LearnPageType> DetectLearnPageTypeAsync(IFrame? mainFrame)
    {
        if (mainFrame == null) return LearnPageType.Unknown;
        try
        {
            var deadline = DateTime.Now.AddSeconds(15);
            while (DateTime.Now < deadline)
            {
                var result = await mainFrame.EvaluateFunctionAsync<string>(@"() => {
                    if (document.querySelector('#container_media')) return 'video';
                    if (document.querySelector('video')) return 'video2';
                    const btns = document.querySelectorAll('.record_submit div');
                    for (const b of btns) {
                        const t = b.textContent.trim();
                        if (t === '进入测试' || t === '再做一遍' || t === '查看答案') return 'quiz';
                    }
                    return 'unknown';
                }");
                if (result != "unknown")
                    return result switch
                    {
                        "video" => LearnPageType.Video,
                        "video2" => LearnPageType.Video2,
                        "quiz" => LearnPageType.Quiz,
                        _ => LearnPageType.Unknown
                    };
                await Task.Delay(600);
            }

            return LearnPageType.Unknown;
        }
        catch { return LearnPageType.Unknown; }
    }
    
    private async Task<IFrame?> WaitForMainFrameAsync(int timeoutSeconds = 15)
    {
        var deadline = DateTime.Now.AddSeconds(timeoutSeconds);
        while (DateTime.Now < deadline)
        {
            try
            {
                // Path 1: top → #mainContent → #mainFrame
                var mcHandle = await _page!.QuerySelectorAsync("#mainContent");
                if (mcHandle != null)
                {
                    var mcFrame = await mcHandle.ContentFrameAsync();
                    if (mcFrame != null)
                    {
                        var mfHandle = await mcFrame.QuerySelectorAsync("#mainFrame");
                        if (mfHandle != null)
                        {
                            var mfFrame = await mfHandle.ContentFrameAsync();
                            if (mfFrame != null) return mfFrame;
                        }

                        // Path 2: top → #mainContent (direct content, no #mainFrame)
                        var hasVideo = await mcFrame.EvaluateFunctionAsync<bool>(
                            "() => !!document.querySelector('video') || !!document.querySelector('.record_submit')");
                        if (hasVideo) return mcFrame;
                    }
                }
            }
            catch { }

            await Task.Delay(500);
        }

        Log("  未找到播放页面 iframe");
        return null;
    }
    
    public async Task NavigateToItemAsync(Course course, CourseItem item, string chapterId, string lessonId,
        CancellationToken ct)
    {
        EnsurePage();
        ct.ThrowIfCancellationRequested();
        
        if (_page!.IsClosed)
        {
            Log("  学习页面已关闭，切回首页...");
        }

        var courseId = ExtractCourseId(course.EntryUrl);
        
        bool alreadyInCourse = false;
        try
        {
            var currentUrl = _page!.Url;
            alreadyInCourse = !string.IsNullOrEmpty(courseId)
                              && currentUrl.Contains(courseId, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            
        }

        if (!alreadyInCourse)
        {
            Log($"进入到课程: {course.CourseName}");
            await EnsureOnCourseListAsync();
            await ClickCourseStartButtonAsync(course, courseId);
        }

      
        await ClickCourseItemInMenuAsync(item, chapterId, lessonId, ct);
    }
    
    private async Task RecoverToHomePageAsync()
    {
        try
        {
            var pages = await _browser!.PagesAsync();
            var homePage = pages.FirstOrDefault(p =>
                               !p.IsClosed && p.Url.Contains("http://jxjycj.suda.edu.cn/ws/",
                                   StringComparison.OrdinalIgnoreCase))
                           ?? pages.FirstOrDefault(p => !p.IsClosed);

            if (homePage != null)
            {
                if (_page != null && !_page.IsClosed && _page != homePage)
                    await _page.CloseAsync();

                _page = homePage;
                await _page.BringToFrontAsync();
                Log("  已切回首页");
            }
            else
            {
                _page = await _browser!.NewPageAsync();
                await _page.GoToAsync(HomeUrl, new NavigationOptions
                {
                    WaitUntil = [WaitUntilNavigation.Networkidle2],
                    Timeout = 30000
                });
                Log("  已重新打开首页");
            }
        }
        catch (Exception ex)
        {
            Log($"  切回首页失败: {ex.Message}");
        }
    }
    
    public Task ReturnToHomePageAsync() => RecoverToHomePageAsync();
    
    private async Task EnsureOnCourseListAsync()
    {
        await WaitForMainFrameAsync();

        var iframeSrc = await _page!.EvaluateFunctionAsync<string>(@"() => {
            const f = document.querySelector('#mainRight');
            return f ? f.src : '';
        }");

        if (!iframeSrc.Contains("/student/leftmenu/program", StringComparison.OrdinalIgnoreCase))
        {
            Log("正在进入到「我的课程」...");
            await _page.WaitForSelectorAsync(
                "#mCSB_1_container > div:nth-child(2) > li:nth-child(1) > a",
                new WaitForSelectorOptions { Timeout = 8000 });
            await _page.ClickAsync("#mCSB_1_container > div:nth-child(2) > li:nth-child(1) > a");
            await _page.WaitForFunctionAsync(@"() => {
                const f = document.querySelector('#mainRight');
                return f && f.src && f.src.includes('/student/leftmenu/program');
            }", new WaitForFunctionOptions { Timeout = 10000, PollingInterval = 500 });
            await WaitForCourseFrameAsync();
        }
    }
    
    private async Task ClickCourseStartButtonAsync(Course course, string courseId)
    {
        var iframeHandle = await _page!.QuerySelectorAsync("#mainRight");
        var courseFrame = iframeHandle != null ? await iframeHandle.ContentFrameAsync() : null;
        if (courseFrame == null) throw new InvalidOperationException("未找到课程列表 iframe");

        var clicked = await courseFrame.EvaluateFunctionAsync<bool>($@"() => {{
            const links = document.querySelectorAll('a.btn-ghost-solid-primary:not(.disabled)');
            for (const a of links) {{
                if (a.href && a.href.includes('courseId=') && a.href.includes('{courseId}')) {{
                    a.click();
                    return true;
                }}
            }}
            return false;
        }}");

        if (!clicked) throw new InvalidOperationException($"未找到课程「{course.CourseName}」的开始学习按钮");
        
        var newPage = await WaitForNewPageAsync(timeout: 12000);

        _page = newPage ?? throw new InvalidOperationException("课程学习页未打开");
        await _page.WaitForNavigationAsync(new NavigationOptions
        {
            WaitUntil = [WaitUntilNavigation.Networkidle2],
            Timeout = 20000
        }).ContinueWith(_ => Task.CompletedTask);

        await Task.Delay(800);
        
        await TryCloseLearnHelperAsync();
        
        await _page.WaitForSelectorAsync("#courseware_main_menu > a",
            new WaitForSelectorOptions { Timeout = 10000 });
        await _page.ClickAsync("#courseware_main_menu > a");
        Log($"已点击课件目录菜单");
        
        await WaitForLearnMenuAsync();

        Log($"已进入课程学习页: {course.CourseName}");
    }
    
    private async Task WaitForLearnMenuAsync()
    {
        var deadline = DateTime.Now.AddSeconds(20);
        while (DateTime.Now < deadline)
        {
            try
            {
                var mcHandle = await _page!.QuerySelectorAsync("#mainContent");
                if (mcHandle != null)
                {
                    var mcFrame = await mcHandle.ContentFrameAsync();
                    if (mcFrame != null)
                    {
                        var hasMenu = await mcFrame.EvaluateFunctionAsync<bool>(
                            "() => !!document.querySelector('#learnMenu')");
                        if (hasMenu)
                        {
                            Log("课件目录已加载");
                            return;
                        }
                    }
                }
            }
            catch { }

            await Task.Delay(600);
        }

        Log("等待课件目录超时，继续尝试...");
    }
    
    private async Task ClickCourseItemInMenuAsync(CourseItem item, string chapterId, string lessonId,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        
        await TryCloseLearnHelperAsync();

        var mcHandle = await _page!.QuerySelectorAsync("#mainContent");
        var mcFrame = mcHandle != null ? await mcHandle.ContentFrameAsync() : null;
        if (mcFrame == null)
        {
            Log($"  未找到课件目录 iframe，跳过点击");
            return;
        }

        var domId = item.ItemId;
        
        if (!string.IsNullOrEmpty(chapterId))
        {
            await mcFrame.EvaluateFunctionAsync<bool>($@"() => {{
                const ch = document.getElementById('{chapterId}');
                const list = ch?.nextElementSibling;
                if (list?.classList.contains('s_sectionlist'))
                    list.style.removeProperty('display');
                return true;
            }}");
        }
        
        if (!string.IsNullOrEmpty(lessonId))
        {
            await mcFrame.EvaluateFunctionAsync<bool>($@"() => {{
                const sec = document.getElementById('{lessonId}');
                const wrap = sec?.nextElementSibling;
                if (wrap?.classList.contains('s_sectionwrap'))
                    wrap.style.removeProperty('display');
                return true;
            }}");
        }
        
        var ptEl = await mcFrame.QuerySelectorAsync($"#{domId}");
        if (ptEl == null)
        {
            Log($"  未找到课件节点 {domId}，跳过");
            return;
        }

        await ptEl.ScrollIntoViewAsync();
        await ptEl.ClickAsync();

        await Task.Delay(1500, ct);
        Log($"  已点击: {item.ItemName}");
    }

    private async Task TryCloseLearnHelperAsync()
    {
        try
        {
            var hasHelper = await _page!.EvaluateFunctionAsync<bool>(
                "() => !!document.querySelector('#learn-helper-main')");
            if (!hasHelper) return;

            var helperIframeHandle = await _page.QuerySelectorAsync("#learnHelperIframe");
            var helperFrame = helperIframeHandle != null
                ? await helperIframeHandle.ContentFrameAsync()
                : null;
            if (helperFrame != null)
                await helperFrame.ClickAsync("#helper-page > div > div.helper-head > a > i");
            await Task.Delay(600);
        }
        catch
        {
        }
    }

    private static string ExtractCourseId(string entryUrl)
    {
        if (string.IsNullOrEmpty(entryUrl)) return string.Empty;
        var parts = entryUrl.Split("courseId=");
        return parts.Length > 1 ? parts[1].Split('&')[0] : string.Empty;
    }

    private async Task WatchVideoAsync(IFrame? mainFrame, bool mute, CancellationToken ct)
    {
        Log("等待视频播放完成...");
        try
        {
            if (mainFrame == null)
            {
                Log("  无法获取播放页面，跳过");
                return;
            }
            
            try
            {
                await mainFrame.WaitForSelectorAsync("#container_media > video",
                    new WaitForSelectorOptions { Timeout = 15000 });
            }
            catch
            {
                Log("  等待视频元素超时，跳过");
                return;
            }
            
            if (mute)
            {
                try
                {
                    var isMuted = await mainFrame.EvaluateFunctionAsync<bool>(
                        "() => { const v = document.querySelector('#container_media > video'); return v ? v.muted : false; }");
                    if (!isMuted)
                    {
                        await mainFrame.EvaluateFunctionAsync(
                            "()=>document.querySelector('#container_media > video').muted=true");
                        Log("  已静音");
                        await Task.Delay(300);
                    }
                }
                catch
                {
                }
            }
            
            try
            {
                await mainFrame.WaitForSelectorAsync("#player_pause_btn > a",
                    new WaitForSelectorOptions { Timeout = 8000 });
                
                var needClick = await mainFrame.EvaluateFunctionAsync<bool>(@"() => {
                    const icon = document.querySelector('#player_pause_btn > a > i');
                    return icon && icon.classList.contains('icon-play-sp-fill');
                }");
                if (needClick)
                {
                    await mainFrame.ClickAsync("#player_pause_btn > a");
                    Log("  已点击播放");
                    await Task.Delay(500);
                }
            }
            catch
            {
            }
            if (mute)
            {
                try
                {
                    var isMuted = await mainFrame.EvaluateFunctionAsync<bool>(
                        "() => { const v = document.querySelector('#container_media > video'); return v ? v.muted : false; }");
                    if (!isMuted)
                    {
                        await mainFrame.EvaluateFunctionAsync(
                            "()=>document.querySelector('#container_media > video').muted=true");
                        Log("  已静音");
                        await Task.Delay(300);
                    }
                }
                catch
                {
                }
            }
            var timeout = TimeSpan.FromHours(2);
            var deadline = DateTime.Now + timeout;
            Log("  开始轮询视频状态...");
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                try
                {
                    var state = await mainFrame.EvaluateFunctionAsync<string>(@"() => {
                        const v = document.querySelector('#container_media > video');
                        if (!v) return 'missing';
                        if (v.ended) return 'ended';
                        if (v.paused) return 'paused';
                        return 'playing';
                    }");

                    if (state == "ended")
                    {
                        Log("视频播放完成");
                        return;
                    }

                    if (state == "paused" || state == "missing")
                    {
                        var needClick = await mainFrame.EvaluateFunctionAsync<bool>(@"() => {
                            const icon = document.querySelector('#player_pause_btn > a > i');
                            return icon && icon.classList.contains('icon-play-sp-fill');
                        }");
                        if (needClick)
                        {
                            await mainFrame.ClickAsync("#player_pause_btn > a");
                            Log("  检测到暂停，已恢复播放");
                        }
                    }

                    if (mute)
                    {
                        try
                        {
                            var isMuted = await mainFrame.EvaluateFunctionAsync<bool>(
                                "() => { const v = document.querySelector('#container_media > video'); return v ? v.muted : false; }");
                            if (!isMuted)
                            {
                                await mainFrame.EvaluateFunctionAsync(
                                    "()=>document.querySelector('#container_media > video').muted=true");
                                Log("  已静音");
                                await Task.Delay(300);
                            }
                        }
                        catch
                        {
                        }
                    }
                    else
                    {
                        try
                        {
                            var isMuted = await mainFrame.EvaluateFunctionAsync<bool>(
                                "() => { const v = document.querySelector('#container_media > video'); return v ? v.muted : false; }");
                            if (isMuted)
                            {
                                await mainFrame.EvaluateFunctionAsync(
                                    "()=>document.querySelector('#container_media > video').muted=false");
                                Log("  已取消静音");
                                await Task.Delay(300, ct);
                            }
                        }
                        catch
                        {
                        }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch
                {
                }
            }

            Log("视频等待超时，继续下一项");
        }
        catch (Exception ex)
        {
            Log($"视频等待超时或异常: {ex.Message}，继续下一项");
        }
    }

    private async Task WatchVideo2Async(IFrame? mainFrame, bool mute, CancellationToken ct)
    {
        Log("等待视频播放完成 (Video2)...");
        try
        {
            if (mainFrame == null)
            {
                Log("  无法获取播放页面，跳过");
                return;
            }

            try
            {
                await mainFrame.WaitForSelectorAsync("video",
                    new WaitForSelectorOptions { Timeout = 15000 });
            }
            catch
            {
                Log("  等待视频元素超时，跳过");
                return;
            }

            // Mute if needed
            if (mute)
            {
                try
                {
                    await mainFrame.EvaluateFunctionAsync(
                        "()=>{ const v = document.querySelector('video'); if(v) v.muted=true; }");
                    Log("  已静音");
                    await Task.Delay(300);
                }
                catch { }
            }

            // Try to click play button or start video via JS
            try
            {
                var started = await mainFrame.EvaluateFunctionAsync<bool>(@"() => {
                    const v = document.querySelector('video');
                    if (!v) return false;
                    if (!v.paused) return true;
                    // Try clicking custom play button first
                    const btn = document.querySelector('.prism-play-btn') ||
                                document.querySelector('.vjs-big-play-button') ||
                                document.querySelector('[class*=play-btn]') ||
                                document.querySelector('[class*=playBtn]');
                    if (btn) { btn.click(); return true; }
                    // Fallback to video.play()
                    v.play();
                    return true;
                }");
                if (started) Log("  已开始播放");
            }
            catch { }

            // Re-apply mute after play starts
            if (mute)
            {
                try
                {
                    await Task.Delay(500);
                    await mainFrame.EvaluateFunctionAsync(
                        "()=>{ const v = document.querySelector('video'); if(v) v.muted=true; }");
                }
                catch { }
            }

            var timeout = TimeSpan.FromHours(2);
            var deadline = DateTime.Now + timeout;
            Log("  开始轮询视频状态...");
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(5000, ct);

                try
                {
                    var state = await mainFrame.EvaluateFunctionAsync<string>(@"() => {
                        const v = document.querySelector('video');
                        if (!v) return 'missing';
                        if (v.ended) return 'ended';
                        if (v.paused) return 'paused';
                        return 'playing';
                    }");

                    if (state == "ended")
                    {
                        Log("视频播放完成");
                        return;
                    }

                    if (state == "paused" || state == "missing")
                    {
                        await mainFrame.EvaluateFunctionAsync(@"() => {
                            const v = document.querySelector('video');
                            if (v && v.paused && !v.ended) v.play();
                        }");
                        Log("  检测到暂停，已恢复播放");
                    }

                    // Maintain mute state
                    if (mute)
                    {
                        try
                        {
                            await mainFrame.EvaluateFunctionAsync(
                                "()=>{ const v = document.querySelector('video'); if(v && !v.muted) v.muted=true; }");
                        }
                        catch { }
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }

            Log("视频等待超时，继续下一项");
        }
        catch (Exception ex)
        {
            Log($"视频等待超时或异常: {ex.Message}，继续下一项");
        }
    }

    private async Task DoQuizAsync(IFrame? mainFrame, string courseName, string courseId, string itemId,
        CancellationToken ct)
    {
        Log("正在处理自测...");
        try
        {
            if (mainFrame == null)
            {
                Log("  未找到自测页面，跳过");
                return;
            }

            if (_ai == null)
            {
                Log("  未配置 AI 接口，跳过自测");
                return;
            }

            var clicked = await mainFrame.EvaluateFunctionAsync<bool>(@"() => {
                const btns = document.querySelectorAll('.record_submit div');
                for (const b of btns) {
                    const t = b.textContent.trim();
                    if (t === '进入测试' || t === '再做一遍') { b.click(); return true; }
                }
                return false;
            }");
            if (!clicked)
            {
                var viewAnswer = await mainFrame.EvaluateFunctionAsync<bool>(@"() => {
                    const btns = document.querySelectorAll('.record_submit div');
                    for (const b of btns) {
                        const t = b.textContent.trim();
                        if (t === '查看答案') { return true; }
                    }
                    return false;
                }");
                if (viewAnswer)
                {
                    Log("  该自测已查看答案无法重做");
                    return;
                }

                Log("  未找到测试按钮，跳过");
                return;
            }

            Log("  已点击进入测试");
            
            IFrame? examFrame = null;
            var deadline = DateTime.Now.AddSeconds(20);
            while (DateTime.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var mcHandle = await _page!.QuerySelectorAsync("#mainContent");
                    var mcFrame = mcHandle != null ? await mcHandle.ContentFrameAsync() : null;
                    if (mcFrame != null)
                    {
                        var mfHandle = await mcFrame.QuerySelectorAsync("#mainFrame");
                        var mfFrame = mfHandle != null ? await mfHandle.ContentFrameAsync() : null;
                        if (mfFrame != null)
                        {
                            var hasExam = await mfFrame.EvaluateFunctionAsync<bool>(
                                "() => !!document.querySelector('.bank_test')");
                            if (hasExam)
                            {
                                examFrame = mfFrame;
                                break;
                            }
                        }
                    }
                }
                catch { }

                await Task.Delay(600, ct);
            }

            if (examFrame == null)
            {
                Log("  等待考试页超时，跳过");
                return;
            }

            Log("  考试页已加载");
            
            var questionsJson = await examFrame.EvaluateFunctionAsync<string>(@"() => {
                const questions = [];
                let currentType = 'single';
                const container = document.querySelector(
                    '#mCSB_1_container > div > div.pageWidth.bank > div.bank_cont > div');
                if (!container) return '[]';

                let idx = 0;
                for (const el of container.children) {
                    if (el.classList.contains('test_item_type')) {
                        const t = el.querySelector('span')?.textContent?.trim() ?? '';
                        if (t === '多选题') currentType = 'multiple';
                        else if (t === '判断题') currentType = 'judge';
                        else currentType = 'single';
                        continue;
                    }
                    if (!el.classList.contains('test_item')) continue;

                    const title = el.querySelector('.test_item_tit')?.textContent
                        ?.replace(/未做答/g, '').trim() ?? '';
                    const opts = [];
                    el.querySelectorAll('input').forEach(inp => {
                        const label = inp.closest('label');
                        const text  = label ? label.textContent.trim() : '';
                        opts.push({ value: inp.value, text });
                    });
                    questions.push({ index: String(idx), type: currentType, title, options: opts });
                    idx++;
                }
                return JSON.stringify(questions);
            }");

            Log(
                $"  解析到 {System.Text.Json.JsonDocument.Parse(questionsJson).RootElement.GetArrayLength()} 道题，正在请求 AI 作答...");
            
            Dictionary<string, string> answers;
            try
            {
                answers = await _ai.AnswerQuizAsync(questionsJson, courseName);
                Log($"  AI 返回 {answers.Count} 个答案");
            }
            catch (Exception ex)
            {
                Log($"  AI 请求失败: {ex.Message}，跳过提交");
                return;
            }
            
            await examFrame.EvaluateFunctionAsync<bool>($@"() => {{
                const answers = {System.Text.Json.JsonSerializer.Serialize(answers)};
                let idx = 0;
                const container = document.querySelector(
                    '#mCSB_1_container > div > div.pageWidth.bank > div.bank_cont > div');
                if (!container) return false;

                for (const el of container.children) {{
                    if (!el.classList.contains('test_item')) continue;
                    const ans = answers[String(idx)];
                    if (!ans) {{ idx++; continue; }}

                    el.querySelectorAll('input').forEach(inp => {{
                        if (inp.type === 'radio' && inp.value === ans) inp.click();
                        if (inp.type === 'checkbox' && ans.includes(inp.value)) inp.click();
                    }});
                    idx++;
                }}
                return true;
            }}");
            Log("  已填写答案");
            var questions = System.Text.Json.JsonSerializer.Deserialize<List<QuizQuestion>>(questionsJson) ?? [];
            foreach (var q in questions)
            {
                if (answers.TryGetValue(q.Index, out var ans))
                {
                    Log($"    题目{int.Parse(q.Index) + 1}: {q.Title}");
                    Log($"      答案: {ans}");
                }
            }

            await Task.Delay(500, ct);
            
            var submitted = await examFrame.EvaluateFunctionAsync<bool>(@"() => {
                const btn = document.querySelector(
                    '#mCSB_1_container > div > div.pageWidth.bank > div.bank_cont > div > div.bank_test_bar > div');
                if (btn) { btn.click(); return true; }
                return false;
            }");
            Log(submitted ? "  已提交自测" : "  未找到提交按钮");

            if (submitted)
            {
                var scoreDeadline = DateTime.Now.AddSeconds(20);
                string? score = null;
                while (DateTime.Now < scoreDeadline && score == null)
                {
                    ct.ThrowIfCancellationRequested();
                    await Task.Delay(800, ct);
                    try
                    {
                        var mcHandle = await _page!.QuerySelectorAsync("#mainContent");
                        var mcFrame = mcHandle != null ? await mcHandle.ContentFrameAsync() : null;
                        if (mcFrame != null)
                        {
                            var mfHandle = await mcFrame.QuerySelectorAsync("#mainFrame");
                            if (mfHandle != null)
                            {
                                var mfFrame = await mfHandle.ContentFrameAsync();
                                score = await mfFrame.EvaluateFunctionAsync<string>(@"() => {
                                const el = document.querySelector(
                                    '#mCSB_1_container > div > div.mainIn > div > div > div.record_stat > div.record_stat_left > div:nth-child(1) > span');
                                return el ? el.textContent.trim() : null;
                            }");
                            }

                        }
                    }
                    catch { }
                }

                if (!string.IsNullOrEmpty(score))
                {
                    Log($"  自测成绩: {score}");
                    OnQuizScored?.Invoke(courseId, itemId, score);
                }
                else
                {
                    Log("  未能读取成绩");
                }
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"  自测处理失败: {ex.Message}");
        }
    }

    public string GetCurrentUrl() => _page?.Url ?? string.Empty;

    private void EnsurePage()
    {
        if (_page == null || _browser == null || _browser.IsClosed)
            throw new InvalidOperationException("浏览器未打开，请先点击「打开浏览器」");
    }

    public async ValueTask DisposeAsync()
    {
        StopAlertWatcher();
        if (_browser != null)
            await _browser.DisposeAsync();
    }

    private class QuizQuestion
    {
        [System.Text.Json.Serialization.JsonPropertyName("index")]
        public string Index { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "single";
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("options")]
        public List<QuizOption> Options { get; set; } = [];
    }

    private class QuizOption
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string Value { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }
}