using Microsoft.UI.Xaml;
using AnalistaHub.Services;
using AnalistaHub.Views;

namespace AnalistaHub;

public partial class App : Application
{
    private static Mutex? _mutex;
    private static EventWaitHandle? _signal;

    public static AppSettings Settings => SettingsService.Current;
    public static ToolCatalog Catalog { get; private set; } = new();
    public static MainWindow? MainWin { get; private set; }
    public static HotkeyService? Hotkey { get; private set; }
    public static TrayIcon? Tray { get; set; }

    /// <summary>Verdadeiro só quando o usuário manda sair pelo menu — aí o X não esconde, fecha.</summary>
    public static bool ForcedExit { get; private set; }

    /// <summary>
    /// Janela invisível que mantém o host XAML do app vivo. Sem ela, fechar a última janela faz o
    /// WinUI desmontar a composição: qualquer janela criada depois disso abre EM BRANCO (o processo
    /// continua vivo pela bandeja, os timers rodam, mas nada renderiza). Ver SPEC §5.4.
    /// </summary>
    private static Window? _keeper;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            AppPaths.Log("UNHANDLED: " + e.Exception);
            e.Handled = true;
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        AppPaths.Ensure();
        SettingsService.Load();

        var cliArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
        if (cliArgs.Contains("--skip-scripts")) ToolCatalog.IgnoreMissingScripts = true;
        if (Cli.IsCliInvocation(cliArgs))
        {
            // modo diagnóstico: roda sem janela e sai
            var code = Cli.Run(cliArgs, ToolCatalog.Load());
            Environment.Exit(code);
            return;
        }

