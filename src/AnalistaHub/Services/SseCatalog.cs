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
    private static readonly object LoadGate = new();

    public static SseCatalog Current
    {
        get { lock (LoadGate) { return _current ??= Load(); } }
    }

    public static void Reset() { lock (LoadGate) { _current = null; } }

    /// <summary>Avisa as telas abertas que a base (e a lista de NMs) foi recarregada.</summary>
    public static event Action? Reloaded;

    /// <summary>
    /// Recarrega sem reiniciar o app: relê o config.local.json e procura a lista de NMs de novo.
    /// O catálogo é singleton, então sem isso um arquivo novo no share só valeria depois de reabrir
    /// o programa (o botão "Recarregar dados" chama este método).
    /// </summary>
    public static SseCatalog Reload()
    {
        LocalConfig.Invalidate();
        SseCatalog novo;
        lock (LoadGate) { novo = _current = Load(); }
        try { Reloaded?.Invoke(); } catch (Exception ex) { AppPaths.Log("SseCatalog.Reload: " + ex.Message); }
        return novo;
    }

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
    /// <summary>Quantas cidades vieram da lista de NMs (0 quando a consulta é só sigla/UF).</summary>
    public int RedeCidades { get; set; }
    /// <summary>De onde a lista de NMs veio (rótulo humano: config, variável, pasta do usuário…).</summary>
    public string RedeLocalOrigem { get; set; } = "";
    /// <summary>Todo candidato testado, na ordem, com o resultado — é o que o CLI e o log mostram.</summary>
    public List<SseRedeOrigem> RedeOrigens { get; } = new();
    /// <summary>Aviso VISÍVEL quando a origem configurada existe mas não pôde ser usada.</summary>
    public string RedeAviso { get; set; } = "";

    /// <summary>Uma linha pronta para a tela: "Rede interna: 161 cidades · \\servidor\share\sse-rede.json".</summary>
    public string RedeResumo =>
        RedeLocalCarregada
            ? $"Rede interna: {RedeCidades} cidade(s) · {RedeLocalPath}"
            : RedeAviso.Length > 0 ? RedeAviso : "Rede interna: não configurada (consulta só com sigla/UF)";

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

            // A base versionada NÃO tem NM/OLT/VPN (dado de rede interna). O que houver vem da lista
            // de NMs, procurada na ordem de NET-005 (config → variável → pastas locais).
            var rede = DescobrirRede(catalog);
            if (rede is not null)
            {
                catalog.RedeLocalCarregada = true;
                catalog.RedeCidades = rede.Count;
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

    /// <summary>Nome aceito dentro de uma PASTA de equipe (mesmo conteúdo, sem o sufixo .local).</summary>
    private const string RedeEquipeNome = "sse-rede.json";

    /// <summary>
    /// Variável de ambiente que aponta a lista de NMs — arquivo ou pasta. Serve para apontar a pasta
    /// da equipe sem gravar endereço nenhum em arquivo do usuário.
    /// </summary>
    public const string RedeEnvVar = "ANALISTAHUB_SSE_REDE";

    // UNC morto não pode segurar a abertura da janela: a checagem de cada candidato tem orçamento
    // próprio, e a descoberta inteira tem um teto. Estourado o tempo, segue para o próximo — e o
    // motivo fica registrado em RedeOrigens (aparece no --check-tools e no hub.log).
    private static readonly TimeSpan OrcamentoPorCandidato = TimeSpan.FromMilliseconds(700);
    private static readonly TimeSpan OrcamentoTotal = TimeSpan.FromMilliseconds(1500);

    /// <summary>
    /// Chave de junção da rede interna: nome sem acento + UF. Só o nome colide — há municípios
    /// homônimos em estados diferentes (ex.: "BOM JESUS" no PI e no RS).
    /// </summary>
    private static string ChaveRede(string key, string uf) =>
        (key.Trim() + "|" + uf.Trim()).ToUpperInvariant();

    /// <summary>
    /// Ordem de resolução (para no primeiro candidato que existir E for válido):
    ///   1. config.local.json → sse-rede.path
    ///   2. config.local.json → sse-rede.candidatos (lista, na ordem)
    ///   3. variável ANALISTAHUB_SSE_REDE (arquivo ou pasta)
    ///   4. &lt;pasta do app&gt;\data\sse-rede.local.json
    ///   5. %LOCALAPPDATA%\AnalistaHub\sse-rede.local.json
    /// Nada encontrado = a consulta funciona só com sigla/UF (é o caso de qualquer clone público).
    /// </summary>
    private static IEnumerable<(string Caminho, string Origem)> Candidatos()
    {
        var vistos = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (valor, origem) in Declarados())
        {
            var expandidos = Expandir(valor).ToList();
            foreach (var caminho in expandidos)
            {
                if (!vistos.Add(caminho)) continue;
                // pasta declarada vira dois candidatos (sse-rede.local.json e sse-rede.json): o rótulo
                // leva o nome do arquivo para o log e o --check-tools dizerem QUAL dos dois falhou
                yield return (caminho, expandidos.Count > 1 ? $"{origem} ({Path.GetFileName(caminho)})" : origem);
            }
        }

        var embutido = Path.Combine(AppContext.BaseDirectory, "data", RedeLocalNome);
        if (vistos.Add(embutido)) yield return (embutido, "pasta do aplicativo");

        var local = Path.Combine(AppPaths.DataDir, RedeLocalNome);
        if (vistos.Add(local)) yield return (local, "pasta de dados do usuário");
    }

    /// <summary>Origens declaradas pelo usuário/administrador (as que, falhando, geram aviso).</summary>
    private static IEnumerable<(string Valor, string Origem)> Declarados()
    {
        var configurado = LocalConfig.Get("sse-rede.path");
        if (!string.IsNullOrWhiteSpace(configurado)) yield return (configurado, "config: sse-rede.path");

        var indice = 0;
        foreach (var candidato in LocalConfig.GetList("sse-rede.candidatos"))
            yield return (candidato, $"config: sse-rede.candidatos[{++indice}]");

        var ambiente = Environment.GetEnvironmentVariable(RedeEnvVar);
        if (!string.IsNullOrWhiteSpace(ambiente)) yield return (ambiente, "variável " + RedeEnvVar);
    }

    /// <summary>
    /// Um valor declarado pode ser arquivo (.json) ou PASTA. Na pasta procuramos sse-rede.local.json
    /// e depois sse-rede.json. A decisão é pelo sufixo, sem tocar no disco: Directory.Exists em UNC
    /// morto pendura igual a File.Exists.
    /// </summary>
    private static IEnumerable<string> Expandir(string valor)
    {
        var caminho = Environment.ExpandEnvironmentVariables((valor ?? "").Replace('/', '\\').Trim().Trim('"'));
        if (caminho.Length == 0) yield break;

        if (caminho.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
        {
            yield return caminho;
            yield break;
        }

        yield return Path.Combine(caminho, RedeLocalNome);
        yield return Path.Combine(caminho, RedeEquipeNome);
    }

    /// <summary>
    /// Procura a lista de NMs nos candidatos e devolve o mapa já validado. Cada tentativa entra em
    /// <see cref="RedeOrigens"/> com status e motivo — inclusive as que falharam.
    /// </summary>
    private static Dictionary<string, List<SseRow>>? DescobrirRede(SseCatalog catalog)
    {
        var limite = DateTime.UtcNow + OrcamentoTotal;
        var declaradoFalhou = false;

        foreach (var (caminho, origem) in Candidatos())
        {
            var info = new SseRedeOrigem { Caminho = caminho, Origem = origem };
            catalog.RedeOrigens.Add(info);

            if (DateTime.UtcNow >= limite)
            {
                info.Status = "tempo_esgotado";
                info.Motivo = "orçamento da descoberta esgotado";
                if (EhDeclarada(origem)) declaradoFalhou = true;
                AppPaths.Log($"SseCatalog: candidato [{origem}] não testado — orçamento de descoberta esgotado ({caminho})");
                continue;
            }

            var (mapa, status, motivo) = LerCandidato(caminho);
            info.Status = status;
            info.Motivo = motivo;
            info.Cidades = mapa?.Count ?? 0;

            AppPaths.Log($"SseCatalog: candidato [{origem}] {caminho} → {status}" +
                         (motivo.Length > 0 ? $" ({motivo})" : ""));

            if (mapa is not null && mapa.Count > 0)
            {
                catalog.RedeLocalPath = caminho;
                catalog.RedeLocalOrigem = origem;
                return mapa;
            }

            if (EhDeclarada(origem)) declaradoFalhou = true;
        }

        catalog.RedeAviso = declaradoFalhou
            ? $"lista de NMs inacessível — usando base pública ({catalog.RedeOrigens.FirstOrDefault(o => EhDeclarada(o.Origem))?.Caminho})"
            : "";
        return null;
    }

    private static bool EhDeclarada(string origem) =>
        origem.StartsWith("config:", StringComparison.OrdinalIgnoreCase) ||
        origem.StartsWith("variável", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Lê e valida um candidato com orçamento de tempo. Regras de aceitação: JSON válido, lista
    /// <c>cidades</c> não vazia, cada cidade com <c>k</c> e <c>uf</c>, e pelo menos uma linha de
    /// acesso. Candidato inválido não aborta a consulta: o motivo é registrado e o próximo é testado.
    /// </summary>
    private static (Dictionary<string, List<SseRow>>? Mapa, string Status, string Motivo) LerCandidato(string caminho)
    {
        Task<(Dictionary<string, List<SseRow>>? Mapa, string Status, string Motivo)> tarefa;
        try
        {
            tarefa = Task.Run(() => LerArquivoRede(caminho));
        }
        catch (Exception ex)
        {
            return (null, "invalido", ex.Message);
        }

        try
        {
            if (tarefa.Wait(OrcamentoPorCandidato)) return tarefa.Result;
            // segue em segundo plano; o resultado tardio é descartado, o motivo fica registrado
            return (null, "tempo_esgotado", $"sem resposta em {OrcamentoPorCandidato.TotalMilliseconds:0} ms");
        }
        catch (Exception ex)
        {
            return (null, "invalido", (ex.InnerException ?? ex).Message);
        }
    }

    /// <summary>
    /// SSE-002: valida um sse-rede.local.json escolhido pelo usuário ANTES de copiar. Não grava nada —
    /// devolve ok, motivo da recusa e quantas cidades o arquivo cobre.
    /// </summary>
    public static (bool Ok, string Motivo, int Cidades) ValidarRede(string caminho)
    {
        if (string.IsNullOrWhiteSpace(caminho)) return (false, "caminho vazio", 0);

        var (mapa, status, motivo) = LerArquivoRede(caminho);
        return mapa is null
            ? (false, $"{status}: {motivo}", 0)
            : (true, "", mapa.Count);
    }

    /// <summary>
    /// SSE-002: valida e copia um sse-rede.local.json para a pasta de dados (a lista que o app lê).
    /// `sobrescrever=false` recusa quando já existe — a UI pergunta antes; o CLI usa --force.
    /// Devolve ok, motivo e nº de cidades. Não abre diálogo: é o mesmo caminho para UI e diagnóstico.
    /// </summary>
    public static (bool Ok, string Motivo, int Cidades) ImportarRede(string origem, bool sobrescrever)
    {
        var validacao = ValidarRede(origem);
        if (!validacao.Ok) return validacao;

        var destino = Path.Combine(AppPaths.DataDir, RedeLocalNome);
        try
        {
            Directory.CreateDirectory(AppPaths.DataDir);
            if (File.Exists(destino))
            {
                if (!sobrescrever) return (false, "já existe uma lista importada", 0);
                try { File.Copy(destino, destino + ".bak", overwrite: true); } catch { }
            }

            File.Copy(origem, destino, overwrite: true);
        }
        catch (Exception ex)
        {
            return (false, ex.Message, 0);
        }

        Reload();
        return (true, destino, validacao.Cidades);
    }

    private static (Dictionary<string, List<SseRow>>? Mapa, string Status, string Motivo) LerArquivoRede(string caminho)
    {
        if (!File.Exists(caminho)) return (null, "ausente", "arquivo não encontrado");

        try
        {
            var texto = File.ReadAllText(caminho);
            if (texto.Trim().Length == 0) return (null, "vazio", "arquivo em branco");

            var arquivo = JsonSerializer.Deserialize<SseRedeFile>(texto, Options);
            var mapa = new Dictionary<string, List<SseRow>>(StringComparer.OrdinalIgnoreCase);
            var linhas = 0;

            foreach (var city in arquivo?.Cities ?? new List<SseRedeCity>())
            {
                if (string.IsNullOrWhiteSpace(city.Key) || string.IsNullOrWhiteSpace(city.Uf)) continue;
                mapa[ChaveRede(city.Key, city.Uf)] = city.Rows ?? new List<SseRow>();
                linhas += city.Rows?.Count ?? 0;
            }

            if (mapa.Count == 0) return (null, "invalido", "nenhuma cidade com key/uf");
            if (linhas == 0) return (null, "vazio", $"{mapa.Count} cidade(s) sem nenhuma linha de NM/OLT/VPN");
            return (mapa, "ok", "");
        }
        catch (Exception ex)
        {
            return (null, "invalido", ex.Message);
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

        // SSE-001: termo com cara de sigla (2–5, maiúsculo, sem espaço/dígito) prioriza a SIGLA.
        // Antes, "contém no nome" (4) vinha antes de "sigla começa com" (5) e a sigla procurada
        // ficava fora dos 30 primeiros.
        var pareceSigla = raw.Length is >= 2 and <= 5
                          && raw == raw.ToUpperInvariant()
                          && !raw.Contains(' ')
                          && !raw.Any(char.IsDigit);
        var sigla = (city.Sigla ?? "").Trim();

        if (pareceSigla && sigla.Equals(term, StringComparison.OrdinalIgnoreCase)) return 2;
        if (raw.Length == 2 && city.Uf.Equals(raw, StringComparison.OrdinalIgnoreCase)) return 3;
        if (pareceSigla && sigla.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 4;

        var index = key.IndexOf(term, StringComparison.Ordinal);
        if (index == 0) return 5;
        if (index > 0) return 6;

        if (sigla.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 7;
        if (city.Uf.StartsWith(term, StringComparison.OrdinalIgnoreCase)) return 8;
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

/// <summary>
/// Um candidato da lista de NMs (NET-005): onde foi procurado, por qual motivo e o que aconteceu.
/// Existe para a descoberta ser auditável — o usuário vê no ⚙ e o instalador/diagnóstico vê no
/// <c>--check-tools</c>, em vez de só um "não achei" no log.
/// </summary>
public sealed class SseRedeOrigem
{
    public string Caminho { get; init; } = "";
    /// <summary>Quem apontou o caminho: config, variável de ambiente, pasta do app, pasta do usuário.</summary>
    public string Origem { get; init; } = "";
    /// <summary>ok · ausente · invalido · vazio · tempo_esgotado.</summary>
    public string Status { get; set; } = "nao_testado";
    public string Motivo { get; set; } = "";
    public int Cidades { get; set; }

    public bool Ok => Status == "ok";
}
