using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CourseAutoLearner.Models;

namespace CourseAutoLearner.Services;

public class AiService
{
    private readonly AppSettings _settings;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(180) };

    private readonly static string LogFile = Path.Combine(
        AppContext.BaseDirectory, "ai_requests.log");

    public AiService(AppSettings settings) => _settings = settings;

    public async Task<Dictionary<string, string>> AnswerQuizAsync(
        string questionsJson, string courseName = "")
    {
        var questions = JsonSerializer.Deserialize<List<QuestionItem>>(questionsJson) ?? [];
        var answers = new Dictionary<string, string>();

        foreach (var q in questions)
        {
            try
            {
                var answer = await AnswerOneAsync(q, courseName);
                answers[q.Index] = answer;
            }
            catch (Exception ex)
            {
                WriteLog($"[ERROR] 题目 {q.Index} 请求失败: {ex.Message}");
            }
        }

        return answers;
    }

    private async Task<string> AnswerOneAsync(QuestionItem q, string courseName)
    {
        var baseUrl = _settings.AiBaseUrl.TrimEnd('/');
        var url = baseUrl.EndsWith("/chat/completions")
            ? baseUrl
            : $"{baseUrl}/chat/completions";

        var subjectLine = string.IsNullOrWhiteSpace(courseName)
            ? ""
            : $"当前科目：{courseName}\n";

        var typeDesc = q.Type switch
        {
            "multiple" => "多选题，返回所有正确选项字母拼接，如 \"ABC\"",
            "judge" => "判断题，返回 \"T\"（对）或 \"F\"（错）",
            _ => "单选题，返回选项字母，如 \"A\""
        };

        var optionsText = q.Options.Count > 0
            ? "\n选项：\n" + string.Join("\n", q.Options.Select(o => $"  {o.Value}. {o.Text}"))
            : "";

        var prompt = $"你是一个考试助手，请根据以下题目给出正确答案。\n"+ 
                     subjectLine +
                     $"请回答以下{(q.Type == "judge" ? "判断" : q.Type == "multiple" ? "多选" : "单选")}题，只返回答案字母，不要解释。\n" +
                     $"{typeDesc}。\n\n题目：{q.Title}{optionsText}";

        var body = new
        {
            model = _settings.AiModel,
            messages = new[] { new { role = "user", content = prompt } },
            temperature = 0
        };

        var bodyJson = JsonSerializer.Serialize(body);
        WriteLog($"[REQ] 题目 {q.Index} ({q.Type}): {q.Title}");
        WriteLog($"[BODY] {bodyJson}");

        var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(bodyJson, Encoding.UTF8, "application/json")
        };
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _settings.AiApiKey);

        var resp = await _http.SendAsync(req);
        var respText = await resp.Content.ReadAsStringAsync();
        WriteLog($"[RESP] {respText}");

        resp.EnsureSuccessStatusCode();

        var node = JsonNode.Parse(respText);
        var content = node?["choices"]?[0]?["message"]?["content"]?.GetValue<string>()?.Trim() ?? "";

        var answer = content.Split(['\n', ' ', '。', '，', '.'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(s => s.All(c => char.IsLetter(c) || c == 'T' || c == 'F'))
            ?.ToUpper() ?? content.ToUpper().Trim();

        WriteLog($"[ANS] 题目 {q.Index} 答案: {answer}");
        return answer;
    }

    private static void WriteLog(string msg)
    {
        try
        {
            File.AppendAllText(LogFile,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
        }
        catch { }
    }

    private class QuestionItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("index")]
        public string Index { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string Type { get; set; } = "single";
        [System.Text.Json.Serialization.JsonPropertyName("title")]
        public string Title { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("options")]
        public List<OptionItem> Options { get; set; } = [];
    }

    private class OptionItem
    {
        [System.Text.Json.Serialization.JsonPropertyName("value")]
        public string Value { get; set; } = "";
        [System.Text.Json.Serialization.JsonPropertyName("text")]
        public string Text { get; set; } = "";
    }
}