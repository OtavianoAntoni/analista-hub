using System.Text.Json.Serialization;

namespace AnalistaHub.Models;

/// <summary>Campo de entrada declarado pela ferramenta (vira uma caixa de texto na HUD).</summary>
public sealed class ToolInput
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Argumento passado ao script, ex.: "-Ip".</summary>
    public string Arg { get; set; } = "";
    /// <summary>text | ip | mac</summary>
    public string Type { get; set; } = "text";
    public bool Required { get; set; }
    /// <summary>Campo vazio = lê o valor da área de transferência.</summary>
    public bool EmptyUsesClipboard { get; set; }
    public string Placeholder { get; set; } = "";
    public string Hint { get; set; } = "";
    public string Example { get; set; } = "";
}

public sealed class ToolOutput
{
    /// <summary>clipboard | type | notify</summary>
    public string Default { get; set; } = "clipboard";
    public List<string> Allowed { get; set; } = new() { "clipboard", "type", "notify" };
    /// <summary>O que o valor representa, ex.: "SN + \"1\"".</summary>
    public string ValueLabel { get; set; } = "";
    /// <summary>Texto extra no aviso de sucesso.</summary>
    public string SuccessLabel { get; set; } = "";
}

public sealed class CredentialField
{
    public string Key { get; set; } = "";
    public string Label { get; set; } = "";
    /// <summary>Variável de ambiente entregue ao script (nunca por linha de comando).</summary>
    public string Env { get; set; } = "";
    public bool Secret { get; set; }
    public string Placeholder { get; set; } = "";
}

/// <summary>
/// Como o HUB confirma, junto ao serviço, se as credenciais digitadas valem.
/// Sem este bloco a ferramenta não tem como validar senha — o estado fica "não verificado".
/// </summary>
public sealed class CredentialValidation
{
    public string Url { get; set; } = "";
    /// <summary>POST | GET</summary>
    public string Method { get; set; } = "POST";
    public string ContentType { get; set; } = "application/json";
    /// <summary>Corpo com marcadores {user} e {passwd} (só para POST).</summary>
    public string BodyTemplate { get; set; } = "";
    /// <summary>Caminho no JSON de resposta que indica sucesso, ex.: "st" ou "data.ok".</summary>
    public string OkJsonPath { get; set; } = "";
    /// <summary>Valor (em texto) que significa credencial aceita.</summary>
    public string OkEquals { get; set; } = "true";
    /// <summary>Caminho no JSON com a mensagem do servidor (opcional).</summary>
    public string MessagePath { get; set; } = "";
    public int TimeoutSeconds { get; set; } = 8;
}

public sealed class CredentialRequirement
{
    public string Label { get; set; } = "";
    public List<CredentialField> Fields { get; set; } = new();
    /// <summary>De onde as credenciais vinham antes (só informativo; o HUB não exige arquivo).</summary>
    public string LegacyFile { get; set; } = "";
    public CredentialValidation? Validation { get; set; }
}

public sealed class NetworkRequirement
{
    public string Label { get; set; } = "";
    public string Host { get; set; } = "";
    public int Port { get; set; }
}

/// <summary>
/// Manifesto de uma ferramenta. É o contrato que as próximas sessões preenchem:
/// uma ferramenta nova entra copiando um .json, sem recompilar o HUB.
/// </summary>
public sealed class ToolManifest
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Category { get; set; } = "Geral";
    public string Description { get; set; } = "";
    public string LongDescription { get; set; } = "";
    public string Trigger { get; set; } = "";
    /// <summary>Glifo Segoe MDL2 Assets, ex.: "\uE968".</summary>
    public string Icon { get; set; } = "\uE8A7";
    public string Script { get; set; } = "";
    /// <summary>powershell | pwsh | python | direct</summary>
    public string Interpreter { get; set; } = "powershell";
    public List<string> ModeArgs { get; set; } = new() { "-Mode", "type" };
    public List<ToolInput> Inputs { get; set; } = new();
    public ToolOutput Output { get; set; } = new();
    public CredentialRequirement? Credentials { get; set; }
    public NetworkRequirement? Network { get; set; }
    public int TimeoutSeconds { get; set; } = 12;
    public string LogFile { get; set; } = "";
    public List<string> Examples { get; set; } = new();
    public string Notes { get; set; } = "";
    /// <summary>O script antigo abre o próprio popup quando falha (duplicidade conhecida).</summary>
    public bool LegacyErrorPopup { get; set; }

    /// <summary>
    /// Contrato legado: o script pode escrever a MENSAGEM DE ERRO no stdout e sair com 0
    /// (mac_vendor.ps1 faz isso no -Mode type). Cada padrão aqui marca esse texto como falha,
    /// em vez de virar "valor" na área de transferência.
    /// </summary>
    public List<string> StdoutErrorPatterns { get; set; } = new();

    [JsonIgnore] public string SourcePath { get; set; } = "";
    [JsonIgnore] public bool FromUserFolder { get; set; }
    [JsonIgnore] public string? LoadError { get; set; }
    [JsonIgnore] public bool IsBroken => LoadError is not null;

    [JsonIgnore]
    public string ExpandedScript =>
        string.IsNullOrWhiteSpace(Script) ? "" : Environment.ExpandEnvironmentVariables(Script.Replace('/', '\\'));

    [JsonIgnore]
    public string ExpandedLogFile =>
        string.IsNullOrWhiteSpace(LogFile) ? "" : Environment.ExpandEnvironmentVariables(LogFile.Replace('/', '\\'));

    [JsonIgnore]
    public bool ScriptExists => !string.IsNullOrWhiteSpace(ExpandedScript) && System.IO.File.Exists(ExpandedScript);

    [JsonIgnore] public string IconGlyph => string.IsNullOrEmpty(Icon) ? "\uE8A7" : Icon;
}
