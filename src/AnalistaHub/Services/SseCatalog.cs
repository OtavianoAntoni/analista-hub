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

/// <summary>Cidade da base unificada, com zero ou mais redes.</summary>
public sealed class SseCity
{
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    /// <summary>Nome sem acento e em maiúsculas — chave de busca já pronta.</summary>
    [JsonPropertyName("k")] public string Key { get; set; } = "";
    [JsonPropertyName("uf")] public string Uf { get; set; } = "";
    [JsonPropertyName("sigla")] public string Sigla { get; set; } = "";
    [JsonPropertyName("rows")] public List<SseRow> Rows { get; set; } = new();

    /// <summary>
    /// De onde veio o registro: <c>sse</c> (cidade com planilha de OLT, tem NM/OLT/VPN)
    /// ou <c>anatel</c> (só o cadastro: sigla + estado).
    /// </summary>
    [JsonPropertyName("src")] public string Origin { get; set; } = "sse";

    /// <summary>Sigla que estava na planilha SSE quando a oficial da ANATEL difere — auditoria.</summary>
    [JsonPropertyName("sigla_sse")] public string? SpreadsheetSigla { get; set; }

    /// <summary>Tem informações de acesso (NM/OLT/VPN)?</summary>
    [JsonIgnore] public bool HasAccess => Rows.Count > 0;
}

/// <summary>Fonte de dados declarada no JSON da base (rastreabilidade).</summary>
public sealed class SseSource
{
    [JsonPropertyName("arquivo")] public string File { get; set; } = "";
    [JsonPropertyName("linhas")] public int Lines { get; set; }
    [JsonPropertyName("nota")] public string Note { get; set; } = "";
    [JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
}

/// <summary>
/// Base local da consulta (cidade -> Sigla Anatel + estado + NM/OLT/VPN quando existe).
///
/// Duas origens unificadas em um registro por cidade:
///   CONSULTA SSE  -> cidades com planilha de OLT: NM, OLT e VPN de cada rede;
///   ANATEL        -> cadastro nacional de municípios: sigla oficial e estado.
/// Cidade só na ANATEL aparece sem NM (Rows vazio). A sigla exibida é sempre a da ANATEL
/// quando a cidade existe nas duas fontes — é a oficial.
///
/// Carregada uma única vez (~500 KB) e buscada por prefixo, trecho do nome, sigla ou UF.
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
    /// <summary>Quantas cidades têm NM/OLT/VPN (o resto é só cadastro).</summary>
    public int TotalWithAccess { get; set; }
    public string DataPath { get; set; } = "";
    public string? Error { get; set; }

    /// <summary>O arquivo de rede interna (sse-rede.local.json) foi encontrado e mesclado?</summary>
    public bool RedeLocalCarregada { get; set; }
    public string RedeLocalPath { get; set; } = "";

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
            catalog.TotalWithAccess = catalog.Cities.Count(c => c.HasAccess);

            foreach (var city in catalog.Cities)
                if (string.IsNullOrWhiteSpace(city.Key)) city.Key = Text.Fold(city.Name).ToUpperInvariant();

            // A base versionada NÃO tem NM/OLT/VPN (dado de rede interna). O que houver vem deste
            // arquivo local, ignorado pelo git: %LOCALAPPDATA%\AnalistaHub\sse-rede.local.json
            var rede = CarregarRedeLocal();
            if (rede is not null)
            {
                catalog.RedeLocalCarregada = true;
                catalog.RedeLocalPath = CaminhoRedeLocal();
                foreach (var city in catalog.Cities)
                {
                    if (!rede.TryGetValue(ChaveRede(city.Key, city.Uf), out var rows) || rows.Count == 0) continue;
                    city.Rows = rows;
                    city.Origin = "sse";
                }
                catalog.TotalWithAccess = catalog.Cities.Count(c => c.HasAccess);
                catalog.TotalLines = catalog.Cities.Sum(c => c.Rows.Count);
            }

