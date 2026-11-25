using Microsoft.UI.Xaml;
using AnalistaHub.Models;
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

    /// <summary>
    /// Timers dos diagnósticos de linha de comando (`--hud`, `--fill`, `--cycle`, `--submit`).
    /// Precisam de referência forte: um DispatcherQueueTimer guardado só numa variável local pode ser
    /// recolhido pelo GC antes do Tick — foi o que fez o `--submit` (3800 ms) não disparar em parte
    /// das execuções, deixando a prova do SQ-004 dependente da sorte.
    /// </summary>
    private static readonly List<Microsoft.UI.Dispatching.DispatcherQueueTimer> DiagnosticTimers = new();

    private static void After(int milliseconds, Action action)
    {
        if (MainWin is null) return;

        var timer = MainWin.DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            DiagnosticTimers.Remove(timer);
            action();
        };
        DiagnosticTimers.Add(timer);
        timer.Start();
    }

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

        // SIG-001: primeira execução depois de instalar/atualizar assina os tools\*.ps1 — antes de
        // qualquer ferramenta rodar. Em segundo plano para não atrasar a janela; o resultado vai para
        // o hub.log e para Configurações → Assinatura local.
        _ = Task.Run(() =>
        {
            try
            {
                var assinatura = ToolSigning.Ensure();
                AppPaths.Log($"assinatura local: {assinatura.Status} — {assinatura.Motivo}");
            }
            catch (Exception ex) { AppPaths.Log("assinatura local falhou: " + ex.Message); }
        });

        // Primeira execução (ou pasta de dados apagada): o arquivo de configuração local nasce com
        // as chaves que o catálogo pediu, para o usuário só preencher — e a ferramenta já aponta o
        // que falta em vez de falar em "erro de comunicação" (NET-003).
        LocalConfig.EnsureTemplate(LocalConfig.Faltando);

        // NET-005: a descoberta da lista de NMs pode tocar um share (UNC morto pendura File.Exists).
        // Carrega fora do thread da interface para a janela abrir na hora mesmo com a pasta da equipe
        // fora do ar; quem precisar da base depois espera o mesmo carregamento (há uma trava só).
        _ = Task.Run(() =>
        {
            try
            {
                var base_ = SseCatalog.Current;
                AppPaths.Log($"pré-carga da base: {base_.Cities.Count} cidades — {base_.RedeResumo}");
            }
            catch (Exception ex) { AppPaths.Log("pré-carga da base falhou: " + ex.Message); }
        });

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
        Hotkey.ToolPressed += OnToolHotkey;
        if (!Hotkey.Register(Settings.Hotkey, out var hotkeyError))
        {
            AppPaths.Log(hotkeyError);
            MainWin.SetHotkeyWarning(hotkeyError);
        }

        // atalhos por ferramenta: cada um independente; recusa entra em HotkeyProblems
        ApplyToolHotkeys();

        // CFG-004: o clique esquerdo (e o duplo, que chega como dois UP — a janela "STATIC" não tem
        // CS_DBLCLKS) segue a preferência. A ação é IDEMPOTENTE: abrir a busca não alterna, senão o
        // duplo clique fecharia o que acabou de abrir.
        Tray = new TrayIcon(TooltipBandeja());
        Tray.LeftClick += OnTrayLeftClick;
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

        // teste/diagnóstico: troca o tema em tempo de execução, pelo mesmo caminho do combo em
        // Configurações — serve para provar que não é preciso reiniciar o aplicativo.
        var temaIdx = Array.IndexOf(cliArgs, "--theme-switch");
        if (temaIdx >= 0 && temaIdx + 1 < cliArgs.Length)
        {
            var alvo = cliArgs[temaIdx + 1];
            var timer = MainWin.DispatcherQueue.CreateTimer();
            timer.Interval = TimeSpan.FromSeconds(4);
            timer.IsRepeating = false;
            timer.Tick += (_, _) =>
            {
                App.Settings.Theme = alvo;
                SettingsService.Save();
                AppPaths.Log($"teste: trocando tema para {alvo} em tempo de execução");
                ThemeService.Apply();
            };
            timer.Start();
        }

        if (cliArgs.Contains("--hud"))
        {
            // teste/diagnóstico: abre a HUD; com um id depois da flag, já nos campos da ferramenta
            var alvo = cliArgs.SkipWhile(a => a != "--hud").Skip(1).FirstOrDefault(a => !a.StartsWith("--"));
            After(900, () => HudController.Show(alvo));

            // NET-001: com "--fill <valor>", o primeiro campo recebe o texto pelo MESMO caminho da
            // digitação (TextChanged → ping). Evita teclado sintético na máquina de quem trabalha.
            var fillIdx = Array.IndexOf(cliArgs, "--fill");
            if (fillIdx >= 0 && fillIdx + 1 < cliArgs.Length)
            {
                var valor = cliArgs[fillIdx + 1];
                After(1900, () => HudController.FillFirstField(valor));

                // "--submit": executa a ferramenta pelo mesmo caminho do Enter (prova do aviso).
                // 3800 ms = HUD (900) + 2 passos de fill (1900 + 700) + debounce (400) com folga.
                if (cliArgs.Contains("--submit"))
                    After(3800, () =>
                    {
                        AppPaths.Log("hud --submit: disparando");
                        HudController.Submit();
                    });
            }

            // SQ-004: "--cycle <id>" troca de ferramenta pelo mesmo caminho das setas (↑/↓) com o
            // painel de campos aberto — prova o roteamento sem teclado sintético.
            var cycleIdx = Array.IndexOf(cliArgs, "--cycle");
            if (cycleIdx >= 0 && cycleIdx + 1 < cliArgs.Length)
            {
                var alvoCiclo = cliArgs[cycleIdx + 1];
                After(fillIdx >= 0 ? 2600 : 1900, () => HudController.CycleTo(alvoCiclo));
            }
        }

        if (cliArgs.Contains("--arm-hotkey"))
            MainWin.ArmHotkeyCapture();

        // CFG-004: dispara o clique esquerdo da bandeja pelo MESMO caminho de produção, sem tocar no
        // ícone (o tray não aceita clique sintético de forma confiável).
        if (cliArgs.Contains("--tray-click"))
        {
            After(1500, () =>
            {
                AppPaths.Log("diagnóstico: simulando clique esquerdo na bandeja");
                OnTrayLeftClick();
            });
        }

        // Diagnóstico visual: rola a página para provar um bloco abaixo da dobra (ex.: CFG-004).
        var scrollIdx = Array.IndexOf(cliArgs, "--scroll-y");
        if (scrollIdx >= 0 && scrollIdx + 1 < cliArgs.Length &&
            double.TryParse(cliArgs[scrollIdx + 1], out var rolagem))
        {
            After(1500, () => MainWin.ScrollContentTo(rolagem));
        }

        // NET-004 / CFG-003: dispara o atalho de uma ferramenta pelo caminho de produção
        // (atalho global por ferramenta), sem teclado sintético.
        var toolHotkeyIdx = Array.IndexOf(cliArgs, "--tool-hotkey");
        if (toolHotkeyIdx >= 0 && toolHotkeyIdx + 1 < cliArgs.Length)
        {
            var alvoAtalho = cliArgs[toolHotkeyIdx + 1];
            After(1200, () =>
            {
                AppPaths.Log($"diagnóstico: simulando atalho da ferramenta «{alvoAtalho}»");
                SimulateToolHotkey(alvoAtalho);
            });
        }

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

        items.Add(TrayMenuItem.Command("Início", () =>
        {
            ShowMainWindow();
            MainWin?.NavigateTo("home");
        }));
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

    /// <summary>Tooltip do ícone refletindo a preferência do clique esquerdo (CFG-004).</summary>
    public static string TooltipBandeja() =>
        SettingsService.TrayLeftClickActionFor() == "search"
            ? "Analista Hub — clique esquerdo: abrir busca rápida; botão direito: menu"
            : "Analista Hub — clique esquerdo: abrir janela principal; botão direito: menu";

    /// <summary>
    /// Clique esquerdo (e duplo) na bandeja. Idempotente de propósito: "busca rápida" abre/foca a
    /// HUD, nunca alterna. O duplo clique dispara este método duas vezes e o resultado é o mesmo.
    /// </summary>
    private static void OnTrayLeftClick()
    {
        try
        {
            var acao = SettingsService.TrayLeftClickActionFor();
            AppPaths.Log($"tray: clique esquerdo — ação={acao}");
            if (acao == "search") Views.HudController.Show();
            else ShowMainWindow();
        }
        catch (Exception ex) { AppPaths.Log("tray clique esquerdo: " + ex.Message); }
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

    /// <summary>
    /// Atalho de uma ferramenta acionado (SQ-003). Roda sem abrir a busca quando dá:
    ///   • consulta por base  → abre o painel da consulta;
    ///   • ferramenta sem campo → roda e mostra o aviso;
    ///   • ferramenta com um campo que aceita a área de transferência → usa o valor copiado e roda;
    ///   • caso contrário → abre a busca já nos campos da ferramenta (para digitar).
    /// O diagnóstico `--tool-hotkey <id>` chama esse mesmo caminho por <see cref="SimulateToolHotkey"/>.
    /// </summary>
    public static void SimulateToolHotkey(string toolId) => OnToolHotkey(toolId);

    private static async void OnToolHotkey(string toolId)
    {
        try
        {
            var tool = Catalog?.Find(toolId);
            if (tool is null || tool.IsBroken) { AppPaths.Log($"atalho: ferramenta {toolId} indisponível"); return; }

            if (tool.IsDataset) { Views.SsePanelController.Open(); return; }

            if (tool.Inputs.Count == 0)
            {
                await RunHeadless(tool, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), "atalho/sem-campo");
                return;
            }

            // CFG-003: o comportamento é uma preferência POR FERRAMENTA, com padrão "ask" (abrir
            // pedindo o valor). "consult" usa o que estiver na área de transferência NO MOMENTO do
            // atalho — nunca o valor de quando a preferência foi configurada.
            var comportamento = SettingsService.ShortcutBehaviorFor(toolId);
            var campoClipboard = tool.Inputs.Count == 1 && tool.Inputs[0].EmptyUsesClipboard;

            if (comportamento == "consult" && campoClipboard)
            {
                var copiado = ClipboardService.GetText()?.Trim();
                if (string.IsNullOrWhiteSpace(copiado))
                {
                    AppPaths.Log($"atalho {toolId}: «consultar direto» sem valor copiado — caindo para aguardar input");
                    Views.HudController.Show(toolId);
                    Views.ToastController.ShowFailure(tool, "Nada para consultar",
                        "A área de transferência está vazia. Digite ou cole o valor no campo.");
                    return;
                }

                // NET-004: a MESMA validação da HUD vale para o valor copiado. Sem isso o atalho
                // mandava "999.999.999.999" para o script, que falhava por conta própria — e o
                // analista via duas mensagens diferentes para o mesmo problema.
                var problema = FieldValidation.Check(tool.Inputs[0], copiado);
                if (problema is not null)
                {
                    AppPaths.Log($"atalho {toolId}: valor recusado — {problema}");

                    // cai para "aguardar input": abre a ferramenta com o valor no campo, para
                    // corrigir sem digitar de novo. Uma mensagem só, dizendo o que estava errado.
                    Views.HudController.Show(toolId);
                    Views.HudController.FillFirstField(copiado);
                    Views.ToastController.ShowFailure(tool, TituloFalha, problema);
                    return;
                }

                // CFG-003: dizer o que vai ser usado antes do resultado (mascarado se sensível)
                Views.ToastController.ShowProgress(tool,
                    $"Consultando {FieldValidation.VisibleValue(tool.Inputs[0], copiado)}…", "");

                var entradas = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    [tool.Inputs[0].Key] = copiado
                };
                await RunHeadless(tool, entradas, "atalho/consult");
                return;
            }

            AppPaths.Log($"atalho {toolId}: aguardando input (comportamento={comportamento}) — abrindo os campos");
            Views.HudController.Show(toolId);
        }
        catch (Exception ex) { AppPaths.Log("atalho da ferramenta: " + ex); }
    }

    /// <summary>Executa a ferramenta sem paleta: aviso na tela, cópia e histórico — igual à busca.</summary>
    private static async Task RunHeadless(ToolManifest tool, IReadOnlyDictionary<string, string> entradas,
        string origem)
    {
        var resultado = await ToolRunner.RunAsync(tool, entradas, CancellationToken.None);
        var rotulo = tool.HistoryLabel(entradas);

        HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, rotulo,
            resultado.Ok, resultado.Elapsed.TotalSeconds, resultado.Ok ? resultado.Value : resultado.Detail,
            origem));

        if (resultado.Ok)
        {
            // MKT-001: "sem SN" não copia nada; o clipboard anterior fica intacto.
            if (resultado.IsZeroComNota(tool))
            {
                Views.ToastController.ShowSuccess(tool, tool.Output.ZeroValue!.Title,
                    resultado.ZeroSubtitle(tool, entradas), "notify");
                return;
            }

            ClipboardService.SetText(resultado.Value);

            // NET-004: o cartão já diz "copiado para a área de transferência" no contexto — repetir
            // isso na legenda punha a mesma frase duas vezes. "0" usa a explicação do resultado.
            Views.ToastController.ShowSuccess(tool, resultado.Value, resultado.SuccessSubtitle(tool, ""), "clipboard");
        }
        else
        {
            // NET-004: UMA mensagem, com o nome da ferramenta no topo e o motivo como legenda.
            // Antes eram duas: o popup do script e um aviso "Não foi possível" com cara de sucesso.
            Views.ToastController.ShowFailure(tool, TituloFalha, resultado.Detail);
        }
    }

    /// <summary>Título único das falhas de consulta (NET-004): o motivo vai na legenda.</summary>
    public const string TituloFalha = "Erro de consulta";

    /// <summary>Combinações que o Windows recusou na última carga (ferramenta → motivo).</summary>
    public static Dictionary<string, string> HotkeyProblems { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Registra os atalhos por ferramenta gravados em settings.json. Cada um é independente: uma
    /// combinação recusada entra em <see cref="HotkeyProblems"/> e não derruba as outras.
    /// </summary>
    public static void ApplyToolHotkeys()
    {
        HotkeyProblems.Clear();
        if (Hotkey is null) return;

        foreach (var par in Settings.ToolHotkeys)
        {
            if (Catalog?.Find(par.Key) is null) continue;          // ferramenta removida
            if (string.IsNullOrWhiteSpace(par.Value)) continue;

            if (!Hotkey.RegisterTool(par.Key, par.Value, out var erro))
                HotkeyProblems[par.Key] = erro;
        }

        if (HotkeyProblems.Count > 0)
            AppPaths.Log("atalhos por ferramenta com problema: " +
                         string.Join(" | ", HotkeyProblems.Select(p => $"{p.Key}: {p.Value}")));
    }

    /// <summary>
    /// Grava e registra o atalho de uma ferramenta. Recusa com motivo quando a combinação é da busca
    /// rápida, de outra ferramenta ou quando o Windows não libera — e não toca nos demais atalhos.
    /// </summary>
    public static bool ApplyToolHotkey(string toolId, string combo, out string error)
    {
        error = "";
        if (Hotkey is null) { error = "Atalho global não inicializado."; return false; }

        combo = (combo ?? "").Trim();

        if (combo.Length == 0)
        {
            Hotkey.UnregisterTool(toolId);
            Settings.ToolHotkeys.Remove(toolId);
            SettingsService.Save();
            HotkeyProblems.Remove(toolId);
            AppPaths.Log($"atalho da ferramenta {toolId}: removido");
            return true;
        }

        if (!Hotkey.RegisterTool(toolId, combo, out error))
        {
            HotkeyProblems[toolId] = error;
            return false;
        }

        Settings.ToolHotkeys[toolId] = combo;
        SettingsService.Save();
        HotkeyProblems.Remove(toolId);

        var relido = SettingsService.ReadFromDisk();
        if (relido is null || !relido.ToolHotkeys.TryGetValue(toolId, out var gravado) ||
            !string.Equals(gravado, combo, StringComparison.Ordinal))
        {
            error = $"O atalho {combo} está ativo agora, mas não consegui gravar em {AppPaths.SettingsFile}.";
            return false;
        }
        return true;
    }

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
