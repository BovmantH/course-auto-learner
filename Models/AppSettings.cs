using System.IO;
using System.Text.Json;

namespace CourseAutoLearner.Models;

public class AppSettings
{
    public string AiBaseUrl { get; set; } = string.Empty;
    public string AiApiKey { get; set; } = string.Empty;
    public string AiModel { get; set; } = "deepseek-chat";
    public string? ChromePath { get; set; }

    public bool IsAiConfigured =>
        !string.IsNullOrWhiteSpace(AiBaseUrl) && !string.IsNullOrWhiteSpace(AiApiKey);

    private readonly static string FilePath = Path.Combine(
        AppContext.BaseDirectory, "appsettings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new();
        }
        catch { }

        return new();
    }

    public void Save()
    {
        File.WriteAllText(FilePath,
            JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}