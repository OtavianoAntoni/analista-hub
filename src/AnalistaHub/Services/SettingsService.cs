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
    /// <summary>
    /// MKT-004: tamanho global dos ícones das ferramentas (1.0 = padrão). Pequeno 0,8 · Médio 1,0 ·
    /// Grande 1,3. Aplicado em todas as telas por <c>ToolIcons.Create</c>.
    /// </summary>
    public double IconScale { get; set; } = 1.0;
    /// <summary>Pasta extra de manifestos. Vazio = %LOCALAPPDATA%\AnalistaHub\tools.</summary>
    public string ToolsFolder { get; set; } = "";
    public int ToastSeconds { get; set; } = 12;
    public bool KeepHistory { get; set; } = true;
    /// <summary>Modo de saída escolhido por ferramenta (id -> clipboard|type|notify).</summary>
    public Dictionary<string, string> OutputMode { get; set; } = new();
    public Dictionary<string, string> LastInput { get; set; } = new();
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
    /// <summary>Consulta: mostrar o resumo da base no cabeçalho.</summary>
    public bool SseShowBaseInfo { get; set; }

    /// <summary>Busca rápida: fechar ao clicar fora (padrão ligado — comportamento histórico).</summary>
    public bool HudCloseOnClickOutside { get; set; } = true;

    /// <summary>
    /// CFG-004 (2.3): o que o clique ESQUERDO no ícone da bandeja faz — "window" (padrão, abre a
    /// janela principal) ou "search" (abre a busca rápida). Duplo clique espelha esta escolha.
    /// </summary>
    public string TrayLeftClickAction { get; set; } = "window";

    // ---- ping do destino (NET-001, docs/SUBFERRAMENTA-PING.md) ----
    /// <summary>Espera depois da última tecla antes de pingar o IP do campo (ms).</summary>
    public int PingDebounceMs { get; set; } = 400;
    /// <summary>Tempo máximo de cada pacote ICMP (ms).</summary>
    public int PingTimeoutMs { get; set; } = 1000;

    /// <summary>Ferramentas removidas da busca rápida (continuam no menu lateral e na bandeja).</summary>
    public List<string> HiddenTools { get; set; } = new();

    /// <summary>Atalho global por ferramenta: id da ferramenta → combinação (ex.: "Ctrl+Alt+J").</summary>
    public Dictionary<string, string> ToolHotkeys { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// O que o atalho da ferramenta faz ao ser acionado (CFG-003): id → "consult" | "ask".
    /// Ausente = "ask" (aguardar input), que é o comportamento menos surpreendente.
    /// </summary>
    public Dictionary<string, string> ShortcutBehavior { get; set; } = new(StringComparer.OrdinalIgnoreCase);

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
    /// <summary>
    /// UPD-003: última versão cujo "o que mudou" resumido já foi mostrado automaticamente.
    /// Igual à versão em execução = não abre de novo (decisão: só no 1º início após atualizar).
    /// </summary>
    public string PublicChangelogSeenVersion { get; set; } = "";
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

                // CFG-002: gatilho de ferramenta saiu do produto (2.2.1). Um settings.json de versão
                // anterior ainda traz "TriggerOverrides": o campo desconhecido é ignorado — a config
                // carrega sem erro — e o aviso fica no log. A próxima gravação já limpa o arquivo.
                AvisarGatilhosLegados(json);
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
    /// Conta os gatilhos que ficaram no settings.json de uma versão anterior e registra no log.
    /// Não é erro: é migração silenciosa, com rastro para quem for investigar depois.
    /// </summary>
    private static void AvisarGatilhosLegados(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (!string.Equals(prop.Name, "TriggerOverrides", StringComparison.OrdinalIgnoreCase)) continue;

                var quantidade = prop.Value.ValueKind == JsonValueKind.Object ? prop.Value.EnumerateObject().Count() : 0;
                AppPaths.Log($"migração 2.2.1: {quantidade} gatilho(s) de ferramenta ignorado(s) " +
                             "(o conceito foi removido; a próxima gravação limpa o settings.json)");
            }
        }
        catch { /* arquivo ilegível já é tratado pelo Load */ }
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

    /// <summary>
    /// Comportamento do atalho da ferramenta (CFG-003). Padrão "ask" — abrir pedindo o valor é menos
    /// surpreendente do que consultar o que estiver na área de transferência.
    /// </summary>
    public static string ShortcutBehaviorFor(string toolId) =>
        Current.ShortcutBehavior.TryGetValue(toolId, out var v) && !string.IsNullOrWhiteSpace(v)
            ? NormalizeShortcutBehavior(v)
            : "ask";

    public static void SetShortcutBehavior(string toolId, string behavior)
    {
        Current.ShortcutBehavior[toolId] = NormalizeShortcutBehavior(behavior);
        Save();
    }

    private static string NormalizeShortcutBehavior(string? behavior) =>
        string.Equals((behavior ?? "").Trim(), "consult", StringComparison.OrdinalIgnoreCase) ? "consult" : "ask";

    /// <summary>
    /// Ação do clique esquerdo na bandeja (CFG-004): "window" (padrão) ou "search".
    /// Qualquer valor desconhecido volta ao padrão — nunca deixa o clique sem ação.
    /// </summary>
    public static string TrayLeftClickActionFor() =>
        string.Equals(Current.TrayLeftClickAction?.Trim(), "search", StringComparison.OrdinalIgnoreCase)
            ? "search"
            : "window";
}
