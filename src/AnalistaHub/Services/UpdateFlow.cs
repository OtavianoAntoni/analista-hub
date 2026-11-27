using Microsoft.UI.Xaml;

namespace AnalistaHub.Services;

/// <summary>Etapas do fluxo único de atualização (UPD-001 revisado).</summary>
public enum UpdateStage
{
    /// <summary>Nada feito nesta sessão — o botão oferece "Buscar atualizações".</summary>
    Idle,
    Checking,
    Downloading,
    /// <summary>Pacote baixado E conferido — o botão passa a "Instalar".</summary>
    Ready,
    Installing,
    /// <summary>Nenhuma versão nova: o botão volta a "Buscar atualizações".</summary>
    UpToDate,
    Failed
}

/// <summary>Estado observável do fluxo — a mesma instância alimenta Configurações e o aviso.</summary>
public sealed record UpdateState(UpdateStage Stage, string Message, double Fraction = 0,
    string? Version = null, string? Channel = null)
{
    public bool Busy => Stage is UpdateStage.Checking or UpdateStage.Downloading or UpdateStage.Installing;
    public bool CanInstall => Stage == UpdateStage.Ready;
}

/// <summary>
/// O fluxo de atualização em UM lugar só (decisão de produto: a notificação automática e
/// Configurações → Atualizações mostram o mesmo fluxo, não duas máquinas de estado).
///
/// Sequência: Buscar atualizações → (achou) download automático com progresso → Instalar →
/// instala e reinicia sem pedir confirmação (decisão 2: consultas são rápidas, reabrir é barato).
/// </summary>
public static class UpdateFlow
{
    private static readonly object Gate = new();
    private static bool _running;

    public static UpdateState Current { get; private set; } =
        new(UpdateStage.Idle, "Ainda não verificado nesta sessão.");

    /// <summary>Disparado a cada transição — sempre na thread que chamou, então a UI só re-renderiza.</summary>
    public static event Action<UpdateState>? Changed;

    private static void Set(UpdateState state)
    {
        Current = state;
        try { Changed?.Invoke(state); }
        catch (Exception ex) { AppPaths.Log("UpdateFlow.Changed: " + ex.Message); }
    }

    /// <summary>
    /// Verifica os canais e, se houver versão nova, baixa e confere sozinho. Falha em qualquer etapa
    /// termina em <see cref="UpdateStage.Failed"/> com mensagem pronta para a tela + retry.
    /// </summary>
    public static async Task<UpdateState> StartAsync()
    {
        lock (Gate)
        {
            if (_running) return Current;   // clique duplo (ou dois botões) não abre dois downloads
            _running = true;
        }

        try
        {
            Set(new UpdateState(UpdateStage.Checking, "Consultando os canais de atualização…"));

            var check = await UpdateService.CheckAsync();
            UpdateService.LastCheck = check;
            App.Settings.LastUpdateCheckUtc = check.CheckedAtUtc.ToString("o");
            SettingsService.Save();

            if (!check.Ok)
            {
                Set(new UpdateState(UpdateStage.Failed,
                    check.Error ?? "Não foi possível verificar atualizações.", Channel: check.Channel));
                return Current;
            }

            if (!check.HasUpdate)
            {
                Set(new UpdateState(UpdateStage.UpToDate,
                    check.Message ?? $"Você está na versão mais recente ({check.CurrentVersion}).",
                    Channel: check.Channel));
                return Current;
            }

            var version = check.LatestVersion ?? "";
            Set(new UpdateState(UpdateStage.Downloading, $"Baixando a versão {version}…", 0, version, check.Channel));

            // O Progress<T> publica na thread de quem criou (UI), então o texto aparece enquanto baixa.
            var progress = new Progress<double>(p => Set(new UpdateState(
                UpdateStage.Downloading,
                p < 0.88 ? $"Baixando a versão {version}… {p:P0}" : $"Conferindo e extraindo… {p:P0}",
                p, version, check.Channel)));

            try
            {
                await UpdateService.DownloadAsync(check, progress);
            }
            catch (Exception ex)
            {
                AppPaths.Log("update: download falhou — " + ex.Message);
                Set(new UpdateState(UpdateStage.Failed,
                    "Não foi possível baixar a atualização: " + ex.Message, Version: version, Channel: check.Channel));
                return Current;
            }

            AppPaths.Log($"update: {version} baixada e conferida (canal {check.Channel})");
            Set(new UpdateState(UpdateStage.Ready,
                $"Versão {version} baixada e conferida. Pode instalar e reiniciar.",
                1, version, check.Channel));
            return Current;
        }
        finally
        {
            lock (Gate) _running = false;
        }
    }

    /// <summary>
    /// Instala o pacote já baixado e reinicia. Sem confirmação: o script de troca reabre o app.
    /// </summary>
    public static bool InstallAndRestart(out string error)
    {
        error = "";
        var payload = UpdateService.PendingPayloadDir();
        if (payload is null)
        {
            error = "Nenhum pacote baixado esperando instalação.";
            Set(new UpdateState(UpdateStage.Failed, error));
            return false;
        }

        Set(Current with { Stage = UpdateStage.Installing, Message = "Instalando e reiniciando…" });
        if (!UpdateService.ApplyAndRestart(payload, out error))
        {
            Set(new UpdateState(UpdateStage.Failed, error));
            return false;
        }

        // O script espera este processo sair, troca a pasta e reabre — não relançamos daqui.
        App.ExitForUpdate();
        return true;
    }

    /// <summary>
    /// Ao abrir a tela: se já existe pacote baixado esperando reinício, o estado é Ready (o botão
    /// precisa oferecer "Instalar", não "Buscar").
    /// </summary>
    public static UpdateState SyncFromDisk()
    {
        if (Current.Busy) return Current;

        var payload = UpdateService.PendingPayloadDir();
        if (payload is not null)
        {
            var version = App.Settings.PendingUpdateVersion;
            Set(new UpdateState(UpdateStage.Ready,
                $"Versão {version} baixada e conferida. Pode instalar e reiniciar.", 1, version));
        }
        return Current;
    }

    /// <summary>Volta ao estado inicial (usado quando o pacote pendente é descartado).</summary>
    public static void Reset() => Set(new UpdateState(UpdateStage.Idle, "Ainda não verificado nesta sessão."));
}
