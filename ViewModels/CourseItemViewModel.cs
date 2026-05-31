using CommunityToolkit.Mvvm.ComponentModel;
using CourseAutoLearner.Models;

namespace CourseAutoLearner.ViewModels;

public partial class CourseItemViewModel(CourseItem item) : ObservableObject
{
    public CourseItem Model { get; } = item;

    public string ItemId => Model.ItemId;
    public string ItemName => Model.ItemName;
    public CourseItemType Type => Model.Type;
    public bool IsLearned => Model.IsLearned;
    public string? QuizScore => Model.QuizScore;
    public DateTime? QuizScoredAt => Model.QuizScoredAt;

    [ObservableProperty] private bool _isSelected = false;
    [ObservableProperty] private bool _isSelectable = true;
    [ObservableProperty] private bool _showCheckbox = false;
}