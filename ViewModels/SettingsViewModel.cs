using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SystemMind.Services;

namespace SystemMind.ViewModels;

public sealed class SettingsViewModel : INotifyPropertyChanged
{
    private string _openAiKey = "";
    private string _openRouterKey = "";
    private string _customEndpoint = "";
    private string _selectedModel = "google/gemma-4-31b-it:free";
    private bool _safeMode = true;
    private string _statusMessage = "Configuration loaded";

    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<string>? NotifyUser;

    public string OpenAiKey
    {
        get => _openAiKey;
        set { if (_openAiKey != value) { _openAiKey = value; OnChanged(); } }
    }

    public string OpenRouterKey
    {
        get => _openRouterKey;
        set { if (_openRouterKey != value) { _openRouterKey = value; OnChanged(); } }
    }

    public string CustomEndpoint
    {
        get => _customEndpoint;
        set { if (_customEndpoint != value) { _customEndpoint = value; OnChanged(); } }
    }

    public string SelectedModel
    {
        get => _selectedModel;
        set { if (_selectedModel != value) { _selectedModel = value; OnChanged(); } }
    }

    public bool SafeMode
    {
        get => _safeMode;
        set { if (_safeMode != value) { _safeMode = value; OnChanged(); } }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { if (_statusMessage != value) { _statusMessage = value; OnChanged(); } }
    }

    public ICommand SaveCommand { get; }
    public ICommand ReloadCommand { get; }

    public SettingsViewModel()
    {
        SaveCommand = new RelayCommand(Save);
        ReloadCommand = new RelayCommand(Load);
        Load();
    }

    public void Load()
    {
        EnvLoader.Load();
        OpenAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";
        OpenRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY") ?? "";
        CustomEndpoint = Environment.GetEnvironmentVariable("AI_API_ENDPOINT") ?? "";
        SelectedModel = Environment.GetEnvironmentVariable("AI_MODEL") ?? "google/gemma-4-31b-it:free";
        StatusMessage = "Settings loaded from environment and .env";
    }

    public void Save()
    {
        try
        {
            Environment.SetEnvironmentVariable("OPENAI_API_KEY", string.IsNullOrWhiteSpace(OpenAiKey) ? null : OpenAiKey.Trim());
            Environment.SetEnvironmentVariable("OPENROUTER_API_KEY", string.IsNullOrWhiteSpace(OpenRouterKey) ? null : OpenRouterKey.Trim());
            Environment.SetEnvironmentVariable("AI_API_ENDPOINT", string.IsNullOrWhiteSpace(CustomEndpoint) ? null : CustomEndpoint.Trim());
            Environment.SetEnvironmentVariable("AI_MODEL", string.IsNullOrWhiteSpace(SelectedModel) ? null : SelectedModel.Trim());

            // Write or update local .env file
            var envPath = Path.Combine(Directory.GetCurrentDirectory(), ".env");
            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(OpenRouterKey))
                lines.Add($"OPENROUTER_API_KEY={OpenRouterKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(OpenAiKey))
                lines.Add($"OPENAI_API_KEY={OpenAiKey.Trim()}");
            if (!string.IsNullOrWhiteSpace(CustomEndpoint))
                lines.Add($"AI_API_ENDPOINT={CustomEndpoint.Trim()}");
            if (!string.IsNullOrWhiteSpace(SelectedModel))
                lines.Add($"AI_MODEL={SelectedModel.Trim()}");

            File.WriteAllLines(envPath, lines);
            StatusMessage = "Settings saved successfully to .env";
            NotifyUser?.Invoke("Settings saved!");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Failed to save settings: {ex.Message}";
        }
    }

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
