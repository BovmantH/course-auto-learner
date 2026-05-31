using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CourseAutoLearner.Models;

namespace CourseAutoLearner.ViewModels;

public partial class CourseViewModel : ObservableObject
{
    private readonly Course _course;
    private bool _updating = false;

    public CourseViewModel(Course course)
    {
        _course = course;
        LessonViewModels = course.Chapters
            .SelectMany(ch => ch.Lessons.Select(l => new LessonViewModel(l)))
            .ToList();

        ChapterLessons = course.Chapters.Select(ch => new ChapterLessonsViewModel(
            ch,
            LessonViewModels.Where(lvm => ch.Lessons.Any(l => l.LessonId == lvm.LessonId)).ToList()
        )).ToList();

        foreach (var ch in ChapterLessons)
            ch.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(ChapterLessonsViewModel.IsSelected) && !_updating)
                    SyncFromChapters();
            };
    }

    public string CourseId => _course.CourseId;
    public string CourseName => _course.CourseName;
    public string Semester => _course.Semester;
    public string CourseType => _course.CourseType;
    public string TeachType => _course.TeachType;
    public bool IsCompleted => _course.IsCompleted;
    public DateTime? CompletedAt => _course.CompletedAt;
    public List<Chapter> Chapters => _course.Chapters;
    public CourseStatus Status => _course.Status;

    [ObservableProperty] private bool _isSelected = false;

    [ObservableProperty] private bool _showCheckbox = false;

    public List<LessonViewModel> LessonViewModels { get; }
    public List<ChapterLessonsViewModel> ChapterLessons { get; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_updating) return;
        _updating = true;
        foreach (var ch in ChapterLessons)
            ch.IsSelected = value;
        _updating = false;
    }

    private void SyncFromChapters()
    {
        _updating = true;
        IsSelected = ChapterLessons.Count > 0 && ChapterLessons.All(c => c.IsSelected);
        _updating = false;
    }

    public void ApplyLearnMode(LearnMode mode)
    {
        foreach (var ch in ChapterLessons)
            ch.ApplyLearnMode(mode);
        SyncFromChapters();
    }

    public void SetShowCheckbox(bool show)
    {
        ShowCheckbox = show;
        foreach (var ch in ChapterLessons)
            ch.SetShowCheckbox(show);
    }

    public string ActionText => !string.IsNullOrEmpty(_course.ActionText)
        ? _course.ActionText
        : _course.Status switch
        {
            CourseStatus.Available => "开始学习",
            CourseStatus.Offline => "面授学习",
            _ => "暂未开课"
        };

    public bool CanLearn => _course.Status == CourseStatus.Available;

    public string StatusText => _course.Status switch
    {
        CourseStatus.Available when IsCompleted => "已完成",
        CourseStatus.Available => "未完成",
        CourseStatus.Offline => "面授",
        _ => "未开课"
    };

    public IBrush StatusBrush => _course.Status switch
    {
        CourseStatus.Available when IsCompleted => new SolidColorBrush(Color.FromRgb(22, 163, 74)),
        CourseStatus.Available => new SolidColorBrush(Color.FromRgb(220, 38, 38)),
        CourseStatus.Offline => new SolidColorBrush(Color.FromRgb(100, 116, 139)),
        _ => new SolidColorBrush(Color.FromRgb(148, 163, 184))
    };

    public string CompletedAtText => CompletedAt.HasValue
        ? CompletedAt.Value.ToString("yyyy-MM-dd HH:mm")
        : "-";

    public int TotalItems => _course.Chapters
        .SelectMany(c => c.Lessons).SelectMany(l => l.Items).Count();
    public int LearnedItems => _course.Chapters
        .SelectMany(c => c.Lessons).SelectMany(l => l.Items).Count(i => i.IsLearned);
    public string ProgressText => $"{LearnedItems}/{TotalItems}";

    public Course Model => _course;
}