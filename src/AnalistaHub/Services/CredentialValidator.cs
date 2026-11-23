using System.Net.Http;
using System.Text;
using System.Text.Json;
using AnalistaHub.Models;

namespace AnalistaHub.Services;

public enum AuthState { Unknown, Ok, Refused, Unreachable }

public sealed record AuthResult(AuthState State, string Message, DateTime When)
{
    public static AuthResult Unknown { get; } = new(AuthState.Unknown, "", DateTime.MinValue);
}

/// <summary>
/// Confirma com o próprio serviço se as credenciais digitadas valem.
/// Só é chamado quando o usuário interage com os campos (regra da SPEC §6.6) — nunca em segundo
/// plano, nunca no carregamento da tela.
/// O corpo da requisição contém a senha: nada aqui é logado, e a resposta é descartada depois de
/// ler apenas o campo de sucesso/mensagem.
/// </summary>
public static class CredentialValidator
{
    private static readonly HttpClient Http = new(new HttpClientHandler { UseProxy = false })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    private static readonly Dictionary<string, AuthResult> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();
    private static bool _loaded;

    private static void LoadPersisted()
    {
        if (_loaded) return;
        lock (Gate)
        {
            if (_loaded) return;
            foreach (var (toolId, raw) in SettingsService.Current.AuthStatus)
            {
                var parts = (raw ?? "").Split('|');
                if (parts.Length < 2) continue;
                var state = parts[0] switch
                {
                    "ok" => AuthState.Ok,
                    "refused" => AuthState.Refused,
                    "unreachable" => AuthState.Unreachable,
                    _ => AuthState.Unknown
                };
                _ = DateTime.TryParse(parts[1], out var when);
                Cache[toolId] = new AuthResult(state, parts.Length > 2 ? parts[2] : "", when);
            }
            _loaded = true;
        }
    }

    private static void Persist(string toolId, AuthResult result)
    {
        SettingsService.Current.AuthStatus[toolId] =
            $"{result.State.ToString().ToLowerInvariant()}|{result.When:o}|{(result.Message ?? "").Replace('|', '/').Replace('\n', ' ')}";
        SettingsService.Save();
    }

    public static AuthResult LastKnown(string toolId)
    {
        LoadPersisted();
        lock (Gate) return Cache.TryGetValue(toolId, out var r) ? r : AuthResult.Unknown;
    }

    public static void Remember(string toolId, AuthResult result)
    {
        lock (Gate) Cache[toolId] = result;
        Persist(toolId, result);
    }

    /// <summary>Falha de autenticação detectada durante uma execução real também alimenta o status.</summary>
    public static void MarkRefused(string toolId, string message) =>
        Remember(toolId, new AuthResult(AuthState.Refused, message, DateTime.Now));

    public static bool CanValidate(ToolManifest tool) =>
        tool.Credentials?.Validation is { Url.Length: > 0, OkJsonPath.Length: > 0 };

    public static async Task<AuthResult> ValidateAsync(ToolManifest tool, CancellationToken ct)
    {
        var validation = tool.Credentials?.Validation;
        if (validation is null || string.IsNullOrWhiteSpace(validation.Url))
            return Finish(tool.Id, new AuthResult(AuthState.Unknown, "ferramenta sem regra de validação", DateTime.Now));

        var missing = Probes.CheckCredentials(tool);
        if (!missing.Complete)
            return Finish(tool.Id, new AuthResult(AuthState.Unknown,
                "faltando: " + string.Join(", ", missing.Missing), DateTime.Now));

        var user = CredentialStore.Get(tool.Id, "user");
        var pass = CredentialStore.Get(tool.Id, "passwd");

        var url = validation.Url;
        var body = (validation.BodyTemplate ?? "")
            .Replace("{user}", JsonEscape(user))
            .Replace("{passwd}", JsonEscape(pass));

        try
        {
            using var request = new HttpRequestMessage(
                string.Equals(validation.Method, "GET", StringComparison.OrdinalIgnoreCase)
                    ? HttpMethod.Get : HttpMethod.Post, url);

            if (request.Method == HttpMethod.Post)
                request.Content = new StringContent(body, Encoding.UTF8, validation.ContentType);

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Clamp(validation.TimeoutSeconds, 3, 30)));

            using var response = await Http.SendAsync(request, cts.Token).ConfigureAwait(false);
            var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode && (int)response.StatusCode is not (400 or 401 or 403))
            {
                // 401/400 com JSON de erro ainda são resposta do serviço: vale ler o corpo
                return Finish(tool.Id, new AuthResult(AuthState.Unreachable,
                    $"serviço respondeu HTTP {(int)response.StatusCode}", DateTime.Now));
            }

            var ok = false;
            var message = "";
            try
            {
                using var doc = JsonDocument.Parse(text);
                var value = ReadPath(doc.RootElement, validation.OkJsonPath);
                ok = string.Equals(value, validation.OkEquals, StringComparison.OrdinalIgnoreCase);
                if (!string.IsNullOrWhiteSpace(validation.MessagePath))
                    message = ReadPath(doc.RootElement, validation.MessagePath) ?? "";
            }
            catch (JsonException)
            {
                return Finish(tool.Id, new AuthResult(AuthState.Unreachable,
                    "resposta inesperada do serviço (não é JSON)", DateTime.Now));
            }

            if (string.IsNullOrWhiteSpace(message))
                message = ok ? "credenciais aceitas" : "credenciais recusadas";

            return Finish(tool.Id, new AuthResult(ok ? AuthState.Ok : AuthState.Refused, message, DateTime.Now));
        }
        catch (OperationCanceledException)
        {
            return Finish(tool.Id, new AuthResult(AuthState.Unreachable, "tempo esgotado ao validar", DateTime.Now));
        }
        catch (Exception ex)
        {
            AppPaths.Log("validação de credencial falhou: " + ex.GetType().Name);
            return Finish(tool.Id, new AuthResult(AuthState.Unreachable,
                "sem resposta do serviço — verifique a VPN", DateTime.Now));
        }
    }

    private static AuthResult Finish(string toolId, AuthResult result)
    {
        Remember(toolId, result);
        return result;
    }

    private static string JsonEscape(string value) =>
        JsonEncodedText.Encode(value ?? "").ToString();

    private static string? ReadPath(JsonElement root, string path)
    {
        var current = root;
        foreach (var part in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Object && current.TryGetProperty(part, out var next))
                current = next;
            else
                return null;
        }

        return current.ValueKind switch
        {
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            JsonValueKind.String => current.GetString(),
            JsonValueKind.Number => current.GetRawText(),
            JsonValueKind.Null => null,
            _ => null
        };
    }
}
