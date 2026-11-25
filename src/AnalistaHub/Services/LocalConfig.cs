using System.Text;
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
                        foreach (var kv in ReadFile()) _values[kv.Key] = kv.Value;
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

    /// <summary>
    /// Lê o arquivo para um dicionário plano. Aceita objeto com valores texto, número/booleano e
    /// LISTA de textos (a lista vira texto separado por ';' — é o que permite
    /// <c>"sse-rede.candidatos": ["\\\\srv\\a.json", "\\\\srv\\b.json"]</c> sem quebrar o formato
    /// plano das outras chaves; ver <see cref="GetList"/>).
    /// </summary>
    private static Dictionary<string, string> ReadFile()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(FilePath)) return map;

        using var doc = JsonDocument.Parse(File.ReadAllText(FilePath), new JsonDocumentOptions
        {
            CommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });
        if (doc.RootElement.ValueKind != JsonValueKind.Object) return map;

        foreach (var kv in doc.RootElement.EnumerateObject())
        {
            var valor = kv.Value.ValueKind switch
            {
                JsonValueKind.String => kv.Value.GetString() ?? "",
                JsonValueKind.Array => string.Join(";", kv.Value.EnumerateArray()
                    .Select(item => item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : item.ToString())
                    .Where(item => !string.IsNullOrWhiteSpace(item))),
                JsonValueKind.Null => "",
                _ => kv.Value.ToString()
            };
            if (!string.IsNullOrWhiteSpace(valor)) map[kv.Name] = valor;
        }
        return map;
    }

    /// <summary>Valor como lista: aceita a forma de array no JSON ou texto separado por ';'.</summary>
    public static List<string> GetList(string chave) =>
        Get(chave).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

    /// <summary>
    /// Grava UMA chave preservando as demais (usado pelo instalador para apontar a pasta da equipe).
    /// Valor vazio remove a chave.
    /// </summary>
    public static bool Set(string chave, string valor)
    {
        if (string.IsNullOrWhiteSpace(chave)) return false;
        try
        {
            AppPaths.Ensure();
            var mapa = ReadFile();
            if (string.IsNullOrWhiteSpace(valor)) mapa.Remove(chave);
            else mapa[chave] = valor.Trim();

            var ordenado = new SortedDictionary<string, string>(mapa, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(ordenado, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            Invalidate();
            return true;
        }
        catch (Exception ex)
        {
            AppPaths.Log($"LocalConfig: gravação de \"{chave}\" falhou — {ex.Message}");
            return false;
        }
    }

    /// <summary>Valor bruto de uma chave ("" quando não existe). Não interpreta ${CFG:}.</summary>
    public static string Get(string chave)
    {
        if (string.IsNullOrWhiteSpace(chave)) return "";
        return Values.TryGetValue(chave, out var valor) ? valor : "";
    }

    /// <summary>Recarrega do disco (usado depois de gravar o arquivo).</summary>
    public static void Invalidate()
    {
        lock (Gate) { _values = null; Faltando.Clear(); }
    }

    /// <summary>
    /// Primeira execução — ou pasta de dados apagada: cria o arquivo-modelo com as chaves que o
    /// catálogo pediu, vazias e com instrução. Sem isso a ferramenta falhava com "erro de
    /// comunicação" em vez de dizer o que faltava (NET-003).
    /// </summary>
    public static void EnsureTemplate(IEnumerable<string>? chaves = null)
    {
        try
        {
            if (File.Exists(FilePath)) return;

            Directory.CreateDirectory(AppPaths.DataDir);

            var valores = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                ["_leia-me"] = "Preencha os valores desta máquina. O modelo comentado está em " +
                               "config.example.json no repositório."
            };
            foreach (var chave in (chaves ?? Enumerable.Empty<string>()).OrderBy(x => x))
                valores[chave] = "";

            var json = JsonSerializer.Serialize(valores, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json, new UTF8Encoding(false));
            AppPaths.Log($"LocalConfig: modelo criado em {FilePath} com {valores.Count - 1} chave(s) — preencha os valores");
        }
        catch (Exception ex) { AppPaths.Log("LocalConfig.EnsureTemplate: " + ex.Message); }
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
