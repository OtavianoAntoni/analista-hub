using System.Text.Json;
using AnalistaHub.Models;

namespace AnalistaHub.Services;

/// <summary>
/// Carrega os manifestos de ferramentas: pasta do usuário sobrepõe a embutida (mesmo id).
/// Manifesto inválido não derruba o app — vira uma entrada "com problema".
/// </summary>
public sealed class ToolCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    public List<ToolManifest> Tools { get; } = new();

    /// <summary>
    /// Verdadeiro no CI (`--skip-scripts`): os scripts das ferramentas vivem na máquina do analista,
    /// não no repositório. Sem isso todo manifesto viraria "com problema" no runner e o gate de
    /// publicação bloquearia o build por um motivo que não existe lá.
    /// </summary>
    public static bool IgnoreMissingScripts { get; set; }

    public static ToolCatalog Load()
    {
        var catalog = new ToolCatalog();
        var byId = new Dictionary<string, ToolManifest>(StringComparer.OrdinalIgnoreCase);

        // 1º a embutida, 2º a do usuário (sobrescreve)
        foreach (var (dir, fromUser) in new[]
                 {
                     (AppPaths.BuiltinToolsDir, false),
                     (SettingsService.Current.ToolsFolder, true)
                 })
        {
            if (string.IsNullOrWhiteSpace(dir) || !Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f))
            {
                var tool = Parse(file, fromUser);
                if (tool is null) continue;
                if (!string.IsNullOrWhiteSpace(tool.Id) && byId.TryGetValue(tool.Id, out var existing) && !fromUser)
                    continue; // já veio de pasta mais prioritária
                if (!string.IsNullOrWhiteSpace(tool.Id)) byId[tool.Id] = tool;
                else catalog.Tools.Add(tool);
            }
        }

        catalog.Tools.InsertRange(0, byId.Values
            .OrderBy(t => t.IsBroken ? 1 : 0)
            .ThenBy(t => t.Category, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(t => t.Name, StringComparer.CurrentCultureIgnoreCase));

        return catalog;
    }

    private static ToolManifest? Parse(string file, bool fromUser)
    {
        try
        {
            var json = File.ReadAllText(file);
            var tool = JsonSerializer.Deserialize<ToolManifest>(json, Options);
            if (tool is null) return Broken(file, fromUser, "manifesto vazio");
            tool.SourcePath = file;
            tool.FromUserFolder = fromUser;

            var problems = new List<string>();
            if (string.IsNullOrWhiteSpace(tool.Id)) problems.Add("sem \"id\"");
            if (string.IsNullOrWhiteSpace(tool.Name)) problems.Add("sem \"name\"");
            if (tool.IsDataset)
            {
                if (string.IsNullOrWhiteSpace(tool.Dataset)) problems.Add("tipo dataset sem \"dataset\"");
                else if (!tool.DatasetExists) problems.Add($"base não encontrada: {tool.ExpandedDataset}");
            }
            else if (string.IsNullOrWhiteSpace(tool.Script)) problems.Add("sem \"script\"");
            else if (!tool.ScriptExists && !IgnoreMissingScripts) problems.Add($"script não encontrado: {tool.ExpandedScript}");
            if (problems.Count > 0) tool.LoadError = string.Join("; ", problems);
            return tool;
        }
        catch (Exception ex)
        {
            return Broken(file, fromUser, ex.Message);
        }
    }

    private static ToolManifest Broken(string file, bool fromUser, string reason) => new()
    {
        Id = Path.GetFileNameWithoutExtension(file),
        Name = Path.GetFileNameWithoutExtension(file),
        Category = "Com problema",
        Description = "Manifesto inválido.",
        SourcePath = file,
        FromUserFolder = fromUser,
        LoadError = reason
    };

    public ToolManifest? Find(string id) =>
        Tools.FirstOrDefault(t => string.Equals(t.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>Busca por nome, gatilho ou palavras da descrição (sem acento/caixa).</summary>
    public IEnumerable<ToolManifest> Search(string query)
    {
        var usable = Tools.Where(t => !t.IsBroken).ToList();
        if (string.IsNullOrWhiteSpace(query)) return usable.Take(50);
        var q = Text.Fold(query.Trim().TrimStart(':'));
        return usable
            .Select(t => (tool: t, score: Score(t, q)))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.tool.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(x => x.tool)
            .Take(50);
    }

    private static int Score(ToolManifest t, string q)
    {
        if (q.Length == 0) return 1;
        var name = Text.Fold(t.Name);
        var trigger = Text.Fold(SettingsService.TriggerFor(t).TrimStart(':'));
        var desc = Text.Fold(t.Description + " " + t.Category + " " + t.LongDescription);

        if (name.Equals(q, StringComparison.Ordinal)) return 100;
        if (trigger.Equals(q, StringComparison.Ordinal)) return 95;
        if (name.StartsWith(q, StringComparison.Ordinal)) return 80;
        if (trigger.StartsWith(q, StringComparison.Ordinal)) return 75;
        if (name.Contains(q, StringComparison.Ordinal)) return 60;
        if (trigger.Contains(q, StringComparison.Ordinal)) return 55;
        if (desc.Contains(q, StringComparison.Ordinal)) return 30;

        // busca por palavras soltas: todas precisam aparecer em algum lugar
        var parts = q.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length > 1 && parts.All(p => name.Contains(p) || desc.Contains(p) || trigger.Contains(p)))
            return 20;
        return 0;
    }
}

public static class Text
{
    /// <summary>Minúsculas e sem acento, para busca tolerante.</summary>
    public static string Fold(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var ch in s.Normalize(System.Text.NormalizationForm.FormD))
        {
            var cat = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(ch);
            if (cat == System.Globalization.UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.ToLowerInvariant(ch));
        }
        return sb.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }
}
