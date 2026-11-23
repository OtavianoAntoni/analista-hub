using System.Runtime.InteropServices;
using System.Text;
using AnalistaHub.Models;
using AnalistaHub.Services;

namespace AnalistaHub;

/// <summary>
/// Modo de linha de comando, sem janela. Serve para o instalador e para diagnóstico:
///   AnalistaHub.exe --list-tools
///   AnalistaHub.exe --test mkt-serial 10.0.0.1
///   AnalistaHub.exe --probe mkt-serial
/// Imprime JSON e sai com 0 (ok) ou 1 (falha).
/// </summary>
public static class Cli
{
    [DllImport("kernel32.dll")] private static extern bool AttachConsole(int processId);
    private const int ATTACH_PARENT_PROCESS = -1;

    public static bool IsCliInvocation(string[] args) =>
        args.Length > 0 && args[0] is "--list-tools" or "--test" or "--probe" or "--version" or "--help"
            or "--set-cred" or "--clear-cred" or "--set-trigger" or "--validate-cred" or "--check-tools";

    public static int Run(string[] args, ToolCatalog catalog)
    {
        AttachConsole(ATTACH_PARENT_PROCESS);
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch { }

        var command = args[0];
        var output = new StringBuilder();

        switch (command)
        {
            case "--version":
                output.AppendLine(Json(new { ok = true, version = typeof(App).Assembly.GetName().Version?.ToString(3) }));
                break;

            case "--help":
                output.AppendLine("Analista Hub — uso:");
                output.AppendLine("  AnalistaHub.exe                     abre a janela principal");
                output.AppendLine("  AnalistaHub.exe --list-tools        lista as ferramentas e o que exigem");
                output.AppendLine("  AnalistaHub.exe --test <id> [valor] executa a ferramenta e imprime o resultado");
                output.AppendLine("  AnalistaHub.exe --probe <id>        testa serviço e credenciais da ferramenta");
                output.AppendLine("  AnalistaHub.exe --set-cred <id> <campo>   grava credencial lida do STDIN");
                output.AppendLine("  AnalistaHub.exe --clear-cred <id>   apaga as credenciais guardadas da ferramenta");
                output.AppendLine("  AnalistaHub.exe --validate-cred <id> confirma as credenciais no serviço");
                output.AppendLine("  AnalistaHub.exe --check-tools       valida o catálogo (gatilho, script, campos)");
                break;

            case "--check-tools":
            {
                var problems = new List<object>();
                foreach (var tool in catalog.Tools)
                {
                    if (tool.IsBroken)
                    {
                        problems.Add(new { id = tool.Id, problema = "manifesto: " + tool.LoadError });
                        continue;
                    }

                    var trigger = SettingsService.TriggerFor(tool);
                    var triggerError = TriggerPolicy.Validate(trigger, catalog, tool.Id);
                    if (triggerError is not null) problems.Add(new { id = tool.Id, problema = "gatilho: " + triggerError });

                    if (tool.IsDataset)
                    {
                        if (!tool.DatasetExists)
                            problems.Add(new { id = tool.Id, problema = "base não encontrada: " + tool.ExpandedDataset });
                    }
                    else if (!tool.ScriptExists)
                        problems.Add(new { id = tool.Id, problema = "script não encontrado: " + tool.ExpandedScript });

                    if (tool.Credentials is not null && tool.Credentials.Fields.Count == 0)
                        problems.Add(new { id = tool.Id, problema = "credenciais declaradas sem campos" });
                }

                output.AppendLine(Json(new
                {
                    ok = problems.Count == 0,
                    ferramentas = catalog.Tools.Count,
                    problemas = problems
                }, true));

                Write(output.ToString());
                return problems.Count == 0 ? 0 : 1;
            }

            case "--validate-cred":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                if (!CredentialValidator.CanValidate(tool)) return Fail(output, "ferramenta sem regra de validação declarada");

                var result = CredentialValidator.ValidateAsync(tool, CancellationToken.None).GetAwaiter().GetResult();
                output.AppendLine(Json(new
                {
                    ok = result.State == AuthState.Ok,
                    ferramenta = tool.Id,
                    estado = result.State.ToString().ToLowerInvariant(),
                    mensagem = result.Message
                }, true));
                Write(output.ToString());
                return result.State == AuthState.Ok ? 0 : 1;
            }

            case "--set-cred":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                var key = args.ElementAtOrDefault(2) ?? "";
                if (tool.Credentials is null || tool.Credentials.Fields.All(f => !string.Equals(f.Key, key, StringComparison.OrdinalIgnoreCase)))
                    return Fail(output, $"campo de credencial \"{key}\" não existe na ferramenta {tool.Id}");

                // o valor vem do STDIN: assim não aparece em lista de processos nem em histórico de shell
                string? line;
                try { line = Console.In.ReadLine(); } catch { line = null; }
                if (string.IsNullOrEmpty(line)) return Fail(output, "nenhum valor recebido no STDIN");

                CredentialStore.Set(tool.Id, key, line.Trim());
                var state = Probes.CheckCredentials(tool);
                output.AppendLine(Json(new
                {
                    ok = state.Complete,
                    ferramenta = tool.Id,
                    campo = key,
                    credenciais_completas = state.Complete,
                    faltando = state.Missing
                }, true));
                Write(output.ToString());
                return state.Complete ? 0 : 0;
            }

