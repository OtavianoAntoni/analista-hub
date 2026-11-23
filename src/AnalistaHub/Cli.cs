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
            or "--set-cred" or "--clear-cred" or "--set-trigger";

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
                break;

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
                    gatilho = SettingsService.TriggerFor(t),
                    estado = t.IsBroken ? "com problema: " + t.LoadError : "ok",
                    script = t.ExpandedScript,
                    script_existe = t.ScriptExists,
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
