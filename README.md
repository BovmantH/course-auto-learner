# 苏大学习助手

苏州大学在线课程自动化学习工具。自动播放课程视频、自动答题，支持 Windows 和 macOS。

## 功能

- 🔐 扫码登录（微信扫码）
- 📚 自动读取课程列表和章节结构
- 🎬 自动播放课程视频（自动恢复暂停、30 分钟续学提醒）
- 📝 AI 自动答题（支持单选、多选、判断题）
- 🎯 支持按课程、章节、类型筛选学习
- ✅ 学习进度追踪和成绩记录

## 下载

前往 [Releases](https://github.com/BovmantH/course-auto-learner/releases) 页面下载：

- **Windows**: 下载 `.zip` 解压即用
- **macOS**: 下载 `.dmg` 安装包

## 使用方法

1. 下载并安装对应平台的版本
2. 打开应用，点击「打开浏览器」
3. 在弹出的浏览器中用微信扫码登录课程平台
4. 点击「读取课程」获取课程列表
5. 选择课程后点击「开始学习」

### AI 答题配置

点击工具栏「⚙ 设置」，填入 AI 接口信息：

- **接口地址**: 如 `https://api.deepseek.com/v1`
- **API Key**: 你的 API 密钥
- **模型名称**: 如 `deepseek-chat`

支持所有 OpenAI 兼容接口，包括 DeepSeek、Kimi、小米大模型等国产模型。

未配置接口时，自测题目将跳过不作答。

## 技术架构

```
┌─────────────────────────────────────────────────────────┐
│                      UI 层 (Avalonia)                    │
│  MainWindow.axaml ─ SettingsWindow.axaml ─ App.axaml    │
├─────────────────────────────────────────────────────────┤
│                   ViewModel 层 (MVVM)                    │
│  MainViewModel ─ CourseViewModel ─ LessonViewModel ...  │
├──────────────────┬──────────────────┬───────────────────┤
│  BrowserService  │    AiService     │  DatabaseService  │
│  (浏览器自动化)    │   (AI 答题)       │   (数据持久化)     │
├──────────────────┼──────────────────┼───────────────────┤
│  PuppeteerSharp  │  OpenAI 兼容 API  │      LiteDB       │
│  Chrome/Chromium │  DeepSeek/Kimi.. │    嵌入式 NoSQL    │
└──────────────────┴──────────────────┴───────────────────┘
```

## 技术栈

| 层级 | 技术 | 说明 |
|------|------|------|
| **跨平台 UI** | [Avalonia UI](https://avaloniaui.net/) 11.2 | 跨平台 UI 框架，支持 Windows / macOS / Linux，使用 Fluent 主题 |
| **MVVM 框架** | [CommunityToolkit.Mvvm](https://github.com/CommunityToolkit/dotnet) 8.4 | 源生成器驱动的 MVVM，`[ObservableProperty]` / `[RelayCommand]` |
| **浏览器自动化** | [PuppeteerSharp](https://hardkoded/puppeteer-sharp) 24.40 | 控制 Chrome/Chromium，处理 iframe 嵌套、视频播放、表单提交 |
| **嵌入式数据库** | [LiteDB](https://github.com/mbdavid/LiteDB) 5.0 | 无服务器 NoSQL，本地存储课程/进度/成绩 |
| **AI 接口** | OpenAI 兼容 Chat Completions API | 支持 DeepSeek、Kimi、通义千问、智谱、豆包、小米 MiMo 等 |
| **运行时** | .NET 8+ | 自包含发布，无需安装运行时 |

## 从源码构建

需要 .NET 8+ SDK：

```bash
git clone https://github.com/BovmantH/course-auto-learner.git
cd course-auto-learner
dotnet build
dotnet run
```

发布：

```bash
# Windows
dotnet publish -c Release -r win-x64 --self-contained -o publish/win-x64

# macOS
dotnet publish -c Release -r osx-x64 --self-contained -o publish/osx-x64
```

## 开源许可

MIT License - Copyright (c) Bovmant.H
