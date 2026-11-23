using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AnalistaHub.Services;

/// <summary>
/// Canal de atualização. O app lê um manifesto publicado junto de cada release do GitHub e, se a
/// versão de lá for maior que a instalada, baixa o pacote .zip, confere o SHA-256, extrai para
/// %LOCALAPPDATA%\AnalistaHub\update\&lt;versao&gt;\payload e troca a pasta do programa (renomeia a
/// antiga, move a nova, reabre; se o app novo não subir, volta a antiga).
///
/// Por que manifesto e não a API do GitHub: o endereço
/// https://github.com/&lt;owner&gt;/&lt;repo&gt;/releases/latest/download/latest.json é um link direto
/// para um arquivo da última release — não passa pela API (sem limite de 60 requisições por hora
/// por IP) e continua valendo se o repositório ficar público depois. Repositório privado exige
/// token, e token não vai embutido em aplicativo distribuído.
/// </summary>
public static class UpdateService
{
    public const string DefaultFeedUrl =
        "https://github.com/OtavianoAntoni/analista-hub/releases/latest/download/latest.json";

    /// <summary>Variável de ambiente que troca o canal (usada nos testes, com servidor local).</summary>
    public const string FeedEnvVar = "ANALISTAHUB_UPDATE_FEED";

    public const string AssetKey = "win-x64";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    /// <summary>Última verificação desta sessão (a tela de Configurações mostra isso sem reconsultar).</summary>
    public static UpdateCheck? LastCheck { get; set; }

    public static string CurrentVersion =>
        typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

