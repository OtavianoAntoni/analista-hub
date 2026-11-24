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
    /// <summary>Última página aberta (id da ferramenta ou "home"/"settings"), para reabrir onde parou.</summary>
    public string LastPage { get; set; } = "home";
    /// <summary>Painel da Consulta SSE: mostra NM/OLT/VPN? (botão de alternância "NMs").</summary>
    public bool SseShowNodes { get; set; } = true;

    // ---- preferências de tela (Bloco 5) ----
    /// <summary>HUD: esconder o rodapé "N ferramenta(s)".</summary>
    public bool HideToolCount { get; set; }
    /// <summary>Consulta SSE: cartões montados por busca (padrão 30).</summary>
    public int SseMaxRows { get; set; } = 30;
    /// <summary>Consulta SSE: mostrar "somente cadastro · sem NM" nos cartões sem rede (padrão: oculto).</summary>
    public bool SseShowNoNodes { get; set; }
    /// <summary>Consulta SSE: mostrar a linha "fonte: …" no rodapé (padrão: oculto).</summary>
    public bool SseShowSource { get; set; }
    /// <summary>Consulta SSE: mostrar o resumo da base no cabeçalho (padrão: oculto).</summary>
    public bool SseShowBaseInfo { get; set; }

    // ---- atualizações ----
    /// <summary>Procurar atualização sozinho, no máximo uma vez por dia.</summary>
    public bool AutoCheckUpdates { get; set; } = true;
    /// <summary>ISO-8601 UTC da última verificação (inclusive a automática).</summary>
    public string LastUpdateCheckUtc { get; set; } = "";
    /// <summary>Canal alternativo; vazio = releases do repositório oficial.</summary>
    public string UpdateFeedUrl { get; set; } = "";
    /// <summary>Versão já avisada por aviso flutuante — evita repetir o mesmo aviso todo dia.</summary>
    public string NotifiedUpdateVersion { get; set; } = "";
    /// <summary>Versão baixada e extraída, esperando reinício para trocar a pasta.</summary>
    public string PendingUpdateVersion { get; set; } = "";
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
    /// <summary>Pacotes baixados e o script que troca a pasta do programa.</summary>
    public static string UpdateDir => Path.Combine(DataDir, "update");

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

    /// <summary>
    /// Grava as preferências de forma ATÔMICA: escreve um temporário e troca de uma vez. Se algo
    /// falhar no meio (arquivo travado por antivírus, disco cheio), o settings.json antigo continua
    /// íntegro em vez de virar um arquivo truncado — que faria o app voltar aos padrões.
    /// </summary>
    public static void Save()
    {
        var tmp = AppPaths.SettingsFile + ".tmp";
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(tmp, JsonSerializer.Serialize(Current, Options));
            File.Move(tmp, AppPaths.SettingsFile, overwrite: true);
        }
        catch (Exception ex)
        {
            AppPaths.Log("settings save falhou: " + ex.Message);
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
        }
    }

    /// <summary>Lê o que está gravado no disco agora (não mexe no estado em memória).</summary>
    public static AppSettings? ReadFromDisk()
    {
        try
        {
            if (!File.Exists(AppPaths.SettingsFile)) return null;
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(AppPaths.SettingsFile));
        }
        catch (Exception ex)
        {
            AppPaths.Log("settings leitura do disco falhou: " + ex.Message);
            return null;
        }
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