            AppPaths.Log($"SseCatalog: {catalog.Cities.Count} cidades " +
                         $"({catalog.TotalWithAccess} com NM, {catalog.TotalLines} linhas), " +
                         $"{catalog.Sources.Count} fonte(s), gerado em {catalog.Generated}, de {path}");
        }
        catch (Exception ex)
        {
            catalog.Error = "Não consegui ler a base da Consulta SSE: " + ex.Message;
            AppPaths.Log("SseCatalog: " + ex);
        }

        return catalog;
    }

    /// <summary>Nome do arquivo local com as redes internas (NM · OLT · VPN). Nunca versionado.</summary>
    private const string RedeLocalNome = "sse-rede.local.json";

    /// <summary>
    /// Chave de junção da rede interna: nome sem acento + UF. Só o nome colide — há municípios
    /// homônimos em estados diferentes (ex.: "BOM JESUS" no PI e no RS).
    /// </summary>
    private static string ChaveRede(string key, string uf) =>
        (key.Trim() + "|" + uf.Trim()).ToUpperInvariant();

    /// <summary>
    /// Arquivo com as redes internas. Caminho vem do config.local.json (`sse-rede.path`) — pode ser
    /// uma pasta compartilhada da empresa ou a pasta sincronizada do OneDrive. Sem a chave, usa o
    /// arquivo ao lado dos dados do usuário.
    /// </summary>
    private static string CaminhoRedeLocal()
    {
        var configurado = LocalConfig.Get("sse-rede.path");
        if (!string.IsNullOrWhiteSpace(configurado))
        {
            var expandido = Environment.ExpandEnvironmentVariables(configurado.Replace('/', '\\').Trim());
            if (Path.IsPathRooted(expandido)) return expandido;
        }
        return Path.Combine(AppPaths.DataDir, RedeLocalNome);
    }

    /// <summary>
    /// Lê as redes internas do arquivo local. Ausente = a consulta funciona só com sigla/UF — é o que
    /// acontece em qualquer clone do repositório, porque NM/OLT/VPN não fazem parte da base pública.
    /// </summary>
    private static Dictionary<string, List<SseRow>>? CarregarRedeLocal()
    {
        var path = CaminhoRedeLocal();
        if (!File.Exists(path))
        {
            AppPaths.Log("SseCatalog: rede local ausente (consulta sem NM/OLT/VPN) — " + path);
            return null;
        }

        try
        {
            var file = JsonSerializer.Deserialize<SseRedeFile>(File.ReadAllText(path), Options);
            var map = new Dictionary<string, List<SseRow>>(StringComparer.OrdinalIgnoreCase);
            foreach (var city in file?.Cities ?? new List<SseRedeCity>())
                if (!string.IsNullOrWhiteSpace(city.Key))
                    map[ChaveRede(city.Key, city.Uf)] = city.Rows;

            AppPaths.Log($"SseCatalog: rede local com {map.Count} cidade(s) — {path}");
            return map;
        }
        catch (Exception ex)
        {
            AppPaths.Log("SseCatalog: rede local inválida — " + ex.Message);
            return null;
        }
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
    /// 0 nome idêntico (com acento) · 1 nome idêntico sem acento · 2 UF digitada sozinha
    /// (ex.: "MG" = consulta por estado) · 3 começa com · 4 termo no meio · 5 sigla Anatel · 6 UF.
    /// Empate: cidade com NM/OLT/VPN primeiro — é a que o analista opera.
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
            .ThenByDescending(m => m.City.HasAccess)
            .ThenBy(m => m.City.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(m => m.City)
            .ToList();
    }

    private static int Score(SseCity city, string key, string term, string raw)
    {
        if (city.Name.Equals(raw, StringComparison.OrdinalIgnoreCase)) return 0;
        if (key.Equals(term, StringComparison.Ordinal)) return 1;
        // "MG"/"SP" sozinho é consulta por estado: vem antes de qualquer prefixo de nome ou sigla
        if (raw.Length == 2 && city.Uf.Equals(raw, StringComparison.OrdinalIgnoreCase)) return 2;
        var index = key.IndexOf(term, StringComparison.Ordinal);
        if (index == 0) return 3;
        if (index > 0) return 4;
        if (city.Sigla.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 5;
        if (city.Uf.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 6;
        return -1;
    }

    /// <summary>
    /// Texto copiado — contrato estável: cabeçalho + uma linha por rede.
    /// Cidade sem planilha de OLT (só cadastro ANATEL) copia o cabeçalho e a ausência de NM.
    /// </summary>
    public static string Format(SseCity city)
    {
        var lines = new List<string> { $"{city.Name} ({city.Uf}) — Sigla Anatel: {city.Sigla}" };
        if (city.Rows.Count == 0)
            lines.Add("Sem NM cadastrado para esta cidade.");
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

    /// <summary>Arquivo local com as redes internas (fora do controle de versão).</summary>
    private sealed class SseRedeFile
    {
        [JsonPropertyName("schema")] public int Schema { get; set; } = 1;
        [JsonPropertyName("generated")] public string Generated { get; set; } = "";
        [JsonPropertyName("cidades")] public List<SseRedeCity> Cities { get; set; } = new();
    }

    private sealed class SseRedeCity
    {
        [JsonPropertyName("k")] public string Key { get; set; } = "";
        [JsonPropertyName("uf")] public string Uf { get; set; } = "";
        [JsonPropertyName("n")] public string Name { get; set; } = "";
        [JsonPropertyName("rows")] public List<SseRow> Rows { get; set; } = new();
    }
}
