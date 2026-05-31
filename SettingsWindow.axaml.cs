using Avalonia.Controls;
using Avalonia.Interactivity;
using CourseAutoLearner.Models;

namespace CourseAutoLearner;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    public bool? Result { get; private set; }

    private static readonly (string Name, string BaseUrl, string Model)[] Presets =
    [
        ("DeepSeek", "https://api.deepseek.com/v1", "deepseek-chat"),
        ("Kimi (月之暗面)", "https://api.moonshot.cn/v1", "moonshot-v1-8k"),
        ("通义千问 (阿里)", "https://dashscope.aliyuncs.com/compatible-mode/v1", "qwen-turbo"),
        ("智谱 GLM", "https://open.bigmodel.cn/api/paas/v4", "glm-4-flash"),
        ("豆包 (字节)", "https://ark.cn-beijing.volces.com/api/v3", "doubao-35k-pro"),
        ("小米 MiMo", "https://api.xiaomi.com/v1", "MiMo-7B"),
        ("OpenAI", "https://api.openai.com/v1", "gpt-4o-mini"),
    ];

    public SettingsWindow(AppSettings settings)
    {
        InitializeComponent();
        _settings = settings;
        TxtBaseUrl.Text = settings.AiBaseUrl;
        TxtApiKey.Text = settings.AiApiKey;
        TxtModel.Text = settings.AiModel;

        // Try to match current settings to a preset
        CmbPreset.SelectedIndex = 0; // default to 自定义
        for (int i = 0; i < Presets.Length; i++)
        {
            if (settings.AiBaseUrl.Contains(Presets[i].BaseUrl.Replace("https://", "").Replace("/v1", "").Replace("/v4", "").Replace("/compatible-mode/v1", "")
                    .Replace("ark.cn-beijing.volces.com/api/v3", "volces.com"), StringComparison.OrdinalIgnoreCase))
            {
                CmbPreset.SelectedIndex = i + 1;
                break;
            }
        }
    }

    private void Preset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        var idx = CmbPreset.SelectedIndex;
        if (idx <= 0 || idx > Presets.Length) return;

        var preset = Presets[idx - 1];
        TxtBaseUrl.Text = preset.BaseUrl;
        TxtModel.Text = preset.Model;
    }

    private void Save_Click(object? sender, RoutedEventArgs e)
    {
        _settings.AiBaseUrl = (TxtBaseUrl.Text ?? "").Trim();
        _settings.AiApiKey = (TxtApiKey.Text ?? "").Trim();
        _settings.AiModel = string.IsNullOrWhiteSpace(TxtModel.Text)
            ? "deepseek-chat" : TxtModel.Text.Trim();
        _settings.Save();
        Result = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Result = false;
        Close();
    }
}
