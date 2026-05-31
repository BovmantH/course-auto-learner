using CommunityToolkit.Mvvm.ComponentModel;
using CourseAutoLearner.Models;

namespace CourseAutoLearner.ViewModels;

public partial class ChapterLessonsViewModel : ObservableObject
{
    public Chapter Chapter { get; }
    public string ChapterName => Chapter.ChapterName;
    public List<LessonViewModel> Lessons { get; }

    [ObservableProperty] private bool _isSelected = false;
    [ObservableProperty] private bool _showCheckbox = false;

    private bool _updating = false;

    public ChapterLessonsViewModel(Chapter chapter, List<LessonViewModel> lessons)
    {
        Chapter = chapter;
        Lessons = lessons;

        foreach (var lesson in Lessons)
            lesson.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(LessonViewModel.IsSelected) && !_updating)
                    SyncFromLessons();
            };
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_updating) return;
        _updating = true;
        foreach (var lesson in Lessons)
            lesson.IsSelected = value;
        _updating = false;
    }

    private void SyncFromLessons()
    {
        _updating = true;
        IsSelected = Lessons.Count > 0 && Lessons.All(l => l.IsSelected);
        _updating = false;
    }

    public void ApplyLearnMode(Models.LearnMode mode)
    {
        foreach (var lesson in Lessons)
            lesson.ApplyLearnMode(mode);
        SyncFromLessons();
    }

    public void SetShowCheckbox(bool show)
    {
        ShowCheckbox = show;
        foreach (var lesson in Lessons)
            lesson.SetShowCheckbox(show);
    }
}