    public static string FeedUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable(FeedEnvVar);
            if (!string.IsNullOrWhiteSpace(env)) return env.Trim();
            var configured = SettingsService.Current.UpdateFeedUrl;
            return string.IsNullOrWhiteSpace(configured) ? DefaultFeedUrl : configured.Trim();
        }
    }

    public static bool CheckDue(TimeSpan interval)
    {
        var raw = SettingsService.Current.LastUpdateCheckUtc;
        if (!DateTimeOffset.TryParse(raw, out var last)) return true;
        return DateTimeOffset.UtcNow - last >= interval;
    }

    // ---------------------------------------------------------------- verificação

    public static async Task<UpdateCheck> CheckAsync(CancellationToken ct = default)
    {
        var current = CurrentVersion;
        var now = DateTimeOffset.UtcNow;
        try
        {
            using var http = CreateClient();
            using var resp = await http.GetAsync(FeedUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (resp.StatusCode == HttpStatusCode.NotFound)
                return new UpdateCheck
                {
                    Ok = true, CurrentVersion = current, CheckedAtUtc = now,
                    Message = "O canal de atualização respondeu 404 — nenhuma versão publicada ainda. " +
                              "Se o repositório for privado, o app não consegue ler as releases."
                };

            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                return new UpdateCheck
                {
                    Ok = false, CurrentVersion = current, CheckedAtUtc = now,
                    Error = "O canal de atualização recusou o acesso (HTTP " + (int)resp.StatusCode +
                            "). Repositório privado exige token — este app não guarda token."
                };

            resp.EnsureSuccessStatusCode();
            var json = await resp.Content.ReadAsStringAsync(ct);
            var manifest = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions);
            if (manifest is null || string.IsNullOrWhiteSpace(manifest.Version))
                return new UpdateCheck
                {
                    Ok = false, CurrentVersion = current, CheckedAtUtc = now,
                    Error = "Manifesto de atualização inválido (sem campo \"version\")."
                };

            var latest = Normalize(manifest.Version);
            if (!IsNewer(latest, current))
                return new UpdateCheck
                {
                    Ok = true, CurrentVersion = current, LatestVersion = latest, CheckedAtUtc = now,
                    Message = $"Você está na versão mais recente ({current})."
                };

            var asset = PickAsset(manifest);
            if (asset is null)
                return new UpdateCheck
                {
                    Ok = false, CurrentVersion = current, LatestVersion = latest, Notes = manifest.Notes,
                    CheckedAtUtc = now,
                    Error = $"A versão {latest} foi publicada sem pacote para {AssetKey}."
                };

            if (string.IsNullOrWhiteSpace(asset.Sha256))
                return new UpdateCheck
                {
                    Ok = false, CurrentVersion = current, LatestVersion = latest, Notes = manifest.Notes,
                    CheckedAtUtc = now,
                    Error = $"O pacote da versão {latest} não publicou o SHA-256; download recusado por segurança."
                };

            AppPaths.Log($"update: versão {latest} disponível (instalada {current})");
            return new UpdateCheck
            {
                Ok = true, CurrentVersion = current, LatestVersion = latest, Notes = manifest.Notes,
                Asset = asset, CheckedAtUtc = now,
                Message = $"Atualização disponível: {latest}."
            };
        }
        catch (Exception ex)
        {
            AppPaths.Log("update check falhou: " + ex.Message);
            return new UpdateCheck
            {
                Ok = false, CurrentVersion = current, CheckedAtUtc = now,
                Error = "Não foi possível consultar o canal de atualização: " + ex.Message
            };
        }
    }

    private static UpdateAsset? PickAsset(UpdateManifest manifest)
    {
        if (manifest.Assets.TryGetValue(AssetKey, out var exact) && !string.IsNullOrWhiteSpace(exact.Url))
            return exact;
        // tolerância: manifesto antigo sem chave por arquitetura
        foreach (var pair in manifest.Assets)
            if (!string.IsNullOrWhiteSpace(pair.Value.Url)) return pair.Value;
        return null;
    }

    // ---------------------------------------------------------------- download

    /// <summary>Baixa o pacote, confere o SHA-256 e extrai. Devolve a pasta com o app novo.</summary>
    public static async Task<string> DownloadAsync(UpdateCheck check, IProgress<double>? progress = null,
        CancellationToken ct = default)
    {
        var asset = check.Asset ?? throw new InvalidOperationException("Nenhum pacote para baixar.");
        var version = Normalize(check.LatestVersion ?? "");
        var versionDir = Path.Combine(AppPaths.UpdateDir, version);
        var zipPath = Path.Combine(versionDir, "package.zip");
        var payloadDir = Path.Combine(versionDir, "payload");

        Directory.CreateDirectory(versionDir);
        progress?.Report(0);

        var hashOk = File.Exists(zipPath) && string.Equals(Sha256Of(zipPath), asset.Sha256,
            StringComparison.OrdinalIgnoreCase);

        if (!hashOk)
        {
            if (File.Exists(zipPath)) File.Delete(zipPath);
            using var http = CreateClient();
            using var resp = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();
            var total = resp.Content.Headers.ContentLength ?? asset.Size;
            await using (var source = await resp.Content.ReadAsStreamAsync(ct))
            await using (var target = File.Create(zipPath))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await source.ReadAsync(buffer, ct)) > 0)
                {
                    await target.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total > 0) progress?.Report(0.85 * Math.Min(1.0, (double)read / total));
                }
            }

            var actual = Sha256Of(zipPath);
            if (!string.Equals(actual, asset.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(zipPath); } catch { }
                throw new InvalidDataException(
                    $"SHA-256 do pacote não confere (esperado {asset.Sha256}, recebido {actual}). Download descartado.");
            }
        }

        progress?.Report(0.9);
        if (Directory.Exists(payloadDir)) Directory.Delete(payloadDir, true);
        Directory.CreateDirectory(payloadDir);
        ZipFile.ExtractToDirectory(zipPath, payloadDir, overwriteFiles: true);
        progress?.Report(0.97);

        var payloadExeDir = FindPayloadRoot(payloadDir);
        if (payloadExeDir is null)
            throw new InvalidDataException("Pacote inválido: AnalistaHub.exe não encontrado dentro do .zip.");

        SettingsService.Current.PendingUpdateVersion = version;
        SettingsService.Save();
        progress?.Report(1.0);
        AppPaths.Log($"update: pacote {version} pronto em {payloadExeDir}");
        return payloadExeDir;
    }

    /// <summary>Aceita zip com o conteúdo na raiz ou dentro de uma única pasta.</summary>
    private static string? FindPayloadRoot(string payloadDir)
    {
        if (File.Exists(Path.Combine(payloadDir, "AnalistaHub.exe"))) return payloadDir;
        foreach (var dir in Directory.GetDirectories(payloadDir))
            if (File.Exists(Path.Combine(dir, "AnalistaHub.exe"))) return dir;
        return null;
    }

    public static string? PendingPayloadDir()
    {
        var version = SettingsService.Current.PendingUpdateVersion;
        if (string.IsNullOrWhiteSpace(version)) return null;
        var payload = Path.Combine(AppPaths.UpdateDir, version, "payload");
        var root = Directory.Exists(payload) ? FindPayloadRoot(payload) : null;
        return root;
    }

    // ---------------------------------------------------------------- aplicação

    public static string InstallDir =>
        (Environment.ProcessPath is not null
            ? Path.GetDirectoryName(Environment.ProcessPath)!
            : AppContext.BaseDirectory).TrimEnd('\\', '/');

    /// <summary>
    /// Agenda a troca da pasta: grava apply-update.cmd, dispara desanexado e o chamador encerra o app.
    /// O script espera este processo sair, renomeia a pasta atual, move a nova, reabre e — se o app
    /// novo não subir — devolve a pasta antiga.
    /// </summary>
    public static bool ApplyAndRestart(string payloadDir, out string error)
    {
        error = "";
        try
        {
            if (!File.Exists(Path.Combine(payloadDir, "AnalistaHub.exe")))
            {
                error = "O pacote baixado não contém AnalistaHub.exe.";
                return false;
            }

            var install = InstallDir;
            if (install.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
                install.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
            {
                error = "Atualização automática não se aplica rodando de pasta de build. Use dist\\AnalistaHub.";
                return false;
            }

            if (!IsWritable(install))
            {
                error = $"A pasta do programa não é gravável ({install}). Peça ao instalador para atualizar.";
                return false;
            }

            Directory.CreateDirectory(AppPaths.UpdateDir);
            var script = Path.Combine(AppPaths.UpdateDir, "apply-update.cmd");
            var pid = Environment.ProcessId;
            var exe = Path.Combine(install, "AnalistaHub.exe");
            File.WriteAllBytes(script, Encoding.UTF8.GetBytes(BuildApplyScript(pid, payloadDir, install, exe)));

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c \"\"{script}\"\"",
                WorkingDirectory = Path.GetTempPath(),
                CreateNoWindow = true,
                UseShellExecute = false
            };
            Process.Start(psi);
            AppPaths.Log($"update: aplicação agendada (pid={pid}, origem={payloadDir}, destino={install})");
            return true;
        }
        catch (Exception ex)
        {
            error = "Não foi possível agendar a atualização: " + ex.Message;
            AppPaths.Log("update apply: " + ex);
            return false;
        }
    }

    private static string BuildApplyScript(int pid, string payload, string install, string exe)
    {
        var log = Path.Combine(AppPaths.UpdateDir, "apply.log");
        var versionDir = Directory.GetParent(payload)?.FullName ?? payload;
        // CRLF explícito: cmd.exe com LF engole a última linha do bloco.
        // TODO comando externo vai por caminho absoluto (%SYS%): se o PATH do usuário tiver um
        // "find" ou "tasklist" diferente (Git/MSYS, por exemplo), a sonda falharia e o rollback
        // apagaria uma instalação boa. Custou uma tarde de diagnóstico.
        return string.Join("\r\n", new[]
        {
            "@echo off",
            "setlocal EnableExtensions",
            "set \"SYS=%SystemRoot%\\System32\"",
            "set \"TASKLIST=%SYS%\\tasklist.exe\"",
            "set \"FINDSTR=%SYS%\\findstr.exe\"",
            "set \"ROBOCOPY=%SYS%\\robocopy.exe\"",
            "set \"PING=%SYS%\\PING.EXE\"",
            $"set \"PID={pid}\"",
            $"set \"SRC={payload}\"",
            $"set \"DST={install}\"",
            $"set \"EXE={exe}\"",
            $"set \"LOG={log}\"",
            $"set \"UPD={versionDir}\"",
            "echo [%DATE% %TIME%] inicio aplicar atualizacao >> \"%LOG%\"",
            "set /a N=0",
            ":wait",
            "\"%TASKLIST%\" /FI \"PID eq %PID%\" /NH 2>nul | \"%FINDSTR%\" /C:\" %PID% \" >nul",
            "if errorlevel 1 goto swap",
            "set /a N+=1",
            "if %N% GEQ 180 goto swap",
            "\"%PING%\" -n 2 127.0.0.1 >nul",
            "goto wait",
            ":swap",
            "echo [%DATE% %TIME%] processo saiu; trocando pasta >> \"%LOG%\"",
            "set \"BAK=%DST%.old\"",
            "set \"MODE=move\"",
            "if exist \"%BAK%\" rd /s /q \"%BAK%\" >> \"%LOG%\" 2>&1",
            "move \"%DST%\" \"%BAK%\" >> \"%LOG%\" 2>&1",
            "if errorlevel 1 goto copyway",
            "move \"%SRC%\" \"%DST%\" >> \"%LOG%\" 2>&1",
            "if errorlevel 1 goto rollback",
            "goto launch",
            ":copyway",
            "rem pasta em uso por outro processo (diretorio corrente): espelha em vez de renomear",
            "echo [%DATE% %TIME%] renomear falhou; usando robocopy >> \"%LOG%\"",
            "set \"MODE=copy\"",
            "\"%ROBOCOPY%\" \"%DST%\" \"%BAK%\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS >> \"%LOG%\" 2>&1",
            "if errorlevel 8 goto failed",
            "\"%ROBOCOPY%\" \"%SRC%\" \"%DST%\" /E /R:2 /W:1 /NFL /NDL /NJH /NJS >> \"%LOG%\" 2>&1",
            "if errorlevel 8 goto rollback",
            ":launch",
            "start \"\" /d \"%DST%\" \"%EXE%\"",
            "set /a N=0",
            ":probe",
            "\"%PING%\" -n 3 127.0.0.1 >nul",
            "\"%TASKLIST%\" /FI \"IMAGENAME eq AnalistaHub.exe\" /NH 2>nul > \"%TEMP%\\analistahub-probe.txt\"",
            "\"%FINDSTR%\" /I \"AnalistaHub.exe\" \"%TEMP%\\analistahub-probe.txt\" >nul",
            "if not errorlevel 1 goto ok",
            "set /a N+=1",
            "if %N% LSS 30 goto probe",
            "echo [%DATE% %TIME%] app novo nao subiu em 60s; voltando >> \"%LOG%\"",
            "\"%FINDSTR%\" /I \"AnalistaHub\" \"%TEMP%\\analistahub-probe.txt\" >> \"%LOG%\" 2>&1",
            "goto rollback",
            ":ok",
            "echo [%DATE% %TIME%] atualizacao concluida >> \"%LOG%\"",
            "if exist \"%BAK%\" rd /s /q \"%BAK%\" >> \"%LOG%\" 2>&1",
            "rd /s /q \"%UPD%\" >> \"%LOG%\" 2>&1",
            "exit /b 0",
            ":rollback",
            "rem so apaga a pasta nova se der; se houver arquivo travado, NAO insiste",
            "if exist \"%DST%\" rd /s /q \"%DST%\" >> \"%LOG%\" 2>&1",
            "if exist \"%DST%\" goto rbfailed",
            "if exist \"%BAK%\" move \"%BAK%\" \"%DST%\" >> \"%LOG%\" 2>&1",
            "if not exist \"%DST%\\AnalistaHub.exe\" goto rbfailed",
            "start \"\" /d \"%DST%\" \"%EXE%\"",
            "rd /s /q \"%UPD%\" >> \"%LOG%\" 2>&1",
            "echo [%DATE% %TIME%] revertido >> \"%LOG%\"",
            "exit /b 1",
            ":rbfailed",
            "rd /s /q \"%UPD%\" >> \"%LOG%\" 2>&1",
            "echo [%DATE% %TIME%] nao foi possivel reverter; copia antiga em %BAK% >> \"%LOG%\"",
            "exit /b 3",
            ":failed",
            "echo [%DATE% %TIME%] falhou ao preparar a troca de pasta >> \"%LOG%\"",
            "exit /b 2",
            ""
        });
    }

    // ---------------------------------------------------------------- utilitários

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("AnalistaHub-Updater/1.0");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    private static bool IsWritable(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, ".update-probe-" + Guid.NewGuid().ToString("N")[..8]);
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    public static string Normalize(string version)
    {
        var v = (version ?? "").Trim();
        if (v.StartsWith('v') || v.StartsWith('V')) v = v[1..];
        var cut = v.IndexOfAny(new[] { '-', '+' });
        if (cut > 0) v = v[..cut];
        return v.Trim();
    }

    /// <summary>Compara versões numéricas ("0.2.0" &gt; "0.1.9"). Texto sem número perde de qualquer versão.</summary>
    public static bool IsNewer(string latest, string current)
    {
        if (!TryParse(latest, out var a)) return false;
        if (!TryParse(current, out var b)) return true;
        return a > b;
    }

    private static bool TryParse(string text, out Version version)
    {
        version = new Version(0, 0, 0, 0);
        var v = Normalize(text);
        if (string.IsNullOrWhiteSpace(v)) return false;
        var parts = v.Split('.');
        if (parts.Length is < 1 or > 4) return false;
        var nums = new int[4];
        for (var i = 0; i < parts.Length; i++)
            if (!int.TryParse(parts[i], out nums[i]) || nums[i] < 0) return false;
        version = new Version(nums[0], nums[1], nums[2], nums[3]);
        return true;
    }

    /// <summary>Texto humano do estado da última verificação (usado na tela e no CLI).</summary>
    public static string Describe(UpdateCheck check)
    {
        if (!check.Ok) return check.Error ?? "Falha ao verificar atualizações.";
        if (check.HasUpdate)
            return $"Atualização disponível: v{check.LatestVersion} (instalada: v{check.CurrentVersion}).";
        return check.Message ?? $"Você está na versão mais recente (v{check.CurrentVersion}).";
    }
}

public sealed class UpdateManifest
{
    public int Schema { get; set; } = 1;
    public string Version { get; set; } = "";
    public string? Notes { get; set; }
    public string? PublishedAt { get; set; }
    public Dictionary<string, UpdateAsset> Assets { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class UpdateAsset
{
    public string Url { get; set; } = "";
    public string? Sha256 { get; set; }
    public long Size { get; set; }
}

/// <summary>Resultado de uma verificação. HasUpdate só é verdadeiro com pacote e hash presentes.</summary>
public sealed class UpdateCheck
{
    public bool Ok { get; init; }
    public string CurrentVersion { get; init; } = "";
    public string? LatestVersion { get; init; }
    public string? Notes { get; init; }
    public UpdateAsset? Asset { get; init; }
    public string? Message { get; init; }
    public string? Error { get; init; }
    public DateTimeOffset CheckedAtUtc { get; init; }
    public bool HasUpdate => Ok && Asset is not null && !string.IsNullOrWhiteSpace(LatestVersion);
}
