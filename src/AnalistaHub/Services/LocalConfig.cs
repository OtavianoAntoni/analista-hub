using System.Text.Json;
using System.Text.RegularExpressions;

namespace AnalistaHub.Services;

/// <summary>
/// Configuração local da máquina: o que é identificador de infraestrutura (endereço de serviço,
/// caminho de cofre, host de inventário) NÃO mora no manifesto versionado. O manifesto traz
/// <c>${CFG:chave}</c> e o valor real vem daqui:
///
///   %LOCALAPPDATA%\AnalistaHub\config.local.json     (ignorado pelo git)
///
/// Formato: objeto plano, chave → texto. Ex.:
///   { "mkt-serial.host": "10.0.0.1:3113",
///     "mkt-serial.url":  "http://10.0.0.1:3113/api/auth_ep" }
///
/// Sem o arquivo (ou sem a chave) a ferramenta continua aparecendo no catálogo, mas avisa o que falta
/// em vez de estourar. O versionado é o <c>config.example.json</c>, com as chaves e CHANGEME.
/// </summary>
public static class LocalConfig
{
    private static readonly Regex Placeholder =
        new(@"\$\{CFG:([^}]+)\}", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static Dictionary<string, string>? _values;
    private static readonly object Gate = new();

    public static string FilePath => Path.Combine(AppPaths.DataDir, "config.local.json");

    /// <summary>Chaves que o catálogo pediu e não existem na configuração local (para avisar).</summary>
    public static SortedSet<string> Faltando { get; } = new(StringComparer.OrdinalIgnoreCase);

    private static Dictionary<string, string> Values
    {
        get
        {
            lock (Gate)
            {
                if (_values is not null) return _values;
                _values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    if (File.Exists(FilePath))
                    {
                        var map = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(FilePath));
                        if (map is not null)
                            foreach (var kv in map)
                                if (!string.IsNullOrWhiteSpace(kv.Value)) _values[kv.Key] = kv.Value;
                        AppPaths.Log($"LocalConfig: {_values.Count} chave(s) de {FilePath}");
                    }
                    else
                    {
                        AppPaths.Log("LocalConfig: arquivo ausente — " + FilePath);
                    }
                }
                catch (Exception ex)
                {
                    AppPaths.Log("LocalConfig: leitura falhou — " + ex.Message);
                }
                return _values;
            }
        }
    }

    /// <summary>Recarrega do disco (usado depois de gravar o arquivo).</summary>
    public static void Invalidate()
    {
        lock (Gate) { _values = null; Faltando.Clear(); }
    }

    /// <summary>Troca cada <c>${CFG:chave}</c> pelo valor local. Chave ausente vira texto vazio e entra em <see cref="Faltando"/>.</summary>
    public static string Resolve(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || !text.Contains("${CFG:", StringComparison.OrdinalIgnoreCase))
            return text ?? "";

        return Placeholder.Replace(text, m =>
        {
            var chave = m.Groups[1].Value.Trim();
            if (Values.TryGetValue(chave, out var valor)) return valor;
            Faltando.Add(chave);
            return "";
        });
    }

    /// <summary>Resolve uma lista inteira (exemplos, placeholders).</summary>
    public static List<string> ResolveAll(IEnumerable<string>? items) =>
        items is null ? new List<string>() : items.Select(Resolve).Where(v => v.Length > 0).ToList();

    /// <summary>Escreve o arquivo local (usado pelo instalador/testes). Não versionar.</summary>
    public static void Write(IReadOnlyDictionary<string, string> valores)
    {
        try
        {
            AppPaths.Ensure();
            var json = JsonSerializer.Serialize(valores, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
            Invalidate();
        }
        catch (Exception ex)
        {
            AppPaths.Log("LocalConfig: gravação falhou — " + ex.Message);
        }
    }
}