            case "--set-trigger":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                var value = args.ElementAtOrDefault(2) ?? "";
                SettingsService.SetTrigger(tool, value);
                output.AppendLine(Json(new { ok = true, ferramenta = tool.Id, gatilho = SettingsService.TriggerFor(tool) }));
                break;
            }

            case "--clear-cred":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                CredentialStore.Clear(tool.Id);
                output.AppendLine(Json(new { ok = true, ferramenta = tool.Id, credenciais = "apagadas" }));
                break;
            }

            case "--list-tools":
            {
                var tools = catalog.Tools.Select(t => new
                {
                    id = t.Id,
                    nome = t.Name,
                    tipo = t.IsDataset ? "dataset" : "script",
                    gatilho = SettingsService.TriggerFor(t),
                    estado = t.IsBroken ? "com problema: " + t.LoadError : "ok",
                    script = t.IsDataset ? null : t.ExpandedScript,
                    script_existe = t.IsDataset ? (bool?)null : t.ScriptExists,
                    base_local = t.IsDataset ? t.ExpandedDataset : null,
                    base_existe = t.IsDataset ? t.DatasetExists : (bool?)null,
                    entradas = t.Inputs.Select(i => new { chave = i.Key, rotulo = i.Label, tipo = i.Type, usa_clipboard = i.EmptyUsesClipboard }),
                    saida_padrao = t.Output.Default,
                    credenciais = t.Credentials is null ? null : new
                    {
                        completo = Probes.CheckCredentials(t).Complete,
                        campos = t.Credentials.Fields.Select(f => f.Label)
                    },
                    servico = t.Network is null ? null : $"{t.Network.Host}:{t.Network.Port}"
                }).ToList();
                output.AppendLine(Json(new { ok = true, pasta_usuario = SettingsService.Current.ToolsFolder, ferramentas = tools },
                    writeIndented: true));
                break;
            }

            case "--probe":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                bool? service = null;
                if (tool.Network is not null)
                    service = Probes.CheckTcpAsync(tool.Network.Host, tool.Network.Port, 3000).GetAwaiter().GetResult();
                object? cred = null;
                if (tool.Credentials is not null && tool.Credentials.Fields.Count > 0)
                {
                    var state = Probes.CheckCredentials(tool);
                    cred = new { completo = state.Complete, faltando = state.Missing };
                }
                output.AppendLine(Json(new { ok = true, ferramenta = tool.Id, servico_responde = service, credenciais = cred }, true));
                break;
            }

            case "--test":
            {
                var tool = catalog.Find(args.ElementAtOrDefault(1) ?? "");
                if (tool is null) return Fail(output, "ferramenta não encontrada");
                var value = args.ElementAtOrDefault(2) ?? "";

                // ferramenta de base local: consulta em memória, sem processo nem rede
                if (tool.IsDataset)
                {
                    var started = System.Diagnostics.Stopwatch.StartNew();
                    var sse = SseCatalog.Current;
                    var matches = sse.Search(value);
                    started.Stop();

                    var top = matches.FirstOrDefault();
                    var formatted = top is null ? "" : SseCatalog.Format(top);
                    output.AppendLine(Json(new
                    {
                        ok = sse.Loaded && matches.Count > 0,
                        ferramenta = tool.Id,
                        tipo = "dataset",
                        base_local = sse.DataPath,
                        cidades_na_base = sse.Cities.Count,
                        cidades_com_nm = sse.TotalWithAccess,
                        linhas = sse.TotalLines,
                        gerado_em = sse.Generated,
                        entrada = value,
                        encontrados = matches.Count,
                        resultados = matches.Take(20).Select(c => new
                        {
                            cidade = c.Name,
                            uf = c.Uf,
                            sigla_anatel = c.Sigla,
                            acesso = c.HasAccess,
                            origem = c.Origin,
                            redes = c.Rows.Select(r => new { nm = r.Nm, olt = r.Olt, vpn = r.Vpn })
                        }),
                        valor = formatted,
                        milissegundos = Math.Round(started.Elapsed.TotalMilliseconds, 3),
                        erro = sse.Error
                    }, true));

                    if (top is not null && args.Contains("--copy"))
                    {
                        ClipboardService.SetText(formatted);
                        HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name,
                            top.Name + " (" + top.Uf + ")", true, started.Elapsed.TotalSeconds, formatted));
                    }

                    Write(output.ToString());
                    return sse.Loaded && top is not null ? 0 : 1;
                }

                var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var first = tool.Inputs.FirstOrDefault();
                if (first is not null && !string.IsNullOrWhiteSpace(value)) inputs[first.Key] = value;

                var result = ToolRunner.RunAsync(tool, inputs, CancellationToken.None).GetAwaiter().GetResult();
                output.AppendLine(Json(new
                {
                    ok = result.Ok,
                    ferramenta = tool.Id,
                    entrada = value,
                    valor = result.Value,
                    detalhe = result.Detail,
                    segundos = Math.Round(result.Elapsed.TotalSeconds, 2),
                    exit = result.ExitCode
                }, true));

                if (result.Ok)
                {
                    ClipboardService.SetText(result.Value);
                    HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, value, true,
                        result.Elapsed.TotalSeconds, result.Value));
                }
                else
                {
                    HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, value, false,
                        result.Elapsed.TotalSeconds, result.Detail));
                }

                Write(output.ToString());
                return result.Ok ? 0 : 1;
            }
        }

        Write(output.ToString());
        return 0;
    }

    private static int Fail(StringBuilder output, string message)
    {
        output.AppendLine(Json(new { ok = false, erro = message }));
        Write(output.ToString());
        return 1;
    }

    private static void Write(string text)
    {
        try { Console.Out.Write(text); Console.Out.Flush(); } catch { }
        try
        {
            AppPaths.Ensure();
            File.WriteAllText(Path.Combine(AppPaths.DataDir, "cli-last.json"), text);
        }
        catch { }
    }

    private static string Json(object value, bool writeIndented = false) =>
        System.Text.Json.JsonSerializer.Serialize(value, new System.Text.Json.JsonSerializerOptions { WriteIndented = writeIndented });
}
