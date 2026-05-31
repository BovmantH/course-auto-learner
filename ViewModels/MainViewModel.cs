using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CourseAutoLearner.Services;
using CourseAutoLearner.ViewModels;

namespace CourseAutoLearner.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly DatabaseService _db;
    private readonly BrowserService _browser;
    private CancellationTokenSource? _cts;
    private Models.AppSettings _settings;

    public ObservableCollection<CourseViewModel> AllCourses { get; } = [];
    public ObservableCollection<CourseViewModel> LearnedCourses { get; } = [];
    public ObservableCollection<CourseViewModel> UnlearnedCourses { get; } = [];
    public ObservableCollection<string> AvailableSemesters { get; } = [];

    [ObservableProperty] private string _logText = string.Empty;
    [ObservableProperty] private bool _isBrowserOpen = false;
    [ObservableProperty] private bool _isLearning = false;
    [ObservableProperty] private bool _isFetching = false;
    [ObservableProperty] private bool _isPartialMode = false;
    [ObservableProperty] private bool _isTopMost = false;
    [ObservableProperty] private Models.LearnMode _learnMode = Models.LearnMode.All;
    [ObservableProperty] private bool _isMuted = false;
    [ObservableProperty] private string _selectedSemester = "全部学期";

    partial void OnSelectedSemesterChanged(string value)
    {
        if (_loadingCourses) return;
        LoadCourses();
    }

    partial void OnIsPartialModeChanged(bool value)
    {
        foreach (var vm in AllCourses)
            vm.SetShowCheckbox(value);
    }

    partial void OnLearnModeChanged(Models.LearnMode value)
    {
        foreach (var vm in AllCourses)
            vm.ApplyLearnMode(value);
    }

    [ObservableProperty] private CourseViewModel? _selectedCourse;
    [ObservableProperty] private string _userName = "未登录";
    [ObservableProperty] private string _studentId = string.Empty;
    
    public List<(CourseViewModel Course, Models.CourseItem Item)> PendingLearnTasks { get; private set; } = [];

    private bool _loadingCourses = false;

    public MainViewModel() : this(Models.AppSettings.Load())
    {
    }

    public MainViewModel(Models.AppSettings settings)
    {
        _settings = settings;
        _db = new DatabaseService();
        _browser = new BrowserService(settings);
        _browser.OnLog += AppendLog;
        _browser.OnLoginSuccess += TryFetchUserInfoAsync;
        _browser.OnCourseChaptersFetched += SyncCourseToDbAsync;
        _browser.OnQuizScored += (courseId, itemId, score) =>
        {
            _db.SaveQuizScore(courseId, itemId, score);
            AppendLog($"  📊 成绩已记录: {score}");
        };
        _browser.OnBrowserClosed += () =>
        {
            Dispatcher.UIThread.InvokeAsync(() =>
            {
                IsBrowserOpen = false;
                _cts?.Cancel();
                AppendLog("浏览器已关闭");
            });
        };
        LoadCourses();
    }

    public void OnSettingsChanged(Models.AppSettings settings)
    {
        _settings = settings;
        _browser.UpdateSettings(settings);
    }

    private void LoadCourses()
    {
        _loadingCourses = true;
        try
        {
            var prevSelectedId = SelectedCourse?.CourseId;

            AllCourses.Clear();
            LearnedCourses.Clear();
            UnlearnedCourses.Clear();

            var all = _db.GetAllCourses();
            
            var newSemesters = all.Select(c => c.Semester)
                .Where(s => !string.IsNullOrEmpty(s))
                .Distinct()
                .OrderBy(s => s)
                .ToList();
            var expected = new[] { "全部学期" }.Concat(newSemesters).ToList();
            if (!expected.SequenceEqual(AvailableSemesters))
            {
                AvailableSemesters.Clear();
                foreach (var s in expected) AvailableSemesters.Add(s);
            }

            if (!AvailableSemesters.Contains(SelectedSemester))
                SelectedSemester = "全部学期";
            var filtered = SelectedSemester == "全部学期"
                ? all
                : all.Where(c => c.Semester == SelectedSemester).ToList();

            foreach (var c in filtered)
            {
                var vm = new CourseViewModel(c);
                vm.SetShowCheckbox(IsPartialMode);
                vm.ApplyLearnMode(LearnMode);
                AllCourses.Add(vm);
                if (c.IsCompleted)
                    LearnedCourses.Add(vm);
                else if (c.Status == Models.CourseStatus.Available)
                    UnlearnedCourses.Add(vm);
            }
            if (prevSelectedId != null)
                SelectedCourse = AllCourses.FirstOrDefault(v => v.CourseId == prevSelectedId);
        }
        finally
        {
            _loadingCourses = false;
        }
    }

    [RelayCommand]
    private async Task OpenBrowserAsync()
    {
        try
        {
            await _browser.OpenBrowserAsync();
            IsBrowserOpen = true;
            // await TryFetchUserInfoAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"打开浏览器失败: {ex.Message}");
        }
    }

    private async Task TryFetchUserInfoAsync()
    {
        try
        {
            var (name, sid) = await _browser.FetchUserInfoAsync();
            if (!string.IsNullOrEmpty(name) || !string.IsNullOrEmpty(sid))
            {
                UserName = name;
                StudentId = sid;
                AppendLog($"已识别用户：{name}  {sid}");
            }

            // 只有数据库没有课程时才自动拉取，否则等用户手动点击「读取课程」
            // if (_db.GetAllCourses().Count == 0)
            //     await _browser.NavigateToCourseListAsync();
        }
        catch
        {
        }
    }

    private readonly SemaphoreSlim _dbLock = new(1, 1);

    private async Task SyncCourseToDbAsync(Models.Course course)
    {
        await _dbLock.WaitAsync();
        try
        {
            var existing = _db.GetCourse(course.CourseId);
            if (existing == null)
            {
                _db.UpsertCourse(course);
            }
            else
            {
                bool statusChanged = existing.Status != course.Status;
                if (statusChanged)
                    AppendLog($"课程状态更新：{course.CourseName} [{existing.ActionText} → {course.ActionText}]");
                
                var existingItems = existing.Chapters
                    .SelectMany(c => c.Lessons)
                    .SelectMany(l => l.Items)
                    .ToDictionary(i => i.ItemId);

                foreach (var item in course.Chapters.SelectMany(c => c.Lessons).SelectMany(l => l.Items))
                {
                    if (existingItems.TryGetValue(item.ItemId, out var old) && !item.IsLearned && old.IsLearned)
                    {
                        item.IsLearned = true;
                        item.LearnedAt = old.LearnedAt;
                    }
                }

                existing.Semester = course.Semester;
                existing.CourseType = course.CourseType;
                existing.TeachType = course.TeachType;
                existing.Status = course.Status;
                existing.ActionText = course.ActionText;
                existing.EntryUrl = course.EntryUrl;
                existing.Chapters = course.Chapters;

                var allItems = existing.Chapters.SelectMany(c => c.Lessons).SelectMany(l => l.Items).ToList();
                if (allItems.Count > 0 && allItems.All(i => i.IsLearned))
                {
                    existing.IsCompleted = true;
                    existing.CompletedAt ??= DateTime.Now;
                }

                _db.UpsertCourse(existing);
            }

            _ = Dispatcher.UIThread.InvokeAsync(LoadCourses);
        }
        finally
        {
            _dbLock.Release();
        }
    }

    [RelayCommand]
    private async Task FetchUserInfoAsync()
    {
        if (!IsBrowserOpen) return;
        await TryFetchUserInfoAsync();
    }

    [RelayCommand]
    private async Task CloseBrowserAsync()
    {
        await _browser.CloseBrowserAsync();
        IsBrowserOpen = false;
    }

    [RelayCommand]
    private async Task FetchCoursesAsync()
    {
        if (!IsBrowserOpen)
        {
            AppendLog("请先打开浏览器并登录课程平台");
            return;
        }

        IsFetching = true;
        try
        {
            await _browser.NavigateToCourseListAsync();
        }
        catch (Exception ex)
        {
            AppendLog($"读取课程失败: {ex.Message}");
        }
        finally
        {
            IsFetching = false;
        }
    }
    
    [RelayCommand]
    private async Task StartLearningAsync()
    {
        if (!IsBrowserOpen)
        {
            AppendLog("请先打开浏览器");
            return;
        }

        PendingLearnTasks = BuildLearnTasks(targetCourse: null);
        if (PendingLearnTasks.Count == 0)
        {
            AppendLog(IsPartialMode
                ? "请先勾选要学习的课程和章节"
                : "没有未学习的课程，请先点击「读取课程」");
            return;
        }

        await ExecuteLearnTasksAsync();
    }
    
    [RelayCommand]
    private async Task StartLearningForCourseAsync(CourseViewModel? courseVm)
    {
        if (courseVm == null || !courseVm.CanLearn) return;
        if (!IsBrowserOpen)
        {
            AppendLog("请先打开浏览器");
            return;
        }

        PendingLearnTasks = BuildLearnTasksForCourse(courseVm);
        if (PendingLearnTasks.Count == 0)
        {
            AppendLog(IsPartialMode
                ? $"课程「{courseVm.CourseName}」没有勾选的未学内容，请先勾选章节"
                : $"课程「{courseVm.CourseName}」没有未学习的内容");
            return;
        }

        await ExecuteLearnTasksAsync();
    }

    private async Task ExecuteLearnTasksAsync()
    {
        IsLearning = true;
        _cts = new CancellationTokenSource();
        _browser.StartAlertWatcher();

        try
        {
            string? lastCourseId = null;
            foreach (var (courseVm, item) in PendingLearnTasks)
            {
                if (_cts.Token.IsCancellationRequested) break;

                if (courseVm.CourseId != lastCourseId)
                {
                    AppendLog($"开始学习课程: {courseVm.CourseName} ({courseVm.CourseId})");
                    lastCourseId = courseVm.CourseId;
                }

                var (chName, lsName, chId, lsId) = FindItemLocation(courseVm, item.ItemId);
                AppendLog($"  [{chName}] [{lsName}] {item.ItemName}");

                await _browser.NavigateToItemAsync(courseVm.Model, item, chId, lsId, _cts.Token);
                await _browser.LearnItemAsync(item, IsMuted, courseVm.CourseName, courseVm.CourseId, _cts.Token);
                _db.MarkItemLearned(courseVm.CourseId, item.ItemId);
            }

            AppendLog("学习任务完成");
        }
        catch (OperationCanceledException)
        {
            AppendLog("学习已停止");
        }
        catch (Exception ex)
        {
            AppendLog($"学习出错: {ex.Message}");
        }
        finally
        {
            _browser.StopAlertWatcher();
            await _browser.ReturnToHomePageAsync();
            IsLearning = false;
            LoadCourses();
        }
    }
    
    private List<(CourseViewModel Course, Models.CourseItem Item)> BuildLearnTasks(CourseViewModel? targetCourse)
    {
        var result = new List<(CourseViewModel, Models.CourseItem)>();

        if (targetCourse != null)
        {
            foreach (var item in targetCourse.Chapters.SelectMany(c => c.Lessons).SelectMany(l => l.Items))
                if (!item.IsLearned && MatchLearnMode(item))
                    result.Add((targetCourse, item));
        }
        else if (IsPartialMode)
        {
            foreach (var vm in AllCourses.Where(c => c.CanLearn))
            foreach (var ch in vm.ChapterLessons)
            foreach (var ls in ch.Lessons)
            foreach (var itemVm in ls.ItemViewModels.Where(i => i.IsSelected && i.IsSelectable && !i.IsLearned))
                result.Add((vm, itemVm.Model));
        }
        else
        {
            foreach (var vm in UnlearnedCourses)
            foreach (var item in vm.Chapters.SelectMany(c => c.Lessons).SelectMany(l => l.Items))
                if (!item.IsLearned && MatchLearnMode(item))
                    result.Add((vm, item));
        }

        return result;
    }
    
    private List<(CourseViewModel Course, Models.CourseItem Item)> BuildLearnTasksForCourse(CourseViewModel courseVm)
    {
        var result = new List<(CourseViewModel, Models.CourseItem)>();

        if (IsPartialMode)
        {
            foreach (var ch in courseVm.ChapterLessons)
            foreach (var ls in ch.Lessons)
            foreach (var itemVm in ls.ItemViewModels.Where(i => i.IsSelected && i.IsSelectable))
                result.Add((courseVm, itemVm.Model));
        }
        else
        {
            foreach (var item in courseVm.Chapters.SelectMany(c => c.Lessons).SelectMany(l => l.Items))
                if (!item.IsLearned && MatchLearnMode(item))
                    result.Add((courseVm, item));
        }

        return result;
    }
    
    private bool MatchLearnMode(Models.CourseItem item)
    {
        return LearnMode switch
        {
            Models.LearnMode.VideoOnly => item.Type == Models.CourseItemType.Video,
            Models.LearnMode.QuizOnly => item.Type == Models.CourseItemType.Quiz,
            _ => true
        };
    }

    private static (string ChName, string LsName, string ChId, string LsId) FindItemLocation(CourseViewModel vm,
        string itemId)
    {
        foreach (var ch in vm.Chapters)
        foreach (var ls in ch.Lessons)
            if (ls.Items.Any(i => i.ItemId == itemId))
                return (ch.ChapterName, ls.LessonName, ch.ChapterId, ls.LessonId);
        return ("?", "?", "", "");
    }

    [RelayCommand]
    private void StopLearning()
    {
        _cts?.Cancel();
        AppendLog("正在停止...");
    }

    private void AppendLog(string msg)
    {
        Dispatcher.UIThread.InvokeAsync(() => LogText += msg + "\n");
    }

    public void Cleanup()
    {
        _cts?.Cancel();
        _db.Dispose();
        _ = _browser.DisposeAsync();
    }
}