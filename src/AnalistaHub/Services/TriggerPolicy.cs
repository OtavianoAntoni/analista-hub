using AnalistaHub.Models;

namespace AnalistaHub.Services;

/// <summary>
/// Regra única de gatilho, válida para TODAS as ferramentas — as que existem hoje e as que
/// surgirem depois. Nenhuma tela ou manifesto define regra própria: tudo passa por aqui.
///
/// Regras (ver docs/REGRAS-DE-FERRAMENTA.md):
///   1. sempre começa com ':'  — é o que o operador digita na busca e o que o Espanso usa;
///   2. minúsculas, sem espaço; só a-z, 0-9, '-' e '_' depois dos dois-pontos;
///   3. entre 2 e 32 caracteres no total (':' + 1..31);
///   4. único no catálogo — dois gatilhos iguais tornam a busca ambígua.
/// O gatilho global (atalho do Windows) tem regra própria, em HotkeyService.
/// </summary>
public static class TriggerPolicy
{
    public const int MaxLength = 32;

    /// <summary>Normaliza para a forma canônica: ":mkt". Nunca devolve valor inválido sem avisar.</summary>
    public static string Normalize(string trigger)
    {
        var value = (trigger ?? "").Trim();
        if (value.Length == 0) return "";
        value = value.TrimStart(':').Trim();
        value = Text.Fold(value).Replace(" ", "");
        return value.Length == 0 ? "" : ":" + value;
    }

    /// <summary>Valida a forma canônica e a unicidade contra o catálogo. Devolve null quando está ok.</summary>
    public static string? Validate(string trigger, ToolCatalog catalog, string? ignoreToolId = null)
    {
        var value = Normalize(trigger);

        if (value.Length == 0) return "o gatilho não pode ficar vazio";
        if (value.Length > MaxLength) return $"o gatilho pode ter no máximo {MaxLength} caracteres";
        if (value == ":") return "escreva algo depois dos dois-pontos (ex.: :mkt)";

        foreach (var ch in value[1..])
        {
            var ok = ch is >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_';
            if (!ok) return "use apenas letras sem acento, números, '-' e '_' (ex.: :mkt, :sn-rb)";
        }

        var clash = catalog.Tools.FirstOrDefault(t =>
            !t.IsBroken &&
            !string.Equals(t.Id, ignoreToolId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Normalize(SettingsService.TriggerFor(t)), value, StringComparison.OrdinalIgnoreCase));

        if (clash is not null)
            return $"o gatilho {value} já é da ferramenta «{clash.Name}»";

        return null;
    }
}
