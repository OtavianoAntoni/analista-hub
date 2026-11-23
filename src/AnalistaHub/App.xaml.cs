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

        items.Add(TrayMenuItem.Command("Buscar ferramenta…", HudController.Show));
        items.Add(TrayMenuItem.Command("Configurações", () =>
        {
            ShowMainWindow();
            MainWin?.NavigateTo("settings");
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
            EnsureMainWindow();
            if (MainWin is null) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWin);
            AppPaths.Log($"ShowMainWindow: hwnd={hwnd} visivel={MainWin.AppWindow.IsVisible}");
            MainWin.AppWindow.Show();
            Native.ShowWindow(hwnd, Native.SW_RESTORE);
            MainWin.Activate();
            UiKit.ForceForeground(MainWin);
            AppPaths.Log($"ShowMainWindow: fg={Native.GetForegroundWindow()} ok={Native.GetForegroundWindow() == hwnd}");
        }
        catch (Exception ex) { AppPaths.Log("ShowMainWindow: " + ex); }
    }

    /// <summary>
    /// A janela principal pode ter sido destruída (fechada no X numa versão anterior, ou por falha
    /// do WinUI). Se o HWND não existe mais, cria outra — senão "abrir" não faz nada e o app fica
    /// vivo só na bandeja, sem janela nenhuma. O atalho global é re-registrado na janela nova.
    /// </summary>
    private static void EnsureMainWindow()
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

        if (alive) return;

        AppPaths.Log("janela principal não existe mais — recriando");
        MainWin = new MainWindow();

        // volta para a tela onde o usuário estava quando fechou a janela
        if (!string.IsNullOrWhiteSpace(Settings.LastPage))
        {
            try { MainWin.NavigateTo(Settings.LastPage); } catch { }
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(MainWin);
        try { Hotkey?.Dispose(); } catch { }
        Hotkey = new HotkeyService(hwnd);
        Hotkey.Pressed += () => HudController.Toggle();
        if (!Hotkey.Register(Settings.Hotkey, out var error))
        {
            AppPaths.Log("atalho após recriar janela: " + error);
            MainWin.SetHotkeyWarning(error);
        }
    }

    /// <summary>Reescreve o atalho global (usado pela tela de configurações).</summary>
    public static bool ApplyHotkey(string combo, out string error)
    {
        error = "";
        if (Hotkey is null) { error = "Atalho global não inicializado."; return false; }
        var ok = Hotkey.Register(combo, out error);
        if (ok)
        {
            Settings.Hotkey = combo;
            SettingsService.Save();
        }
        return ok;
    }

    public static void ReloadCatalog()
    {
        Catalog = ToolCatalog.Load();
        HudController.Invalidate();
        MainWin?.RebuildNav();
    }
}
