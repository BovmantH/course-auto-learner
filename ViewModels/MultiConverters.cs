using System.Globalization;
using Avalonia.Data.Converters;

namespace CourseAutoLearner.ViewModels;

public class FetchingMultiConverter : IMultiValueConverter
{
    public static readonly FetchingMultiConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.FirstOrDefault() is true ? "🔄  读取中..." : "📥  读取课程";
}

public class MuteMultiConverter : IMultiValueConverter
{
    public static readonly MuteMultiConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.FirstOrDefault() is true ? "🔇 静音" : "🔊 声音";
}

public class BrowserStatusMultiConverter : IMultiValueConverter
{
    public static readonly BrowserStatusMultiConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
        => values.FirstOrDefault() is true ? "运行中" : "未启动";
}

public class QuizScoreMultiConverter : IMultiValueConverter
{
    public static readonly QuizScoreMultiConverter Instance = new();
    public object? Convert(IList<object?> values, Type targetType, object? parameter, CultureInfo culture)
    {
        var score = values.FirstOrDefault()?.ToString();
        return string.IsNullOrEmpty(score) ? "成绩: 未采集" : $"成绩: {score}";
    }
}
