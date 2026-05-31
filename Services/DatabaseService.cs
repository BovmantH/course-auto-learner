using LiteDB;
using CourseAutoLearner.Models;

namespace CourseAutoLearner.Services;

public class DatabaseService : IDisposable
{
    private readonly LiteDatabase _db;
    private readonly ILiteCollection<Course> _courses;

    public DatabaseService(string dbPath = "courses.db")
    {
        _db = new LiteDatabase(dbPath);
        _courses = _db.GetCollection<Course>("courses");
        _courses.EnsureIndex(x => x.CourseId, unique: true);
    }

    public List<Course> GetAllCourses() => _courses.FindAll().ToList();

    public List<Course> GetLearnedCourses() =>
        _courses.Find(x => x.IsCompleted).ToList();

    public List<Course> GetUnlearnedCourses() =>
        _courses.Find(x => !x.IsCompleted).ToList();

    public Course? GetCourse(string courseId) =>
        _courses.FindOne(x => x.CourseId == courseId);

    public void UpsertCourse(Course course) => _courses.Upsert(course);

    public void MarkItemLearned(string courseId, string itemId)
    {
        var course = GetCourse(courseId);
        if (course == null) return;

        foreach (var chapter in course.Chapters)
        foreach (var lesson in chapter.Lessons)
        {
            var item = lesson.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (item != null)
            {
                item.IsLearned = true;
                item.LearnedAt = DateTime.Now;
            }
        }
        
        var allItems = course.Chapters
            .SelectMany(c => c.Lessons)
            .SelectMany(l => l.Items)
            .ToList();

        if (allItems.Count > 0 && allItems.All(i => i.IsLearned))
        {
            course.IsCompleted = true;
            course.CompletedAt = DateTime.Now;
        }

        _courses.Update(course);
    }

    public void SaveQuizScore(string courseId, string itemId, string score)
    {
        var course = GetCourse(courseId);
        if (course == null) return;
        foreach (var chapter in course.Chapters)
        foreach (var lesson in chapter.Lessons)
        {
            var item = lesson.Items.FirstOrDefault(i => i.ItemId == itemId);
            if (item != null)
            {
                item.QuizScore = score;
                item.QuizScoredAt = DateTime.Now;
            }
        }

        _courses.Update(course);
    }

    public void DeleteCourse(string courseId)
    {
        var course = GetCourse(courseId);
        if (course != null) _courses.Delete(course.Id);
    }

    public void Dispose() => _db.Dispose();
}