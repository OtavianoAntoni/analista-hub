using System.Text.Json;
using System.Text.Json.Serialization;
using AnalistaHub.Models;

namespace AnalistaHub.Services;

public sealed class AppSettings
{
    /// <summary>Atalho global, ex.: "Ctrl+Alt+Space".</summary>
    public string Hotkey { get; set; } = "Ctrl+Alt+Space";
    public bool StartWithWindows { get; set; } = false;
    /// <summary>Escuro | Claro | Sistema</summary>
    public string Theme { get; set; } = "Escuro";
    /// <summary>Pasta extra de manifestos. Vazio = %LOCALAPPDATA%\AnalistaHub\tools.</summary>
    public string ToolsFolder { get; set; } = "";
    public int ToastSeconds { get; set; } = 12;
    public bool KeepHistory { get; set; } = true;
    /// <summary>Modo de saída escolhido por ferramenta (id -> clipboard|type|notify).</summary>
    public Dictionary<string, string> OutputMode { get; set; } = new();
    public Dictionary<string, string> LastInput { get; set; } = new();
    /// <summary>Gatilho redefinido pelo usuário (id -> ":mkt").</summary>
    public Dictionary<string, string> TriggerOverrides { get; set; } = new();
    /// <summary>Último resultado conhecido da validação de credencial (id -> "estado|quando|mensagem").</summary>
    public Dictionary<string, string> AuthStatus { get; set; } = new();
}

public static class AppPaths
{
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AnalistaHub");

    public static string ToolsDir => Path.Combine(DataDir, "tools");
    public static string SettingsFile => Path.Combine(DataDir, "settings.json");
    public static string HistoryFile => Path.Combine(DataDir, "historico.jsonl");
    public static string BuiltinToolsDir => Path.Combine(AppContext.BaseDirectory, "tools");
    public static string IconFile => Path.Combine(AppContext.BaseDirectory, "Assets", "app.ico");
    public static string LogFile => Path.Combine(DataDir, "hub.log");

    public static void Ensure()
    {
        Directory.CreateDirectory(DataDir);
        Directory.CreateDirectory(ToolsDir);
    }

    public static void Log(string msg)
    {
        try
        {
            Ensure();
            File.AppendAllText(LogFile, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {msg}{Environment.NewLine}");
        }
        catch { /* log nunca derruba o app */ }
    }
}

public static class SettingsService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never
    };

    public static AppSettings Current { get; private set; } = new();

    public static void Load()
    {
        try
        {
            if (File.Exists(AppPaths.SettingsFile))
            {
                var json = File.ReadAllText(AppPaths.SettingsFile);
                Current = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log("settings load falhou: " + ex.Message);
            Current = new AppSettings();
        }
        if (string.IsNullOrWhiteSpace(Current.ToolsFolder)) Current.ToolsFolder = AppPaths.ToolsDir;
    }

    public static void Save()
    {
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(AppPaths.SettingsFile, JsonSerializer.Serialize(Current, Options));
        }
        catch (Exception ex) { AppPaths.Log("settings save falhou: " + ex.Message); }
    }

    public static string OutputModeFor(string toolId, string fallback) =>
        Current.OutputMode.TryGetValue(toolId, out var v) && !string.IsNullOrWhiteSpace(v) ? v : fallback;

    /// <summary>Gatilho efetivo: o que o usuário definiu na aba da ferramenta vence o manifesto.</summary>
    public static string TriggerFor(ToolManifest tool) =>
        Current.TriggerOverrides.TryGetValue(tool.Id, out var v) && !string.IsNullOrWhiteSpace(v)
            ? v.Trim()
            : tool.Trigger;

    public static void SetTrigger(ToolManifest tool, string trigger)
    {
        var value = (trigger ?? "").Trim();
        if (string.IsNullOrWhiteSpace(value)) Current.TriggerOverrides.Remove(tool.Id);
        else
        {
            if (!value.StartsWith(':')) value = ":" + value;
            Current.TriggerOverrides[tool.Id] = value;
        }
        Save();
    }
}
