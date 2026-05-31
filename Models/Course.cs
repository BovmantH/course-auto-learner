using LiteDB;

namespace CourseAutoLearner.Models;

public class Course
{
    [BsonId]
    public ObjectId Id { get; set; } = ObjectId.NewObjectId();

    public string CourseId { get; set; } = string.Empty;
    public string CourseName { get; set; } = string.Empty;
    public string Semester { get; set; } = string.Empty;
    public string CourseType { get; set; } = string.Empty;
    public string TeachType { get; set; } = string.Empty;
    public CourseStatus Status { get; set; } = CourseStatus.NotStarted;
    public string ActionText { get; set; } = string.Empty;
    public string EntryUrl { get; set; } = string.Empty;
    public bool IsCompleted { get; set; } = false;
    public DateTime? CompletedAt { get; set; }
    public List<Chapter> Chapters { get; set; } = [];
}

public enum CourseStatus
{
    Available,

    NotStarted,

    Offline,
}

public class Chapter
{
    public string ChapterId { get; set; } = string.Empty;
    public string ChapterName { get; set; } = string.Empty;
    public List<Lesson> Lessons { get; set; } = [];
}

public class Lesson
{
    public string LessonId { get; set; } = string.Empty;
    public string LessonName { get; set; } = string.Empty;
    public List<CourseItem> Items { get; set; } = [];
}

public class CourseItem
{
    public string ItemId { get; set; } = string.Empty;
    public string ItemName { get; set; } = string.Empty;
    public CourseItemType Type { get; set; }
    public bool IsLearned { get; set; } = false;
    public DateTime? LearnedAt { get; set; }
    public string Url { get; set; } = string.Empty;
    public string? QuizScore { get; set; }
    public DateTime? QuizScoredAt { get; set; }
}

public enum CourseItemType
{
    Video,
    Quiz
}

public enum LearnMode
{
    All,
    VideoOnly,
    QuizOnly
}