        // ---- instância única ----
        _mutex = new Mutex(true, @"Local\AnalistaHub.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(@"Local\AnalistaHub.ShowSignal", out var existing))
                    existing.Set();
            }
            catch { }
            Environment.Exit(0);
            return;
        }

        try { _signal = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\AnalistaHub.ShowSignal"); }
        catch (Exception ex) { AppPaths.Log("sinal de instância: " + ex.Message); }

        Catalog = ToolCatalog.Load();

        CreateKeeperWindow();

        MainWin = new MainWindow();
        MainWin.Activate();

        if (cliArgs.Contains("--preview-toast"))
        {
            // usado só em teste: mostra um aviso de exemplo sem consultar nada
            var timer = MainWin.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(1.5);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => Views.ToastController.ShowMessage(
                "Analista Hub", "E3210E3C4EE71", "exemplo de aviso · serial da RB com o sufixo \"1\"");
            timer.Start();
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWin);
        Hotkey = new HotkeyService(hwnd);
        Hotkey.Pressed += () => HudController.Toggle();
        if (!Hotkey.Register(Settings.Hotkey, out var hotkeyError))
        {
            AppPaths.Log(hotkeyError);
            MainWin.SetHotkeyWarning(hotkeyError);
        }

        Tray = new TrayIcon("Analista Hub — clique para buscar, botão direito para abrir o menu");
        Tray.LeftClick += () => HudController.Toggle();
        Tray.RightClick += () => ShowMainWindow();
        Tray.MenuProvider = BuildTrayMenu;

        var pageIndex = Array.IndexOf(cliArgs, "--page");
        if (pageIndex >= 0 && pageIndex + 1 < cliArgs.Length)
            MainWin.NavigateTo(cliArgs[pageIndex + 1]);

        // usado em teste e por atalho direto: abre a Consulta SSE já com um termo digitado
        var sseIndex = Array.IndexOf(cliArgs, "--sse");
        if (sseIndex >= 0)
        {
            var term = sseIndex + 1 < cliArgs.Length && !cliArgs[sseIndex + 1].StartsWith("--")
                ? cliArgs[sseIndex + 1]
                : null;
            var sseTimer = MainWin.DispatcherQueue.CreateTimer();
            sseTimer.Interval = TimeSpan.FromMilliseconds(700);
            sseTimer.IsRepeating = false;
            sseTimer.Tick += (_, _) => SsePanelController.Open(term);
            sseTimer.Start();
        }

        if (cliArgs.Contains("--hud"))
        {
            // teste/diagnóstico: abre a HUD; com um id depois da flag, já nos campos da ferramenta
            var alvo = cliArgs.SkipWhile(a => a != "--hud").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            var hudTimer = MainWin.DispatcherQueue.CreateTimer();
            hudTimer.Interval = TimeSpan.FromMilliseconds(900);
            hudTimer.IsRepeating = false;
            hudTimer.Tick += (_, _) => HudController.Show(alvo);
            hudTimer.Start();
        }

        if (cliArgs.Contains("--arm-hotkey"))
            MainWin.ArmHotkeyCapture();

        if (cliArgs.Contains("--validate-once"))
        {
            var validateTimer = MainWin.DispatcherQueue.CreateTimer();
            validateTimer.Interval = TimeSpan.FromMilliseconds(900);
            validateTimer.IsRepeating = false;
            validateTimer.Tick += (_, _) => MainWin.ValidateCredentialsNow();
            validateTimer.Start();
        }

        if (cliArgs.Contains("--close-window"))
        {
            // teste: fecha pelo mesmo caminho do X da barra de título (AppWindow.Close)
            var closeTimer = MainWin.DispatcherQueue.CreateTimer();
            closeTimer.Interval = TimeSpan.FromSeconds(2);
            closeTimer.IsRepeating = false;
            closeTimer.Tick += (_, _) =>
            {
                AppPaths.Log("teste: pedindo fechamento pelo caminho do X");
                MainWin.Close();
            };
            closeTimer.Start();
        }

        StartShowSignalWatcher();
        StartUpdateCheck();
    }

    /// <summary>
    /// Atualização: uma verificação automática por dia (silenciosa se não há novidade) e um aviso
    /// flutuante quando existe versão nova — uma vez por versão, para não repetir todo dia.
    /// O botão "Procurar por atualização agora" (Configurações e menu da bandeja) chama
    /// <see cref="MainWindow.CheckUpdatesNow"/>.
    /// </summary>
    private static void StartUpdateCheck()
    {
        try
        {
            // atualização aplicada no reinício: a pendência já é a versão que está rodando
            if (!string.IsNullOrWhiteSpace(Settings.PendingUpdateVersion) &&
                string.Equals(UpdateService.Normalize(Settings.PendingUpdateVersion),
                              UpdateService.CurrentVersion, StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.Log($"update: rodando {UpdateService.CurrentVersion} — atualização concluída");
                Settings.PendingUpdateVersion = "";
                SettingsService.Save();
            }
            // pacote apagado (rollback ou limpeza manual): a pendência não vale mais
            else if (!string.IsNullOrWhiteSpace(Settings.PendingUpdateVersion) &&
                     UpdateService.PendingPayloadDir() is null)
            {
                AppPaths.Log("update: pacote pendente não existe mais — limpando a pendência");
                Settings.PendingUpdateVersion = "";
                SettingsService.Save();
            }

            if (!Settings.AutoCheckUpdates || !UpdateService.CheckDue(TimeSpan.FromHours(24))) return;

            _ = Task.Run(async () =>
            {
                var result = await UpdateService.CheckAsync();
                UpdateService.LastCheck = result;
                Settings.LastUpdateCheckUtc = result.CheckedAtUtc.ToString("o");
                SettingsService.Save();

                if (!result.HasUpdate) return;
                var version = result.LatestVersion ?? "";
                if (string.Equals(Settings.NotifiedUpdateVersion, version, StringComparison.OrdinalIgnoreCase))
                    return;

                Settings.NotifiedUpdateVersion = version;
                SettingsService.Save();
                AppPaths.Log($"update: avisando sobre a versão {version}");

                MainWin?.DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        Views.ToastController.ShowMessage("Atualização disponível",
                            $"Analista Hub v{version} disponível",
                            "Configurações → Atualizações para instalar");
                    }
                    catch (Exception ex) { AppPaths.Log("aviso de atualização: " + ex.Message); }
                });
            });
        }
        catch (Exception ex) { AppPaths.Log("StartUpdateCheck: " + ex.Message); }
    }

    /// <summary>
    /// Cria a janela guardiã: 1x1, fora do Alt+Tab, invisível e sem conteúdo. Ela nunca aparece na
    /// tela; existe só para o WinUI nunca achar que "acabaram as janelas" e derrubar a composição.
    /// </summary>
    private static void CreateKeeperWindow()
    {
        try
        {
            _keeper = new Window { Title = "AnalistaHubKeeper" };

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_keeper);
            if (hwnd == IntPtr.Zero)
            {
                // HWND só nasce no primeiro Activate; ela fica fora da tela (8x8 em -32000)
                _keeper.Activate();
                hwnd = WinRT.Interop.WindowNative.GetWindowHandle(_keeper);
            }

            var id = Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd);
            var appWindow = Microsoft.UI.Windowing.AppWindow.GetFromWindowId(id);
            appWindow.IsShownInSwitchers = false;
            appWindow.MoveAndResize(new Windows.Graphics.RectInt32(-32000, -32000, 8, 8));
            Native.ShowWindow(hwnd, Native.SW_HIDE);

            AppPaths.Log($"janela guardiã criada: hwnd={hwnd}");
        }
        catch (Exception ex) { AppPaths.Log("janela guardiã falhou: " + ex); }
    }

    /// <summary>
    /// Menu do clique direito na bandeja: nome das ferramentas, Configurações e Fechar aplicativo.
    /// É montado na hora, então ferramenta nova aparece sem reiniciar.
    /// </summary>
    private static IReadOnlyList<TrayMenuItem> BuildTrayMenu()
    {
        var items = new List<TrayMenuItem>();

        foreach (var tool in Catalog.Tools.Where(t => !t.IsBroken))
            items.Add(TrayMenuItem.Tool(tool.Name, () => OpenTool(tool.Id)));

        if (items.Count > 0) items.Add(TrayMenuItem.Separator());

        items.Add(TrayMenuItem.Command("Buscar ferramenta…", () => HudController.Show()));
        items.Add(TrayMenuItem.Command("Configurações", () =>
        {
            ShowMainWindow();
            MainWin?.NavigateTo("settings");
        }));
        items.Add(TrayMenuItem.Command("Procurar atualização agora", () =>
        {
            ShowMainWindow();
            MainWin?.CheckUpdatesNow();
        }));
        items.Add(TrayMenuItem.Separator());
        items.Add(TrayMenuItem.Command("Fechar aplicativo", ExitApp));

        return items;
    }

    private static void OpenTool(string toolId)
    {
        ShowMainWindow();
        MainWin?.NavigateTo(toolId);
    }

    public static void ExitApp()
    {
        ForcedExit = true;
        AppPaths.Log("saindo pelo menu da bandeja");
        try { Tray?.Dispose(); } catch { }
        try { Hotkey?.Dispose(); } catch { }
        try { HudController.HideAll(); } catch { }
        try { SsePanelController.HideAll(); } catch { }
        Application.Current.Exit();
    }

    /// <summary>
    /// Encerra só para o script de atualização trocar a pasta (o script reabre o app). Não marca
    /// ForcedExit: este processo não reinicia sozinho — quem abre o novo é o apply-update.cmd.
    /// </summary>
    public static void ExitForUpdate()
    {
        AppPaths.Log("saindo para aplicar atualização");
        try { Tray?.Dispose(); } catch { }
        try { Hotkey?.Dispose(); } catch { }
        try { HudController.HideAll(); } catch { }
        try { SsePanelController.HideAll(); } catch { }
        Application.Current.Exit();
    }

    private void StartShowSignalWatcher()
    {
        if (_signal is null) return;
        var thread = new Thread(() =>
        {
            while (true)
            {
                try
                {
                    _signal.WaitOne();
                    MainWin?.DispatcherQueue.TryEnqueue(ShowMainWindow);
                }
                catch { return; }
            }
        }) { IsBackground = true, Name = "AnalistaHub.SingleInstance" };
        thread.Start();
    }

    public static void ShowMainWindow()
    {
        try
        {
            if (!EnsureMainWindow()) return;   // janela morta: o app está reiniciando
            if (MainWin is null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWin);
            AppPaths.Log($"ShowMainWindow: hwnd={hwnd} visivel={MainWin.AppWindow.IsVisible}");
            MainWin.AppWindow.Show();
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            MainWin.Activate();
            UiKit.ForceForeground(MainWin);

            // janela em monitor com escala diferente abre em branco até algo redimensionar:
            // agenda um empurrão de 1 px para forçar o repaint
            MainWin.ScheduleRenderKick();

            AppPaths.Log($"ShowMainWindow: fg={Native.GetForegroundWindow()} ok={Native.GetForegroundWindow() == hwnd}");
        }
        catch (Exception ex) { AppPaths.Log("ShowMainWindow: " + ex); }
    }

    /// <summary>
    /// A janela principal pode ter sido destruída (fechada no X numa versão anterior, ou por falha
    /// do WinUI). Se o HWND não existe mais, cria outra — senão "abrir" não faz nada e o app fica
    /// vivo só na bandeja, sem janela nenhuma. O atalho global é re-registrado na janela nova.
    /// </summary>
    private static bool EnsureMainWindow()
    {
        var alive = false;
        if (MainWin is not null)
        {
            try
            {
                var h = WinRT.Interop.WindowNative.GetWindowHandle(MainWin);
                alive = h != IntPtr.Zero && Native.IsWindow(h);
            }
            catch { alive = false; }
        }

        if (alive) return true;

        // Recriar a janela DENTRO deste processo sai em branco: quando a última janela do app é
        // fechada, o WinUI desmonta o host XAML e nada mais renderiza. Comprovado com contagem de
        // cores (1 cor = branco) nos dois monitores. Solução determinística: começar um processo
        // novo, que abre a primeira janela já renderizando — e sair deste.
        AppPaths.Log("janela principal não existe mais — reiniciando o processo para abrir janela nova");
        RelaunchSelf();
        return false;
    }

    /// <summary>
    /// Inicia uma nova instância depois de 2 s (tempo do mutex desta liberar) e encerra esta.
    /// O processo novo abre a janela principal normalmente, no estado salvo em settings.json.
    /// </summary>
    private static void RelaunchSelf()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe))
            {
                AppPaths.Log("relançar: caminho do executável desconhecido");
                return;
            }

            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/c timeout /t 2 /nobreak >nul & start \"\" \"{exe}\"",
                CreateNoWindow = true,
                UseShellExecute = false
            };
            System.Diagnostics.Process.Start(psi);

            ForcedExit = true;
            Application.Current.Exit();
        }
        catch (Exception ex) { AppPaths.Log("relançar falhou: " + ex.Message); }
    }

    /// <summary>Reescreve o atalho global (usado pela tela de configurações).</summary>
    public static bool ApplyHotkey(string combo, out string error)
    {
        error = "";
        if (Hotkey is null) { error = "Atalho global não inicializado."; return false; }

        var ok = Hotkey.Register(combo, out error);
        if (!ok) return false;

        Settings.Hotkey = combo;
        SettingsService.Save();

        // Confere no disco: se a gravação falhou (arquivo travado, disco cheio), o usuário precisa
        // saber AGORA — senão a alteração "some" na próxima abertura e vira um mistério.
        // A comparação é pelo valor lido do JSON, não pelo texto: o serializador grava "+" como
        // \u002B, então procurar a string crua daria falso alarme.
        var relido = SettingsService.ReadFromDisk();
        if (relido is null || !string.Equals(relido.Hotkey, combo, StringComparison.Ordinal))
        {
            error = $"O atalho {combo} está ativo agora, mas não consegui gravar em {AppPaths.SettingsFile}. " +
                    "Ele volta ao anterior quando o app reiniciar.";
            AppPaths.Log($"settings: atalho ativo mas não confirmado no disco — salvo=\"{relido?.Hotkey}\" esperado=\"{combo}\"");
        }

        return true;
    }

    public static void ReloadCatalog()
    {
        Catalog = ToolCatalog.Load();
        HudController.Invalidate();
        MainWin?.RebuildNav();
    }
}
