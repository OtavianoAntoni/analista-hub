using System.Diagnostics;
using System.Text;
using AnalistaHub.Models;

namespace AnalistaHub.Services;

public sealed record RunResult(
    bool Ok,
    string Value,
    string Detail,
    string RawStdout,
    string RawStderr,
    int ExitCode,
    TimeSpan Elapsed,
    bool TimedOut);

/// <summary>
/// Executa o script de uma ferramenta.
/// Contrato novo: -Mode hub devolve JSON {status,value,detail,meta}.
/// Contrato legado: -Mode type devolve o valor no stdout; stdout vazio = falha,
/// e o detalhe é lido do log do próprio script.
/// </summary>
public static class ToolRunner
{
    public static async Task<RunResult> RunAsync(
        ToolManifest tool,
        IReadOnlyDictionary<string, string> inputs,
        CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (!tool.ScriptExists)
            return new RunResult(false, "", $"Script da ferramenta não encontrado:\n{tool.ExpandedScript}", "", "", -1, sw.Elapsed, false);

        var psi = BuildStartInfo(tool, inputs);

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

        try
        {
            if (!process.Start())
                return new RunResult(false, "", "Não foi possível iniciar o script.", "", "", -1, sw.Elapsed, false);

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            var timeout = TimeSpan.FromSeconds(Math.Max(3, tool.TimeoutSeconds));
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeout);

            try
            {
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                var wasTimeout = !ct.IsCancellationRequested;
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { }
                sw.Stop();
                return new RunResult(false, "",
                    wasTimeout ? $"Tempo esgotado ({timeout.TotalSeconds:0} s)." : "Consulta cancelada.",
                    stdout.ToString(), stderr.ToString(), -2, sw.Elapsed, wasTimeout);
            }

            sw.Stop();
            var raw = stdout.ToString().TrimEnd('\r', '\n');
            var err = stderr.ToString().TrimEnd('\r', '\n');

            if (TryParseHubJson(raw, out var json))
            {
                return new RunResult(
                    Ok: json.Ok,
                    Value: json.Value,
                    Detail: json.Ok ? "" : json.Detail,
                    RawStdout: raw, RawStderr: err,
                    ExitCode: process.ExitCode, Elapsed: sw.Elapsed, TimedOut: false);
            }

            // contrato legado: stdout é o valor — a não ser que combine com um padrão de erro
            // declarado no manifesto (scripts antigos escrevem a falha no stdout e saem com 0)
            if (process.ExitCode == 0 && raw.Length > 0)
            {
                if (LooksLikeError(tool, raw))
                {
                    var errorDetail = tool.LegacyErrorPopup
                        ? raw.Trim() + Environment.NewLine + "(o aviso próprio do script também foi exibido)"
                        : raw.Trim();
                    return new RunResult(false, "", errorDetail, raw, err, process.ExitCode, sw.Elapsed, false);
                }
                return new RunResult(true, raw.Trim(), "", raw, err, process.ExitCode, sw.Elapsed, false);
            }

            var detail = DescribeLegacyFailure(tool, raw, err, process.ExitCode);
            if (detail.StartsWith("Usuário ou senha recusados", StringComparison.Ordinal))
                CredentialValidator.MarkRefused(tool.Id, detail);
            return new RunResult(false, "", detail, raw, err, process.ExitCode, sw.Elapsed, false);
        }
        catch (Exception ex)
        {
            sw.Stop();
            return new RunResult(false, "", $"Falha ao executar o script:\n{ex.Message}", stdout.ToString(), stderr.ToString(), -1, sw.Elapsed, false);
        }
    }

    private static ProcessStartInfo BuildStartInfo(ToolManifest tool, IReadOnlyDictionary<string, string> inputs)
    {
        var script = tool.ExpandedScript;
        var scriptDir = Path.GetDirectoryName(script) ?? AppContext.BaseDirectory;

        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = scriptDir
        };

        var interpreter = (tool.Interpreter ?? "powershell").Trim().ToLowerInvariant();
        switch (interpreter)
        {
            case "powershell":
            case "pwsh":
            {
                psi.FileName = interpreter == "pwsh"
                    ? "pwsh.exe"
                    : Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

                psi.ArgumentList.Add("-NoProfile");
                psi.ArgumentList.Add("-ExecutionPolicy");
                psi.ArgumentList.Add("Bypass");
                psi.ArgumentList.Add("-STA");

                // -Command (e não -File) para forçar saída em UTF-8: fabricante/MAC com acento
                var sb = new StringBuilder();
                sb.Append("[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; ");
                sb.Append("& ").Append(PsQuote(script));
                foreach (var arg in tool.ModeArgs.Where(a => !string.IsNullOrWhiteSpace(a)))
                    sb.Append(' ').Append(PsToken(arg));
                foreach (var field in tool.Inputs)
                {
                    if (string.IsNullOrWhiteSpace(field.Arg)) continue;
                    if (!inputs.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                    sb.Append(' ').Append(PsToken(field.Arg)).Append(' ').Append(PsQuote(value));
                }
                psi.ArgumentList.Add("-Command");
                psi.ArgumentList.Add(sb.ToString());
                break;
            }
            case "python":
            {
                psi.FileName = "python.exe";
                psi.ArgumentList.Add(script);
                foreach (var arg in tool.ModeArgs.Where(a => !string.IsNullOrWhiteSpace(a))) psi.ArgumentList.Add(arg);
                foreach (var field in tool.Inputs)
                {
                    if (string.IsNullOrWhiteSpace(field.Arg)) continue;
                    if (!inputs.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                    psi.ArgumentList.Add(field.Arg);
                    psi.ArgumentList.Add(value);
                }
                break;
            }
            default:
            {
                psi.FileName = script;
                foreach (var arg in tool.ModeArgs.Where(a => !string.IsNullOrWhiteSpace(a))) psi.ArgumentList.Add(arg);
                foreach (var field in tool.Inputs)
                {
                    if (string.IsNullOrWhiteSpace(field.Arg)) continue;
                    if (!inputs.TryGetValue(field.Key, out var value) || string.IsNullOrWhiteSpace(value)) continue;
                    psi.ArgumentList.Add(field.Arg);
                    psi.ArgumentList.Add(value);
                }
                break;
            }
        }

        // credenciais: vão por variável de ambiente do processo filho (nunca na linha de comando,
        // que aparece em listagem de processos e em log)
        if (tool.Credentials is not null)
        {
            foreach (var field in tool.Credentials.Fields)
            {
                if (string.IsNullOrWhiteSpace(field.Env)) continue;
                var value = CredentialStore.Get(tool.Id, field.Key);
                if (!string.IsNullOrWhiteSpace(value)) psi.Environment[field.Env] = value;
            }
        }

        return psi;
    }

    private static string PsQuote(string s) => "'" + (s ?? "").Replace("'", "''") + "'";

    private static bool LooksLikeError(ToolManifest tool, string stdout)
    {
        foreach (var pattern in tool.StdoutErrorPatterns)
        {
            if (string.IsNullOrWhiteSpace(pattern)) continue;
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(stdout, pattern,
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase |
                        System.Text.RegularExpressions.RegexOptions.Multiline))
                    return true;
            }
            catch { }
        }
        return false;
    }

    /// <summary>
    /// Nome de parâmetro (-Mode) NÃO pode ir entre aspas: com aspas o PowerShell o trata
    /// como valor posicional e o script recusa o argumento. Só os valores são citados.
    /// </summary>
    private static string PsToken(string s)
    {
        var t = s ?? "";
        if (t.Length > 1 && t[0] == '-' && t.Skip(1).All(char.IsLetterOrDigit)) return t;
        return PsQuote(t);
    }

    private sealed record HubJson(bool Ok, string Value, string Detail);

    private static bool TryParseHubJson(string raw, out HubJson result)
    {
        result = new HubJson(false, "", "");
        if (string.IsNullOrWhiteSpace(raw) || !raw.TrimStart().StartsWith("{")) return false;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(raw);
            var root = doc.RootElement;
            if (!root.TryGetProperty("status", out var statusEl)) return false;
            var status = statusEl.GetString() ?? "";
            var value = root.TryGetProperty("value", out var v) ? (v.GetString() ?? "") : "";
            var detail = root.TryGetProperty("detail", out var d) ? (d.GetString() ?? "") : "";
            result = new HubJson(string.Equals(status, "ok", StringComparison.OrdinalIgnoreCase), value, detail);
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// O script antigo não devolve a mensagem no stdout: ela vai para o log dele
    /// (%TEMP%\mkt_serial.log) e/ou stderr. Lê o fim do log para não perder o motivo.
    /// </summary>
    private static string DescribeLegacyFailure(ToolManifest tool, string raw, string err, int exitCode)
    {
        var parts = new List<string>();
        if (!string.IsNullOrWhiteSpace(raw)) parts.Add(raw.Trim());
        if (!string.IsNullOrWhiteSpace(err)) parts.Add(err.Trim());

        var log = tool.ExpandedLogFile;
        if (!string.IsNullOrWhiteSpace(log) && File.Exists(log))
        {
            try
            {
                var tail = File.ReadLines(log).TakeLast(8).Where(l => !string.IsNullOrWhiteSpace(l)).ToList();

                // login recusado é o caso mais comum e o mais confuso: o log traz só JSON
                var authLine = tail.LastOrDefault(IsAuthFailure);
                if (authLine is not null)
                    return "Usuário ou senha recusados pelo inventário.\n" +
                           "Confira as credenciais na aba da ferramenta (Configurações e credenciais).";

                var meaningful = tail.LastOrDefault(l => LooksLikeDiagnostic(l));
                if (meaningful is not null)
                {
                    var idx = meaningful.IndexOf(' ', meaningful.IndexOf(' ') + 1); // tira data/hora
                    parts.Add(idx > 0 ? meaningful[(idx + 1)..].Trim() : meaningful.Trim());
                }
            }
            catch { }
        }

        if (parts.Count == 0) parts.Add($"O script terminou com código {exitCode} sem devolver valor.");
        if (tool.LegacyErrorPopup)
            parts.Add("(o aviso próprio do script também foi exibido)");

        return string.Join(Environment.NewLine, parts.Distinct());
    }

    private static bool IsAuthFailure(string line) =>
        line.Contains("auth_failed", StringComparison.OrdinalIgnoreCase) ||
        (line.Contains("AUTH", StringComparison.OrdinalIgnoreCase) &&
         (line.Contains("recus", StringComparison.OrdinalIgnoreCase) ||
          line.Contains("inesperada", StringComparison.OrdinalIgnoreCase) ||
          line.Contains("falhou", StringComparison.OrdinalIgnoreCase)));

    private static bool LooksLikeDiagnostic(string line)
    {
        string[] tokens =
        {
            "falhou", "erro", "inesperada", "recusad", "negad",
            "indisponivel", "invalido", "invalida", "nao encontrada", "expirado", "sem sn"
        };
        return tokens.Any(t => line.Contains(t, StringComparison.OrdinalIgnoreCase));
    }
}
