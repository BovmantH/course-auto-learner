using CommunityToolkit.Mvvm.ComponentModel;
using CourseAutoLearner.Models;

namespace CourseAutoLearner.ViewModels;

public partial class LessonViewModel : ObservableObject
{
    public Lesson Model { get; }
    public string LessonId => Model.LessonId;
    public string LessonName => Model.LessonName;
    public List<CourseItemViewModel> ItemViewModels { get; }

    [ObservableProperty] private bool _isSelected = false;
    [ObservableProperty] private bool _showCheckbox = false;

    private bool _updating = false;

    public LessonViewModel(Lesson lesson)
    {
        Model = lesson;
        ItemViewModels = lesson.Items.Select(i => new CourseItemViewModel(i)).ToList();

        foreach (var item in ItemViewModels)
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CourseItemViewModel.IsSelected) && !_updating)
                    SyncFromItems();
            };
    }

    partial void OnIsSelectedChanged(bool value)
    {
        if (_updating) return;
        _updating = true;
        foreach (var item in ItemViewModels.Where(i => i.IsSelectable))
            item.IsSelected = value;
        _updating = false;
    }

    private void SyncFromItems()
    {
        _updating = true;
        var selectable = ItemViewModels.Where(i => i.IsSelectable).ToList();
        IsSelected = selectable.Count > 0 && selectable.All(i => i.IsSelected);
        _updating = false;
    }

    public void ApplyLearnMode(Models.LearnMode mode)
    {
        foreach (var item in ItemViewModels)
        {
            var allowed = mode switch
            {
                Models.LearnMode.VideoOnly => item.Type == CourseItemType.Video,
                Models.LearnMode.QuizOnly => item.Type == CourseItemType.Quiz,
                _ => true
            };
            item.IsSelectable = allowed;
            if (!allowed) item.IsSelected = false;
        }

        SyncFromItems();
    }

    public void SetShowCheckbox(bool show)
    {
        ShowCheckbox = show;
        foreach (var item in ItemViewModels)
            item.ShowCheckbox = show;
    }
}