using System.Net.Sockets;
using AnalistaHub.Models;

namespace AnalistaHub.Services;

public enum ProbeState { Unknown, Reachable, Unreachable }

/// <summary>Estado dos requisitos de uma ferramenta. Nunca carrega o valor do segredo.</summary>
public sealed record CredentialState(bool Complete, IReadOnlyList<string> Missing);

/// <summary>Mede o serviço da ferramenta e a presença das credenciais — sem tocar em VPN.</summary>
public static class Probes
{
    public static async Task<bool> CheckTcpAsync(string host, int port, int timeoutMs = 3000, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(host) || port <= 0) return false;
        try
        {
            using var client = new TcpClient();
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeoutMs);
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client.Connected;
        }
        catch { return false; }
    }

    /// <summary>
    /// Credenciais agora são digitadas na interface e ficam cifradas no cofre do HUB.
    /// Devolve só rótulos faltantes — o valor do segredo nunca sai daqui.
    /// </summary>
    public static CredentialState CheckCredentials(ToolManifest tool)
    {
        if (tool.Credentials is null || tool.Credentials.Fields.Count == 0)
            return new CredentialState(true, Array.Empty<string>());

        var missing = tool.Credentials.Fields
            .Where(f => string.IsNullOrWhiteSpace(CredentialStore.Get(tool.Id, f.Key)))
            .Select(f => string.IsNullOrWhiteSpace(f.Label) ? f.Key : f.Label)
            .ToList();

        return new CredentialState(missing.Count == 0, missing);
    }
}

public sealed record HistoryEntry(DateTime When, string ToolId, string ToolName, string Input, bool Ok,
    double Seconds, string Detail);

public static class HistoryStore
{
    private static readonly object Lock = new();

    public static void Append(HistoryEntry e)
    {
        if (!SettingsService.Current.KeepHistory) return;
        try
        {
            AppPaths.Ensure();
            var json = System.Text.Json.JsonSerializer.Serialize(new
            {
                when = e.When.ToString("o"),
                tool = e.ToolId,
                name = e.ToolName,
                input = e.Input,
                ok = e.Ok,
                sec = Math.Round(e.Seconds, 2),
                detail = Truncate(e.Detail, 300)
            });
            lock (Lock) File.AppendAllText(AppPaths.HistoryFile, json + Environment.NewLine);
        }
        catch (Exception ex) { AppPaths.Log("historico falhou: " + ex.Message); }
    }

    public static IEnumerable<HistoryEntry> Recent(string toolId, int count = 5)
    {
        var result = new List<HistoryEntry>();
        try
        {
            if (!File.Exists(AppPaths.HistoryFile)) return result;
            string[] lines;
            lock (Lock) lines = File.ReadAllLines(AppPaths.HistoryFile);
            foreach (var line in lines.Reverse())
            {
                if (result.Count >= count) break;
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    var id = root.GetProperty("tool").GetString() ?? "";
                    if (!string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase)) continue;
                    result.Add(new HistoryEntry(
                        DateTime.TryParse(root.GetProperty("when").GetString(), out var when) ? when : DateTime.Now,
                        id,
                        root.GetProperty("name").GetString() ?? "",
                        root.GetProperty("input").GetString() ?? "",
                        root.GetProperty("ok").GetBoolean(),
                        root.GetProperty("sec").GetDouble(),
                        root.GetProperty("detail").GetString() ?? ""));
                }
                catch { }
            }
        }
        catch { }
        return result;
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
