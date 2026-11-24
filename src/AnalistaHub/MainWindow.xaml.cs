using AnalistaHub.Models;
using AnalistaHub.Services;
using AnalistaHub.Views;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.System;
using Windows.UI.Core;

namespace AnalistaHub;

public sealed class NavItem
{
    public string Title { get; init; } = "";
    public string Glyph { get; init; } = "\uE8A7";
    /// <summary>Ícone já resolvido (glifo do sistema ou arquivo .ico/.png da ferramenta).</summary>
    public FrameworkElement? IconVisual { get; init; }
    public string? ToolId { get; init; }
    public string Kind { get; init; } = "tool";
}

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, (bool ok, DateTime when)> ProbeCache = new();
    private Action<bool>? _armHotkeyCapture;
    private Action? _validateCurrentTool;
    private Action? _runUpdateCheck;
    private bool _nudging;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Analista Hub";
        AppWindow.Resize(new SizeInt32(1120, 760));
        CenterOnScreen();
        UiKit.ApplyDarkChrome(this, round: false);

        // Fechar no X destrói a janela nesta versão do WinAppSDK (AppWindow.Closing não é acionado
        // nem cancelável). O app continua vivo pela janela oculta da bandeja, e qualquer ação de
        // "abrir" recria a janela na hora — ver App.EnsureMainWindow.
        Closed += (_, _) => AppPaths.Log("janela principal fechada — app segue na bandeja, abrir recria");

        // O empurrão de re-render NÃO roda aqui: mexer no tamanho de uma janela que ainda não foi
        // mostrada deixa a ilha XAML sem conteúdo (janela branca). Só depois de Show + Activate.
        Activated += OnFirstActivated;

        RootGrid.RequestedTheme = App.Settings.Theme switch
        {
            "Claro" => ElementTheme.Light,
            "Sistema" => ElementTheme.Default,
            _ => ElementTheme.Dark
        };

        var icon = AppPaths.IconFile;
        if (File.Exists(icon))
        {
            try { AppWindow.SetIcon(icon); } catch { }
        }

        RebuildNav();
        UpdateHotkeyHint();
    }

    /// <summary>
    /// Primeira ativação: aqui o HWND já existe. Desliga o fechamento pelo usuário e força o repaint
    /// inicial (sem isso a primeira janela também pode sair parcialmente em branco).
    /// </summary>
    private void OnFirstActivated(object sender, Microsoft.UI.Xaml.WindowActivatedEventArgs args)
    {
        Activated -= OnFirstActivated;
        DisableUserClose();
        ScheduleRenderKick();
    }

    /// <summary>
    /// Tira o menu de sistema da janela (WS_SYSMENU). Efeito: some o botão X do título e o Alt+F4
    /// deixa de fechar — e fechar a janela é justamente o que faz o WinUI derrubar a composição e
    /// as janelas seguintes abrirem em branco. Sair é pelo menu do ícone → "Fechar aplicativo".
    /// </summary>
    private void DisableUserClose()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            if ((style & Native.WS_SYSMENU) == 0)
            {
                AppPaths.Log("janela principal: já sem menu de sistema");
                return;
            }

            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, (IntPtr)(style & ~Native.WS_SYSMENU));
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED);
            AppPaths.Log("janela principal: menu de sistema removido (sem X, sem Alt+F4) — sair pelo menu do ícone");
        }
        catch (Exception ex) { AppPaths.Log("disable close: " + ex.Message); }
    }
    /// <summary>
    /// Força o conteúdo a repintar: empurrão de 1 px (gera WM_SIZE) + UpdateLayout. Cura a janela
    /// que abre parcialmente em branco e só carrega quando o usuário mexe nela.
    /// </summary>
    public void NudgeRender()
    {
        if (_nudging) return;
        _nudging = true;
        try
        {
            var size = AppWindow.Size;
            var pos = AppWindow.Position;
            if (size.Width <= 2 || size.Height <= 2) return;

            Content?.UpdateLayout();

            AppWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, size.Width - 1, size.Height));
            AppWindow.MoveAndResize(new RectInt32(pos.X, pos.Y, size.Width, size.Height));

            Content?.UpdateLayout();
            AppPaths.Log($"re-render: janela {size.Width}x{size.Height} em ({pos.X},{pos.Y}) escala {UiKit.Scale(this) * 100:0}%");
        }
        catch (Exception ex) { AppPaths.Log("re-render: " + ex.Message); }
        finally { _nudging = false; }
    }

    /// <summary>Agenda o empurrão para depois que a janela aparecer e o DPI já tiver assentado.</summary>
    public void ScheduleRenderKick()
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(120);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => NudgeRender();
        timer.Start();
    }

    private void CenterOnScreen()
    {
        var (left, top, right, bottom) = UiKit.WorkAreaAtCursor();

        // a janela não pode ser maior que a área útil: em tela 1366x768 (ou com escala 125 %)
        // o tamanho padrão estourava e o conteúdo da direita ficava fora do monitor
        var maxWidth = (int)((right - left) * 0.94);
        var maxHeight = (int)((bottom - top) * 0.94);
        var size = AppWindow.Size;

        var width = Math.Min(size.Width, Math.Max(900, maxWidth));
        var height = Math.Min(size.Height, Math.Max(600, maxHeight));

        AppWindow.MoveAndResize(new RectInt32(
            left + Math.Max(0, (right - left - width) / 2),
            top + Math.Max(0, (bottom - top - height) / 2),
            width, height));
    }

    public void SetHotkeyWarning(string message)
    {
        HotkeyWarning.Text = message;
        HotkeyWarning.Visibility = Visibility.Visible;
    }

    public void UpdateHotkeyHint()
    {
        HotkeyHint.Text = $"Atalho global: {App.Settings.Hotkey}";
    }

    public void RebuildNav()
    {
        var selected = (NavList.SelectedItem as NavItem)?.Title;
        var items = new List<NavItem>
        {
            new() { Title = "Início", Glyph = "\uE80F", IconVisual = ToolIcons.Glyph("\uE80F", 15), Kind = "home" }
        };
        items.AddRange(App.Catalog.Tools.Select(t => new NavItem
        {
            Title = t.IsBroken ? $"{t.Name} (com problema)" : t.Name,
            Glyph = t.IconGlyph,
            IconVisual = ToolIcons.Create(t, 20, 15),
            ToolId = t.Id,
            Kind = "tool"
        }));
        items.Add(new NavItem { Title = "Configurações", Glyph = "\uE713", IconVisual = ToolIcons.Glyph("\uE713", 15), Kind = "settings" });

        NavList.ItemsSource = items;
        NavList.SelectedItem = items.FirstOrDefault(i => i.Title == selected) ?? items[0];
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (NavList.SelectedItem is not NavItem item) return;
        ContentScroll.ChangeView(null, 0, null);

        if (item.Kind == "home") ShowHome();
        else if (item.Kind == "settings") ShowSettings();
        else if (item.ToolId is not null)
        {
            var tool = App.Catalog.Find(item.ToolId);
            if (tool is null) ShowHome();
            else ShowTool(tool);
        }
    }

    /// <summary>Navega para uma página por identificador (usado por --page e pelo painel).</summary>
    public void NavigateTo(string page)
    {
        if (NavList.ItemsSource is not IEnumerable<NavItem> items) return;

        var target = page.Equals("home", StringComparison.OrdinalIgnoreCase) ? items.FirstOrDefault(i => i.Kind == "home")
            : page.Equals("settings", StringComparison.OrdinalIgnoreCase) ? items.FirstOrDefault(i => i.Kind == "settings")
            : items.FirstOrDefault(i => string.Equals(i.ToolId, page, StringComparison.OrdinalIgnoreCase));

        if (target is null) return;
        NavList.SelectedItem = target;

        // lembra onde o usuário estava: se ele fechar a janela no X, abrir de novo volta aqui
        var marker = target.Kind switch
        {
            "home" => "home",
            "settings" => "settings",
            _ => target.ToolId ?? "home"
        };
        if (!string.Equals(App.Settings.LastPage, marker, StringComparison.OrdinalIgnoreCase))
        {
            App.Settings.LastPage = marker;
            SettingsService.Save();
        }
    }

    /// <summary>
    /// Menu da bandeja → "Procurar atualização agora": abre Configurações e dispara a verificação
    /// no mesmo gesto (o usuário vê o resultado, não só um clique sem retorno).
    /// </summary>
    public void CheckUpdatesNow()
    {
        NavigateTo("settings");
        DispatcherQueue.TryEnqueue(() =>
        {
            try { _runUpdateCheck?.Invoke(); }
            catch (Exception ex) { AppPaths.Log("CheckUpdatesNow: " + ex.Message); }
        });
    }

    /// <summary>
    /// Abre Configurações e arma a captura do atalho (mesmo caminho do botão Definir atalho).
    /// Usado por --arm-hotkey, que serve para testar a captura sem mouse.
    /// </summary>
    public void ArmHotkeyCapture()
    {
        NavigateTo("settings");
        UiKit.ForceForeground(this);

        // a janela pode não estar ativa no instante do arranque: rearma depois de assentar
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(700);
        timer.IsRepeating = false;
        timer.Tick += (_, _) =>
        {
            UiKit.ForceForeground(this);
            _armHotkeyCapture?.Invoke(true);
        };
        timer.Start();
    }

    /// <summary>
    /// Roda a validação de credencial da ferramenta aberta — o MESMO caminho do 320 ms de digitação.
    /// Usado por --validate-once para testar a tela sem depender de teclado.
    /// </summary>
    public void ValidateCredentialsNow() =>
        DispatcherQueue.TryEnqueue(() => _validateCurrentTool?.Invoke());

    private void OnOpenHudClick(object sender, RoutedEventArgs e) => HudController.Show();

    private void OnExitClick(object sender, RoutedEventArgs e) => App.ExitApp();

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        try { ((Microsoft.UI.Windowing.OverlappedPresenter)AppWindow.Presenter).Minimize(); }
        catch (Exception ex) { AppPaths.Log("minimizar: " + ex.Message); }
    }

    // ================================================================== widgets
    private Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = (Brush)Application.Current.Resources["HubCardBackground"],
        BorderBrush = (Brush)Application.Current.Resources["HubStroke"],
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = padding ?? new Thickness(18, 16, 18, 16),
        Child = child
    };

    private void AddPageHeader(string title, string? subtitle = null)
    {
        ContentPanel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.Resources["HubTitle"] });
        if (!string.IsNullOrWhiteSpace(subtitle))
            ContentPanel.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.Resources["HubSubtitle"] });
    }

    private void AddBlock(string heading, UIElement body)
    {
        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(new TextBlock
        {
            Text = heading,
            FontSize = 13,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["HubAccentBrush"]
        });
        panel.Children.Add(body);
        ContentPanel.Children.Add(Card(panel));
    }

    private static TextBlock Para(string text, double size = 13) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = size
    };

    private static TextBlock Muted(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 255, 255, 255))
    };

    private static StackPanel KeyValue(string key, string value)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        panel.Children.Add(new TextBlock { Text = key, Width = 150, FontSize = 12, Opacity = 0.7 });
        panel.Children.Add(new TextBlock { Text = value, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = 560 });
        return panel;
    }

    // ================================================================== início
    private void ShowHome()
    {
        ContentPanel.Children.Clear();
        AddPageHeader("Início", "Cada ferramenta agrupa um script e sabe se apresentar: para que serve, como executar, o que exige e como entrega o resultado.");

        var hotkeyRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        hotkeyRow.Children.Add(new Button { Content = $"Abrir busca ({App.Settings.Hotkey})", Style = (Style)Application.Current.Resources["AccentButtonStyle"] });
        ((Button)hotkeyRow.Children[0]).Click += (_, _) => HudController.Show();
        hotkeyRow.Children.Add(Muted("Funciona com qualquer janela em foco, inclusive em tela cheia."));
        AddBlock("Comando rápido", hotkeyRow);

        var tools = App.Catalog.Tools;
        if (tools.Count == 0)
        {
            AddBlock("Nenhuma ferramenta instalada", Para(
                $"Coloque manifestos .json em:\n{AppPaths.ToolsDir}\n\nCada .json descreve uma ferramenta e o script que ela executa."));
            return;
        }

        foreach (var tool in tools)
        {
            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 4 };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(ToolIcons.Create(tool, 18, 14));
            titleRow.Children.Add(new TextBlock { Text = tool.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (!string.IsNullOrWhiteSpace(tool.Trigger))
                titleRow.Children.Add(new TextBlock { Text = SettingsService.TriggerFor(tool), FontFamily = new FontFamily("Consolas"), FontSize = 12, Opacity = 0.6, VerticalAlignment = VerticalAlignment.Center });
            left.Children.Add(titleRow);
            left.Children.Add(Muted(tool.IsBroken ? "Manifesto com problema: " + tool.LoadError : tool.Description));
            row.Children.Add(left);

            var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            var status = new TextBlock { Text = "○", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(status);
            if (!tool.IsBroken)
            {
                var open = new Button { Content = "Abrir", FontSize = 12 };
                open.Click += (_, _) => HudController.ShowTool(tool.Id);
                actions.Children.Add(open);

                var run = new Button { Content = "Testar", FontSize = 12 };
                run.Click += async (_, _) => await RunFromDashboardAsync(tool);
                actions.Children.Add(run);
            }
            Grid.SetColumn(actions, 1);
            row.Children.Add(actions);

            ContentPanel.Children.Add(Card(row));

            if (!tool.IsBroken && (tool.Network is not null || tool.Credentials is not null))
                _ = UpdateStatusAsync(tool, status);
        }
    }

    /// <summary>
    /// Bolinha única de status da ferramenta. Ela reflete as DUAS coisas ao mesmo tempo:
    /// comunicação com o serviço e aceitação das credenciais.
    ///   cinza  = não verificado
    ///   verde  = serviço responde e credenciais aceitas (ou não exige)
    ///   amarelo= serviço responde, mas autenticação falhou (ou credenciais faltando)
    ///   vermelho = sem conexão com o serviço
    /// </summary>
    private async Task UpdateStatusAsync(ToolManifest tool, TextBlock target)
    {
        bool? reachable = null;
        if (tool.Network is not null)
            reachable = await Probes.CheckTcpAsync(tool.Network.Host, tool.Network.Port, 2500);

        ProbeCache[tool.Id] = (reachable ?? false, DateTime.Now);

        var auth = CredentialValidator.LastKnown(tool.Id);
        var creds = Probes.CheckCredentials(tool);
        var requiresCredentials = tool.Credentials is not null && tool.Credentials.Fields.Count > 0;

        Windows.UI.Color color;
        string tooltip;

        if (reachable == false)
        {
            color = Windows.UI.Color.FromArgb(255, 232, 122, 122);          // vermelho
            tooltip = $"Sem resposta de {tool.Network!.Host}:{tool.Network.Port} — verifique a VPN";
        }
        else if (auth.State == AuthState.Refused)
        {
            color = Windows.UI.Color.FromArgb(255, 232, 184, 75);           // amarelo
            tooltip = "Autenticação recusada pelo serviço: " + auth.Message +
                      " — corrija as credenciais na aba da ferramenta";
        }
        else if (requiresCredentials && !creds.Complete)
        {
            color = Windows.UI.Color.FromArgb(255, 232, 184, 75);           // amarelo
            tooltip = "Credenciais incompletas: faltando " + string.Join(", ", creds.Missing);
        }
        else if (reachable == true && auth.State != AuthState.Unreachable)
        {
            color = Windows.UI.Color.FromArgb(255, 108, 214, 138);          // verde
            tooltip = auth.State == AuthState.Ok
                ? $"{tool.Network?.Label ?? "serviço"} respondeu · credenciais aceitas ({auth.When:HH:mm})"
                : $"{tool.Network?.Label ?? "serviço"} respondeu · credenciais não verificadas ainda";
        }
        else
        {
            color = Windows.UI.Color.FromArgb(255, 138, 138, 138);          // cinza
            tooltip = "Status não verificado";
        }

        target.Text = "●";
        target.Foreground = new SolidColorBrush(color);
        ToolTipService.SetToolTip(target, tooltip);
    }

    private async Task RunFromDashboardAsync(ToolManifest tool)
    {
        if (tool.IsDataset)
        {
            SsePanelController.Open();
            return;
        }

        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var clip = ClipboardService.GetText();
        foreach (var field in tool.Inputs)
        {
            if (field.EmptyUsesClipboard) continue; // script lê o clipboard
            if (!string.IsNullOrWhiteSpace(clip)) inputs[field.Key] = clip;
        }

        var result = await ToolRunner.RunAsync(tool, inputs, CancellationToken.None);
        if (result.Ok)
        {
            ClipboardService.SetText(result.Value);
            ToastController.ShowSuccess(tool, result.Value, tool.Output.ValueLabel, "clipboard");
        }
        else
        {
            var detail = result.Detail;
            if (tool.Network is not null && !result.TimedOut &&
                !await Probes.CheckTcpAsync(tool.Network.Host, tool.Network.Port, 2500))
                detail = "Sem conexão com o servidor. Verifique a VPN e tente novamente.";
            ToastController.ShowMessage(tool.Name, "Falhou", detail);
        }

        HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, "teste pelo painel",
            result.Ok, result.Elapsed.TotalSeconds, result.Ok ? result.Value : result.Detail));
    }

    // ================================================================== ferramenta
    private void ShowTool(ToolManifest tool)
    {
        ContentPanel.Children.Clear();
        AddPageHeader(tool.Name, tool.IsBroken ? null : tool.Description);

        // indicador único de "testando" para toda validação desta ferramenta
        var busyRing = new ProgressRing
        {
            Width = 16,
            Height = 16,
            IsActive = false,
            Visibility = Visibility.Collapsed,
            Foreground = (Brush)Application.Current.Resources["HubAccentBrush"]
        };
        var busyText = Muted("");
        var busyRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Margin = new Thickness(0, -6, 0, 0)
        };
        busyRow.Children.Add(busyRing);
        busyRow.Children.Add(busyText);
        ContentPanel.Children.Add(busyRow);

        void SetToolBusy(bool on, string message = "testando…")
        {
            busyRing.IsActive = on;
            busyRing.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            busyText.Text = on ? message : "";
        }

        if (tool.IsBroken)
        {
            AddBlock("Manifesto com problema", Para(
                $"Não foi possível usar esta ferramenta.\n\nMotivo: {tool.LoadError}\n\nArquivo: {tool.SourcePath}"));
            return;
        }

        // 1 — para que serve
        AddBlock("Para que serve", Para(string.IsNullOrWhiteSpace(tool.LongDescription) ? tool.Description : tool.LongDescription));

        // 2 — como executar
        var how = new StackPanel { Spacing = 8 };
        how.Children.Add(KeyValue("Gatilho", string.IsNullOrWhiteSpace(tool.Trigger) ? "sem gatilho" : tool.Trigger));
        how.Children.Add(KeyValue("Busca / atalho", $"{App.Settings.Hotkey} → digite o nome ou o gatilho → Enter"));
        how.Children.Add(KeyValue("Exemplo", tool.Examples.Count > 0 ? string.Join("   ·   ", tool.Examples) : "—"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };
        if (tool.IsDataset)
        {
            var openData = new Button
            {
                Content = "Abrir consulta",
                Style = (Style)Application.Current.Resources["AccentButtonStyle"]
            };
            openData.Click += (_, _) => SsePanelController.Open();
            actions.Children.Add(openData);

            var openHudData = new Button { Content = "Abrir no comando" };
            openHudData.Click += (_, _) => HudController.ShowTool(tool.Id);
            actions.Children.Add(openHudData);
        }
        else
        {
            var openHud = new Button { Content = "Abrir no comando", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
            openHud.Click += (_, _) => HudController.ShowTool(tool.Id);
            actions.Children.Add(openHud);
            var testNow = new Button { Content = "Testar agora (usa o clipboard)" };
            testNow.Click += async (_, _) => await RunFromDashboardAsync(tool);
            actions.Children.Add(testNow);
        }
        how.Children.Add(actions);
        AddBlock("Como executar", how);

        // base local: transparência de onde vêm os dados e como atualizar
        if (tool.IsDataset)
        {
            var catalog = SseCatalog.Current;
            var data = new StackPanel { Spacing = 8 };
            data.Children.Add(KeyValue("Arquivo", catalog.DataPath));
            data.Children.Add(KeyValue("Conteúdo", catalog.Loaded
                ? $"{catalog.Cities.Count} cidades · {catalog.TotalWithAccess} com NM/OLT/VPN · " +
                  $"{catalog.TotalLines} linhas · base de {catalog.Generated}"
                : catalog.Error ?? "base não carregada"));
            if (catalog.Sources.Count > 0)
                data.Children.Add(KeyValue("Origem", string.Join("   ·   ", catalog.Sources.Select(s => s.File))));
            data.Children.Add(Muted("Nada é consultado na rede: os dados ficam neste arquivo (planilhas do SSE + " +
                                    "cadastro de municípios da ANATEL). Para atualizar, rode build_merge.py e copie o " +
                                    "JSON para a pasta de dados do Hub."));
            AddBlock("Base de dados", data);

            // mesmas opções do menu ⚙ do painel de consulta: uma preferência, dois lugares
            var extras = new StackPanel { Spacing = 10 };
            extras.Children.Add(Muted(
                "Estas opções valem para a Consulta SSE — painel flutuante e comando — e ficam gravadas. " +
                "Se o painel estiver aberto, ele se atualiza na hora."));
            extras.Children.Add(SseExtras.Build(SsePanelController.AplicarExtras, comTitulo: false));
            AddBlock("Dados extras da consulta", extras);
        }

        // 3 — configurações e credenciais
        var requirements = new StackPanel { Spacing = 12 };

        // 3a — gatilho da ferramenta: mesmo padrão de interação do atalho global
        // (armar → capturar o que o usuário digita → validar pela regra única → gravar)
        var triggerCaption = new TextBlock { Text = "Gatilho desta ferramenta", FontSize = 12, Opacity = 0.75 };
        var triggerStatus = Muted("");
        var triggerFrame = new Border
        {
            BorderThickness = new Thickness(1.5),
            CornerRadius = new CornerRadius(6),
            BorderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255)),
            Width = 220,
            Child = new TextBox
            {
                Text = SettingsService.TriggerFor(tool),
                PlaceholderText = ":mkt",
                FontFamily = new FontFamily("Consolas"),
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
            }
        };
        var triggerBox = (TextBox)triggerFrame.Child;
        var armTrigger = new Button { Content = "Definir gatilho" };
        var triggerArmed = false;

        void PaintTrigger(bool? ok)
        {
            triggerFrame.BorderBrush = ok switch
            {
                true => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 108, 214, 138)),   // verde
                false => new SolidColorBrush(Windows.UI.Color.FromArgb(255, 232, 184, 75)),  // amarelo
                null => new SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255))
            };
        }

        void CommitTrigger()
        {
            var typed = triggerBox.Text;
            var problem = TriggerPolicy.Validate(typed, App.Catalog, tool.Id);
            if (problem is not null)
            {
                PaintTrigger(false);
                triggerStatus.Text = "recusado: " + problem;
                return;
            }

            var normalized = TriggerPolicy.Normalize(typed);
            SettingsService.SetTrigger(tool, normalized);
            triggerBox.Text = SettingsService.TriggerFor(tool);
            PaintTrigger(true);
            triggerStatus.Text = "— ok · gatilho ativo " + SettingsService.TriggerFor(tool);
            AppPaths.Log($"gatilho de {tool.Id} definido: {SettingsService.TriggerFor(tool)}");
        }

        void SetTriggerArmed(bool on)
        {
            triggerArmed = on;
            armTrigger.Content = on ? "digite o gatilho…" : "Definir gatilho";
            if (!on) return;
            PaintTrigger(null);
            triggerStatus.Text = "captura armada — digite o gatilho e pressione Enter";
            triggerBox.Focus(FocusState.Programmatic);
            triggerBox.SelectAll();
        }

        armTrigger.Click += (_, _) => SetTriggerArmed(true);

        triggerBox.KeyDown += (_, e) =>
        {
            if (!triggerArmed) return;
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                triggerBox.Text = SettingsService.TriggerFor(tool);
                SetTriggerArmed(false);
                triggerStatus.Text = "captura cancelada";
                PaintTrigger(null);
                return;
            }
            if (e.Key == VirtualKey.Enter)
            {
                e.Handled = true;
                SetTriggerArmed(false);
                CommitTrigger();
            }
        };

        triggerBox.LostFocus += (_, _) =>
        {
            if (!triggerArmed) return;
            SetTriggerArmed(false);
            CommitTrigger();
        };

        var triggerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Bottom };
        triggerRow.Children.Add(triggerFrame);
        triggerRow.Children.Add(armTrigger);
        requirements.Children.Add(triggerCaption);
        requirements.Children.Add(triggerRow);
        requirements.Children.Add(triggerStatus);
        requirements.Children.Add(Muted("Mesma regra para toda ferramenta: começa com ':', minúsculas, até 32 caracteres e único no catálogo. Aparece na busca do HUD e nos cartões."));

        // 3b — campos de entrada (entrada da consulta)
        if (tool.Inputs.Count > 0)
        {
            foreach (var field in tool.Inputs)
            {
                var line = string.IsNullOrWhiteSpace(field.Hint) ? "" : $"  ({field.Hint})";
                var required = field.Required ? "obrigatório" : "opcional";
                var clip = field.EmptyUsesClipboard ? " · vazio = clipboard" : "";
                requirements.Children.Add(KeyValue($"Entrada: {field.Label}", $"{field.Type} · {required}{clip}{line}"));
            }
        }

        // 3c — credenciais: digitáveis, gravadas sozinhas e validadas no serviço
        if (tool.Credentials is not null)
        {
            var credLabel = string.IsNullOrWhiteSpace(tool.Credentials.Label) ? "Credenciais" : tool.Credentials.Label;
            requirements.Children.Add(new TextBlock
            {
                Text = credLabel,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 4, 0, 0)
            });

            var canValidate = CredentialValidator.CanValidate(tool);
            var entries = new List<(CredentialField field, Func<string> read, Action clear, Border frame, TextBlock status)>();
            var credPanel = new StackPanel { Spacing = 10 };
            var credStatus = Muted("");
            var suppress = true;     // não valida enquanto a tela está sendo montada
            var validating = false;
            Microsoft.UI.Dispatching.DispatcherQueueTimer? debounce = null;

            var neutral = Windows.UI.Color.FromArgb(60, 255, 255, 255);
            var green = Windows.UI.Color.FromArgb(255, 108, 214, 138);
            var amber = Windows.UI.Color.FromArgb(255, 232, 184, 75);

            void Paint(AuthState state, string message = "")
            {
                foreach (var entry in entries)
                {
                    var color = state switch
                    {
                        AuthState.Ok => green,
                        AuthState.Refused => amber,
                        _ => neutral           // sem resposta / não verificado: não julga a credencial
                    };
                    entry.frame.BorderBrush = new SolidColorBrush(color);
                    entry.status.Text = state switch
                    {
                        AuthState.Ok => "— ok",
                        AuthState.Refused => "— recusado",
                        AuthState.Unreachable => "— sem resposta",
                        _ => "—"
                    };
                    entry.status.Foreground = new SolidColorBrush(state switch
                    {
                        AuthState.Ok => green,
                        AuthState.Refused => amber,
                        _ => Windows.UI.Color.FromArgb(170, 255, 255, 255)
                    });
                }

                if (!string.IsNullOrWhiteSpace(message))
                    credStatus.Text = "serviço: " + message;
            }

            void SaveCredentials(bool announce)
            {
                foreach (var (field, read, _, _, _) in entries)
                    CredentialStore.Set(tool.Id, field.Key, read());

                var state = Probes.CheckCredentials(tool);
                credStatus.Text = state.Complete
                    ? "credenciais salvas e cifradas neste computador"
                    : "faltando: " + string.Join(", ", state.Missing);
                if (announce) credStatus.Text += " · salvo agora";
            }

            async void RunValidationAsync()
            {
                if (!canValidate || validating) return;
                SaveCredentials(false);

                if (!Probes.CheckCredentials(tool).Complete)
                {
                    Paint(AuthState.Unknown);
                    credStatus.Text = "preencha usuário e senha para validar";
                    return;
                }

                validating = true;
                SetToolBusy(true, "validando credenciais…");
                Paint(AuthState.Unknown);
                credStatus.Text = "consultando o serviço…";

                var result = await CredentialValidator.ValidateAsync(tool, CancellationToken.None);

                validating = false;
                SetToolBusy(false);
                Paint(result.State, result.Message);
            }

            void ScheduleValidation()
            {
                if (suppress) return;              // regra 3.1: só por interação do usuário
                debounce?.Stop();
                debounce = DispatcherQueue.CreateTimer();
                debounce.Interval = TimeSpan.FromMilliseconds(320);   // regra 3.2
                debounce.IsRepeating = false;
                debounce.Tick += (_, _) => RunValidationAsync();
                debounce.Start();
            }

            foreach (var field in tool.Credentials.Fields)
            {
                var currentValue = CredentialStore.Get(tool.Id, field.Key);

                var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
                header.Children.Add(new TextBlock { Text = field.Label, FontSize = 12, Opacity = 0.75 });
                var fieldStatus = new TextBlock
                {
                    Text = "—",
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(170, 255, 255, 255))
                };
                header.Children.Add(fieldStatus);

                var frame = new Border
                {
                    BorderThickness = new Thickness(1.5),
                    CornerRadius = new CornerRadius(6),
                    BorderBrush = new SolidColorBrush(neutral),
                    Width = 340
                };

                if (field.Secret)
                {
                    var box = new PasswordBox
                    {
                        PlaceholderText = field.Placeholder,
                        Password = currentValue,
                        PasswordRevealMode = PasswordRevealMode.Peek,
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                    };
                    box.PasswordChanged += (_, _) => ScheduleValidation();
                    box.LostFocus += (_, _) => SaveCredentials(false);
                    frame.Child = box;
                    entries.Add((field, () => box.Password, () => box.Password = "", frame, fieldStatus));
                }
                else
                {
                    var box = new TextBox
                    {
                        PlaceholderText = field.Placeholder,
                        Text = currentValue,
                        BorderThickness = new Thickness(0),
                        Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0))
                    };
                    box.TextChanged += (_, _) => ScheduleValidation();
                    box.LostFocus += (_, _) => SaveCredentials(false);
                    frame.Child = box;
                    entries.Add((field, () => box.Text, () => box.Text = "", frame, fieldStatus));
                }

                var item = new StackPanel { Spacing = 4 };
                item.Children.Add(header);
                item.Children.Add(frame);
                credPanel.Children.Add(item);
            }

            var credActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var saveCred = new Button { Content = "Salvar credenciais" };
            saveCred.Click += (_, _) =>
            {
                SaveCredentials(true);
                RunValidationAsync();
            };
            var clearCred = new Button { Content = "Apagar" };
            clearCred.Click += (_, _) =>
            {
                CredentialStore.Clear(tool.Id);
                foreach (var (_, _, clear, _, _) in entries) clear();
                Paint(AuthState.Unknown);
                credStatus.Text = "credenciais apagadas deste computador";
            };
            credActions.Children.Add(saveCred);
            credActions.Children.Add(clearCred);
            credPanel.Children.Add(credActions);
            credPanel.Children.Add(credStatus);

            credPanel.Children.Add(Muted(canValidate
                ? "Guardadas com a criptografia do seu usuário do Windows (DPAPI). A senha é conferida no serviço 320 ms depois que você para de digitar."
                : "Esta ferramenta ainda não declarou como validar credenciais — o HUB guarda, mas não confere."));

            var initial = Probes.CheckCredentials(tool);
            credStatus.Text = initial.Complete
                ? "credenciais salvas e cifradas neste computador"
                : "faltando: " + string.Join(", ", initial.Missing);

            var lastKnown = CredentialValidator.LastKnown(tool.Id);
            if (lastKnown.State != AuthState.Unknown) Paint(lastKnown.State, lastKnown.Message);

            _validateCurrentTool = RunValidationAsync;

            requirements.Children.Add(credPanel);

            suppress = false;   // a partir daqui, digitar dispara validação
        }

        if (tool.Network is not null)
        {
            requirements.Children.Add(KeyValue("Serviço", $"{tool.Network.Label} — {tool.Network.Host}:{tool.Network.Port}"));
            var probeRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var probeButton = new Button { Content = "Testar conexão", FontSize = 12 };
            var probeResult = Muted("não verificado");
            probeButton.Click += async (_, _) =>
            {
                probeResult.Text = "testando…";
                SetToolBusy(true, "testando conexão…");
                var ok = await Probes.CheckTcpAsync(tool.Network!.Host, tool.Network.Port, 3000);
                SetToolBusy(false);
                probeResult.Text = ok
                    ? $"respondeu ({DateTime.Now:HH:mm:ss})"
                    : "sem resposta — verifique a VPN";
            };
            probeRow.Children.Add(probeButton);
            probeRow.Children.Add(probeResult);
            requirements.Children.Add(probeRow);
        }

        if (!string.IsNullOrWhiteSpace(tool.Notes)) requirements.Children.Add(Muted(tool.Notes));
        AddBlock("Configurações e credenciais", requirements);

        // 4 — apresentação do resultado
        var output = new StackPanel { Spacing = 10 };
        output.Children.Add(Para("O que é copiado/digitado: " + (string.IsNullOrWhiteSpace(tool.Output.ValueLabel) ? "o valor devolvido pelo script" : tool.Output.ValueLabel)));

        var current = SettingsService.OutputModeFor(tool.Id, tool.Output.Default);
        var radios = new StackPanel { Spacing = 4 };
        foreach (var mode in tool.Output.Allowed.Distinct())
        {
            var (label, description) = mode switch
            {
                "clipboard" => ("Copiar (padrão)", "copia o valor e mostra o aviso"),
                "type" => ("Digitar", "copia e digita no campo que estava em foco"),
                "notify" => ("Só avisar", "mostra o aviso sem copiar"),
                _ => (mode, "")
            };
            var rb = new RadioButton
            {
                Content = $"{label} — {description}",
                GroupName = "out-" + tool.Id,
                IsChecked = string.Equals(current, mode, StringComparison.OrdinalIgnoreCase),
                FontSize = 13
            };
            var capturedMode = mode;
            rb.Checked += (_, _) =>
            {
                SettingsService.Current.OutputMode[tool.Id] = capturedMode;
                SettingsService.Save();
            };
            radios.Children.Add(rb);
        }
        output.Children.Add(radios);
        AddBlock("Como o resultado é apresentado", output);

        // histórico
        var recent = HistoryStore.Recent(tool.Id, 5).ToList();
        var history = new StackPanel { Spacing = 4 };
        if (recent.Count == 0) history.Children.Add(Muted("Nenhuma execução registrada ainda."));
        foreach (var entry in recent)
        {
            var mark = entry.Ok ? "OK" : "ERRO";
            history.Children.Add(Muted($"{entry.When:dd/MM HH:mm} · {mark} · {entry.Seconds:0.0}s · {Truncate(entry.Input, 40)}" +
                                       (entry.Ok ? $" → {Truncate(entry.Detail, 60)}" : $" → {Truncate(entry.Detail, 80)}")));
        }
        AddBlock("Últimas execuções", history);

        AddBlock("Arquivos", new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Muted("Script: " + tool.ExpandedScript),
                Muted("Manifesto: " + tool.SourcePath + (tool.FromUserFolder ? " (sua pasta)" : " (embutido)"))
            }
        });
    }

    private static string Truncate(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s : s[..max] + "…";

    private static void OpenPath(string path)
    {
        try
        {
            if (File.Exists(path) || Directory.Exists(path))
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(path) { UseShellExecute = true });
            else
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Path.GetDirectoryName(path)!) { UseShellExecute = true });
        }
        catch (Exception ex) { AppPaths.Log("OpenPath: " + ex.Message); }
    }

    // ================================================================== configurações
    private void ShowSettings()
    {
        ContentPanel.Children.Clear();
        AddPageHeader("Configurações", "Preferências do aplicativo. Valem para todas as ferramentas.");

        // atalho — a captura só acontece depois de o usuário armar ("Definir atalho").
        // Assim uma tecla perdida com o campo focado não troca o atalho global sem querer.
        var hotkeyPanel = new StackPanel { Spacing = 8 };
        var hotkeyStatus = Muted("");

        var hotkeyBox = new TextBox
        {
            IsReadOnly = true,
            Width = 300,
            Text = App.Settings.Hotkey,
            PlaceholderText = "atalho atual",
            FontFamily = new FontFamily("Consolas")
        };

        var armButton = new Button { Content = "Definir atalho" };
        var armed = false;

        void SetArmed(bool on)
        {
            armed = on;
            armButton.Content = on ? "pressione a combinação… (Esc cancela)" : "Definir atalho";
            if (on)
            {
                hotkeyStatus.Text = "captura armada — pressione a combinação agora";
                hotkeyBox.Focus(FocusState.Programmatic);
            }
        }

        armButton.Click += (_, _) => SetArmed(true);

        static bool IsDown(VirtualKey key) =>
            InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

        static bool IsModifier(VirtualKey key) => key is VirtualKey.Control or VirtualKey.Shift or VirtualKey.Menu
            or VirtualKey.LeftWindows or VirtualKey.RightWindows;

        static string KeyName(VirtualKey key)
        {
            var v = (int)key;
            if (v is >= 0x30 and <= 0x39) return ((char)v).ToString();   // 0-9
            if (v is >= 0x41 and <= 0x5A) return ((char)v).ToString();   // A-Z
            if (v is >= 0x70 and <= 0x87) return "F" + (v - 0x6F);       // F1-F24
            return key switch
            {
                VirtualKey.Space => "Space",
                VirtualKey.Enter => "Enter",
                VirtualKey.Tab => "Tab",
                VirtualKey.Escape => "Escape",
                VirtualKey.Back => "Backspace",
                VirtualKey.Insert => "Insert",
                VirtualKey.Delete => "Delete",
                VirtualKey.Home => "Home",
                VirtualKey.End => "End",
                VirtualKey.PageUp => "PageUp",
                VirtualKey.PageDown => "PageDown",
                VirtualKey.Left => "Left",
                VirtualKey.Up => "Up",
                VirtualKey.Right => "Right",
                VirtualKey.Down => "Down",
                _ => key.ToString()
            };
        }

        static string ModifiersOnly()
        {
            var parts = new List<string>();
            if (IsDown(VirtualKey.Control)) parts.Add("Ctrl");
            if (IsDown(VirtualKey.Menu)) parts.Add("Alt");
            if (IsDown(VirtualKey.Shift)) parts.Add("Shift");
            if (IsDown(VirtualKey.LeftWindows) || IsDown(VirtualKey.RightWindows)) parts.Add("Win");
            return string.Join("+", parts);
        }

        static string ComboFor(VirtualKey finalKey)
        {
            var mods = ModifiersOnly();
            return mods.Length > 0 ? mods + "+" + KeyName(finalKey) : KeyName(finalKey);
        }

        hotkeyBox.GotFocus += (_, _) =>
        {
            if (armed) hotkeyStatus.Text = "captura armada — pressione a combinação agora";
        };

        hotkeyBox.LostFocus += (_, _) =>
        {
            if (armed)
            {
                SetArmed(false);
                hotkeyStatus.Text = "captura cancelada (o campo perdeu o foco)";
            }
        };

        hotkeyBox.KeyDown += (_, e) =>
        {
            if (!armed) return; // tecla perdida não troca o atalho global

            if (IsModifier(e.Key))
            {
                e.Handled = true;
                var mods = ModifiersOnly();
                hotkeyBox.Text = mods.Length > 0 ? mods + " + …" : "";
                return;
            }

            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                hotkeyBox.Text = App.Settings.Hotkey;
                hotkeyStatus.Text = "captura cancelada";
                SetArmed(false);
                return;
            }

            e.Handled = true;
            var combo = ComboFor(e.Key);
            AppPaths.Log($"captura: key={e.Key} ({(int)e.Key}) mods=[{ModifiersOnly()}] combo={combo}");
            var hasModifier = combo.Contains("Ctrl+") || combo.Contains("Alt+") || combo.Contains("Shift+") || combo.Contains("Win+");
            if (!hasModifier)
            {
                hotkeyStatus.Text = "use Ctrl, Alt, Shift ou Win junto com a tecla";
                hotkeyBox.Text = App.Settings.Hotkey;
                return;
            }

            if (App.ApplyHotkey(combo, out var error))
            {
                hotkeyBox.Text = combo;
                hotkeyStatus.Text = $"atalho ativo: {combo}";
                AppPaths.Log("atalho global definido pelo usuário: " + combo);
                UpdateHotkeyHint();
            }
            else
            {
                hotkeyBox.Text = App.Settings.Hotkey;
                hotkeyStatus.Text = error + " O atalho anterior continua valendo.";
                AppPaths.Log("atalho recusado pelo Windows: " + combo);
            }
            SetArmed(false);
        };

        _armHotkeyCapture = SetArmed;

        var armRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        armRow.Children.Add(hotkeyBox);
        armRow.Children.Add(armButton);
        hotkeyPanel.Children.Add(armRow);
        hotkeyPanel.Children.Add(hotkeyStatus);
        hotkeyPanel.Children.Add(Muted("Clique em \"Definir atalho\" e pressione a combinação — ela é gravada na hora. Evite Alt+Espaço (PowerToys Run) e Win+Alt+Espaço (Paleta de Comandos)."));
        AddBlock("Atalho global", hotkeyPanel);

        // aviso
        var toastPanel = new StackPanel { Spacing = 8 };
        var toastRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var secondsBox = new NumberBox
        {
            Value = App.Settings.ToastSeconds,
            Minimum = 4,
            Maximum = 120,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 140
        };
        var saveToast = new Button { Content = "Salvar" };
        saveToast.Click += (_, _) =>
        {
            App.Settings.ToastSeconds = (int)Math.Clamp(secondsBox.Value, 4, 120);
            SettingsService.Save();
        };
        toastRow.Children.Add(secondsBox);
        toastRow.Children.Add(saveToast);
        toastPanel.Children.Add(toastRow);
        toastPanel.Children.Add(Muted("Segundos que o aviso de resultado fica na tela antes de fechar sozinho."));
        AddBlock("Aviso de resultado", toastPanel);

        // início com o Windows
        var startupPanel = new StackPanel { Spacing = 8 };
        var startupToggle = new ToggleSwitch
        {
            Header = "Iniciar com o Windows",
            IsOn = StartupShortcut.Exists(),
            OnContent = "ligado",
            OffContent = "desligado"
        };
        var startupStatus = Muted(StartupShortcut.Location);
        startupToggle.Toggled += (_, _) =>
        {
            var error = StartupShortcut.Set(startupToggle.IsOn);
            startupStatus.Text = error ?? (startupToggle.IsOn ? "atalho criado na pasta Inicializar" : "atalho removido");
        };
        startupPanel.Children.Add(startupToggle);
        startupPanel.Children.Add(startupStatus);
        AddBlock("Inicialização", startupPanel);

        // aparência
        var themePanel = new StackPanel { Spacing = 8 };
        var themeCombo = new ComboBox { Width = 220 };
        foreach (var item in new[] { "Escuro", "Claro", "Sistema" }) themeCombo.Items.Add(item);
        themeCombo.SelectedItem = App.Settings.Theme;
        themeCombo.SelectionChanged += (_, _) =>
        {
            if (themeCombo.SelectedItem is string theme)
            {
                App.Settings.Theme = theme;
                SettingsService.Save();
                RootGrid.RequestedTheme = theme switch
                {
                    "Claro" => ElementTheme.Light,
                    "Sistema" => ElementTheme.Default,
                    _ => ElementTheme.Dark
                };
            }
        };
        themePanel.Children.Add(themeCombo);
        themePanel.Children.Add(Muted("O tema vale para as janelas abertas agora; o HUD e o aviso seguem o sistema."));
        AddBlock("Aparência", themePanel);

        // dados e ferramentas
        var dataPanel = new StackPanel { Spacing = 8 };
        var dataRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openData = new Button { Content = "Abrir pasta de dados" };
        openData.Click += (_, _) => OpenPath(AppPaths.DataDir);
        var openTools = new Button { Content = "Abrir pasta de ferramentas" };
        openTools.Click += (_, _) => OpenPath(AppPaths.ToolsDir);
        var reload = new Button { Content = "Recarregar ferramentas" };
        reload.Click += (_, _) => App.ReloadCatalog();
        dataRow.Children.Add(openData);
        dataRow.Children.Add(openTools);
        dataRow.Children.Add(reload);
        dataPanel.Children.Add(dataRow);
        dataPanel.Children.Add(Muted("Ferramentas do usuário: " + AppPaths.ToolsDir));
        dataPanel.Children.Add(Muted("Dados: " + AppPaths.DataDir));
        AddBlock("Dados e ferramentas", dataPanel);

        // histórico
        var historyPanel = new StackPanel { Spacing = 8 };
        var historyToggle = new ToggleSwitch
        {
            Header = "Manter histórico de execuções",
            IsOn = App.Settings.KeepHistory,
            OnContent = "ligado",
            OffContent = "desligado"
        };
        historyToggle.Toggled += (_, _) =>
        {
            App.Settings.KeepHistory = historyToggle.IsOn;
            SettingsService.Save();
        };
        var clearHistory = new Button { Content = "Apagar histórico" };
        clearHistory.Click += (_, _) => { try { File.Delete(AppPaths.HistoryFile); } catch { } };
        historyPanel.Children.Add(historyToggle);
        historyPanel.Children.Add(clearHistory);
        AddBlock("Histórico", historyPanel);

        // busca rápida (HUD)
        var hudPanel = new StackPanel { Spacing = 8 };
        var countToggle = new ToggleSwitch
        {
            Header = "Ocultar contagem de ferramentas",
            IsOn = App.Settings.HideToolCount,
            OnContent = "oculta",
            OffContent = "mostra"
        };
        countToggle.Toggled += (_, _) =>
        {
            App.Settings.HideToolCount = countToggle.IsOn;
            SettingsService.Save();
        };
        hudPanel.Children.Add(countToggle);
        hudPanel.Children.Add(Muted(
            "Vale para o rodapé da busca rápida (Ctrl+Alt+Space). A contagem continua aparecendo quando " +
            "nenhuma ferramenta é encontrada — ali o número é o próprio aviso."));
        AddBlock("Busca rápida", hudPanel);

        // atualizações
        var updatePanel = new StackPanel { Spacing = 8 };
        var updateVersion = Muted($"Versão instalada: v{UpdateService.CurrentVersion}");
        var updateStatus = Muted("Ainda não verificado nesta sessão.");
        var updateBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0, Width = 360,
            HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed
        };

        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var checkNow = new Button
        {
            Content = "Procurar por atualização agora",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var installNow = new Button { Content = "Baixar e instalar", Visibility = Visibility.Collapsed };
        var restartNow = new Button { Content = "Reiniciar e atualizar", Visibility = Visibility.Collapsed };
        updateRow.Children.Add(checkNow);
        updateRow.Children.Add(installNow);
        updateRow.Children.Add(restartNow);

        void RefreshUpdateState()
        {
            var last = UpdateService.LastCheck;
            var pendingDir = UpdateService.PendingPayloadDir();

            if (pendingDir is not null)
            {
                updateStatus.Text = $"Pacote v{App.Settings.PendingUpdateVersion} baixado. Reinicie para concluir a atualização.";
                installNow.Visibility = Visibility.Collapsed;
                restartNow.Visibility = Visibility.Visible;
                return;
            }

            restartNow.Visibility = Visibility.Collapsed;

            if (last is null)
            {
                updateStatus.Text = "Ainda não verificado nesta sessão.";
                installNow.Visibility = Visibility.Collapsed;
            }
            else
            {
                var when = last.CheckedAtUtc.ToLocalTime().ToString("dd/MM 'às' HH:mm");
                updateStatus.Text = UpdateService.Describe(last) + $" (verificado em {when})";
                installNow.Visibility = last.HasUpdate ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        async void RunUpdateCheck()
        {
            if (!checkNow.IsEnabled) return;   // já existe uma verificação em andamento
            checkNow.IsEnabled = false;
            installNow.Visibility = Visibility.Collapsed;
            updateBar.IsIndeterminate = true;
            updateBar.Visibility = Visibility.Visible;
            updateStatus.Text = "Consultando o canal de atualização…";
            try
            {
                var result = await UpdateService.CheckAsync();
                UpdateService.LastCheck = result;
                App.Settings.LastUpdateCheckUtc = result.CheckedAtUtc.ToString("o");
                SettingsService.Save();
            }
            catch (Exception ex)
            {
                AppPaths.Log("update check (UI): " + ex);
                updateStatus.Text = "Falha ao verificar: " + ex.Message;
            }
            finally
            {
                updateBar.IsIndeterminate = false;
                updateBar.Visibility = Visibility.Collapsed;
                checkNow.IsEnabled = true;
                RefreshUpdateState();
            }
        }
        _runUpdateCheck = RunUpdateCheck;

        checkNow.Click += (_, _) => RunUpdateCheck();

        installNow.Click += async (_, _) =>
        {
            var last = UpdateService.LastCheck;
            if (last is null || !last.HasUpdate) return;

            checkNow.IsEnabled = false;
            installNow.IsEnabled = false;
            updateBar.IsIndeterminate = false;
            updateBar.Value = 0;
            updateBar.Visibility = Visibility.Visible;

            var progress = new Progress<double>(p =>
            {
                updateBar.Value = p * 100;
                updateStatus.Text = p < 0.88
                    ? $"Baixando a versão {last.LatestVersion}… {p:P0}"
                    : $"Conferindo e extraindo… {p:P0}";
            });

            try
            {
                await UpdateService.DownloadAsync(last, progress);
                updateStatus.Text = $"Versão {last.LatestVersion} baixada. Clique em \"Reiniciar e atualizar\".";
                AppPaths.Log($"update: {last.LatestVersion} baixada pelo botão");
            }
            catch (Exception ex)
            {
                AppPaths.Log("update download (UI): " + ex);
                updateStatus.Text = "Não foi possível baixar: " + ex.Message;
            }
            finally
            {
                updateBar.Visibility = Visibility.Collapsed;
                installNow.IsEnabled = true;
                checkNow.IsEnabled = true;
                RefreshUpdateState();
            }
        };

        restartNow.Click += (_, _) =>
        {
            var dir = UpdateService.PendingPayloadDir();
            if (dir is null)
            {
                updateStatus.Text = "O pacote baixado não está mais disponível. Procure novamente.";
                RefreshUpdateState();
                return;
            }

            restartNow.IsEnabled = false;
            if (!UpdateService.ApplyAndRestart(dir, out var error))
            {
                updateStatus.Text = error;
                restartNow.IsEnabled = true;
                return;
            }

            updateStatus.Text = "Reiniciando para aplicar a atualização…";
            App.ExitForUpdate();
        };

        var autoCheckToggle = new ToggleSwitch
        {
            Header = "Procurar automaticamente (1x por dia)",
            IsOn = App.Settings.AutoCheckUpdates,
            OnContent = "ligado",
            OffContent = "desligado"
        };
        autoCheckToggle.Toggled += (_, _) =>
        {
            App.Settings.AutoCheckUpdates = autoCheckToggle.IsOn;
            SettingsService.Save();
        };

        updatePanel.Children.Add(updateVersion);
        updatePanel.Children.Add(updateRow);
        updatePanel.Children.Add(updateBar);
        updatePanel.Children.Add(updateStatus);
        updatePanel.Children.Add(autoCheckToggle);
        updatePanel.Children.Add(Muted("Canal: " + UpdateService.FeedUrl));
        updatePanel.Children.Add(Muted("A troca de versão fecha o app, substitui a pasta do programa e abre de novo. Se a versão nova não abrir, a anterior volta sozinha."));
        AddBlock("Atualizações", updatePanel);
        RefreshUpdateState();

        // sobre
        var about = new StackPanel { Spacing = 6 };
        var version = typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        about.Children.Add(Muted($"Analista Hub {version} · Windows App SDK + WinUI 3 · sem administrador"));
        about.Children.Add(Muted("Ferramentas são scripts; o HUB só orquestra entrada, execução e apresentação."));
        var log = new Button { Content = "Abrir log" };
        log.Click += (_, _) => OpenPath(AppPaths.LogFile);
        about.Children.Add(log);
        AddBlock("Sobre", about);
    }
}

/// <summary>Atalho na pasta Inicializar (Task Scheduler com gatilho de boot é negado nesta máquina).</summary>
public static class StartupShortcut
{
    private const string FileName = "Analista Hub.lnk";

    public static string Location =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), FileName);

    public static bool Exists() => File.Exists(Location);

    public static string? Set(bool enable)
    {
        try
        {
            if (!enable)
            {
                if (File.Exists(Location)) File.Delete(Location);
                return null;
            }

            var exe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "AnalistaHub.exe");
            var type = Type.GetTypeFromProgID("WScript.Shell");
            if (type is null) return "WScript.Shell indisponível nesta máquina.";
            dynamic shell = Activator.CreateInstance(type)!;
            dynamic shortcut = shell.CreateShortcut(Location);
            shortcut.TargetPath = exe;
            shortcut.WorkingDirectory = Path.GetDirectoryName(exe) ?? AppContext.BaseDirectory;
            if (File.Exists(AppPaths.IconFile)) shortcut.IconLocation = AppPaths.IconFile;
            shortcut.Description = "Analista Hub";
            shortcut.Save();
            return null;
        }
        catch (Exception ex)
        {
            AppPaths.Log("startup shortcut: " + ex.Message);
            return "Não foi possível criar o atalho: " + ex.Message;
        }
    }
}
