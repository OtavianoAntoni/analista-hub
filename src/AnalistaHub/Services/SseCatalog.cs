using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnalistaHub.Services;

/// <summary>Uma combinação NM/OLT/VPN de uma cidade.</summary>
public sealed class SseRow
{
    [JsonPropertyName("nm")] public string Nm { get; set; } = "";
    [JsonPropertyName("olt")] public string Olt { get; set; } = "";
    [JsonPropertyName("vpn")] public string Vpn { get; set; } = "";
}

/// <summary>Cidade da base CONSULTA SSE, com uma ou mais redes.</summary>
public sealed class SseCity
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    /// <summary>Nome sem acento e em maiúsculas — chave de busca já pronta.</summary>
    [JsonPropertyName("k")] public string Key { get; set; } = "";
    [JsonPropertyName("uf")] public string Uf { get; set; } = "";
    [JsonPropertyName("sigla")] public string Sigla { get; set; } = "";
    [JsonPropertyName("rows")] public List<SseRow> Rows { get; set; } = new();
}

public sealed class SseSource
{
    [JsonPropertyName("arquivo")] public string File { get; set; } = "";
    [JsonPropertyName("linhas")] public int Lines { get; set; }
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>
/// Base local da consulta SSE (cidades -> Sigla Anatel + NM + OLT + VPN).
/// Carregada uma única vez, mantida em memória (~24 KB) e buscada por prefixo,
/// trecho do nome, sigla Anatel ou UF.
///
/// Ordem de precedência do arquivo:
///   1. %LOCALAPPDATA%\AnalistaHub\sse-cidades.json  (atualização sem reinstalar)
///   2. &lt;pasta do app&gt;\data\sse-cidades.json        (embutido no pacote)
/// </summary>
public sealed class SseCatalog
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static SseCatalog? _current;
    public static SseCatalog Current => _current ??= Load();
    public static void Reset() => _current = null;

    public List<SseCity> Cities { get; } = new();
    public List<SseSource> Sources { get; } = new();
    public string Generated { get; set; } = "";
    public int TotalLines { get; set; }
    public string DataPath { get; set; } = "";
    public string? Error { get; set; }

    public bool Loaded => Error is null && Cities.Count > 0;

    public static SseCatalog Load()
    {
        var catalog = new SseCatalog();
        var path = ResolvePath();
        catalog.DataPath = path;

        if (path.Length == 0 || !File.Exists(path))
        {
            catalog.Error = "Base da Consulta SSE não encontrada.";
            AppPaths.Log("SseCatalog: arquivo não encontrado. Locais testados: " +
                         Path.Combine(AppPaths.DataDir, "sse-cidades.json") + " e " +
                         Path.Combine(AppContext.BaseDirectory, "data", "sse-cidades.json"));
            return catalog;
        }

        try
        {
            var json = File.ReadAllText(path);
            var file = JsonSerializer.Deserialize<SseFile>(json, Options);
            if (file is null || file.Cities.Count == 0)
            {
                catalog.Error = "Base da Consulta SSE vazia.";
                return catalog;
            }

            catalog.Cities.AddRange(file.Cities
                .Where(c => !string.IsNullOrWhiteSpace(c.Name))
                .OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase));
            catalog.Sources.AddRange(file.Sources);
            catalog.Generated = file.Generated;
            catalog.TotalLines = file.TotalLines > 0
                ? file.TotalLines
                : catalog.Cities.Sum(c => Math.Max(1, c.Rows.Count));

            foreach (var city in catalog.Cities)
                if (string.IsNullOrWhiteSpace(city.Key)) city.Key = Text.Fold(city.Name).ToUpperInvariant();

            AppPaths.Log($"SseCatalog: {catalog.Cities.Count} cidades, {catalog.TotalLines} linhas, " +
                         $"{catalog.Sources.Count} arquivo(s), gerado em {catalog.Generated}, de {path}");
        }
        catch (Exception ex)
        {
            catalog.Error = "Não consegui ler a base da Consulta SSE: " + ex.Message;
            AppPaths.Log("SseCatalog: " + ex);
        }

        return catalog;
    }

    private static string ResolvePath()
    {
        var user = Path.Combine(AppPaths.DataDir, "sse-cidades.json");
        if (File.Exists(user)) return user;
        var builtin = Path.Combine(AppContext.BaseDirectory, "data", "sse-cidades.json");
        return File.Exists(builtin) ? builtin : user;
    }

    /// <summary>
    /// Filtro incremental. Sem termo devolve tudo. Prioridade:
    /// 0 nome idêntico (com acento) · 1 nome idêntico sem acento · 2 começa com · 3 termo no meio
    /// · 4 sigla Anatel · 5 UF.
    /// </summary>
    public List<SseCity> Search(string query)
    {
        var raw = (query ?? "").Trim();
        var term = Text.Fold(raw).ToUpperInvariant();
        if (term.Length == 0) return Cities.ToList();

        var matches = new List<(SseCity City, int Score)>();
        foreach (var city in Cities)
        {
            var key = Text.Fold(string.IsNullOrWhiteSpace(city.Key) ? city.Name : city.Key).ToUpperInvariant();
            var score = Score(city, key, term, raw);
            if (score >= 0) matches.Add((city, score));
        }

        return matches
            .OrderBy(m => m.Score)
            .ThenBy(m => m.City.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => m.City)
            .ToList();
    }

    private static int Score(SseCity city, string key, string term, string raw)
    {
        if (city.Name.Equals(raw, StringComparison.OrdinalIgnoreCase)) return 0;
        if (key.Equals(term, StringComparison.Ordinal)) return 1;
        var index = key.IndexOf(term, StringComparison.Ordinal);
        if (index == 0) return 2;
        if (index > 0) return 3;
        if (city.Sigla.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 4;
        if (city.Uf.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 5;
        return -1;
    }

    /// <summary>Texto copiado — contrato estável: cabeçalho + uma linha por rede.</summary>
    public static string Format(SseCity city)
    {
        var lines = new List<string> { $"{city.Name} ({city.Uf}) — Sigla Anatel: {city.Sigla}" };
        foreach (var row in city.Rows)
            lines.Add($"NM: {row.Nm} | OLT: {row.Olt} | VPN: {row.Vpn}");
        return string.Join(Environment.NewLine, lines);
    }

    private sealed class SseFile
    {
        [JsonPropertyName("generated")] public string Generated { get; set; } = "";
        [JsonPropertyName("total_linhas")] public int TotalLines { get; set; }
        [JsonPropertyName("sources")] public List<SseSource> Sources { get; set; } = new();
        [JsonPropertyName("cidades")] public List<SseCity> Cities { get; set; } = new();
    }
}
