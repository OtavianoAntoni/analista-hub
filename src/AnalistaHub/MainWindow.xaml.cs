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
    /// <summary>Assinatura viva do UpdateFlow na tela de Configurações (a página é reconstruída).</summary>
    private Action<UpdateState>? _updateFlowHandler;
    /// <summary>
    /// HOME-001: campo e ação "Buscar" de cada card da página Início. Existe para o diagnóstico
    /// `--home-buscar` acionar o MESMO caminho do usuário, sem clique sintético.
    /// </summary>
    private readonly Dictionary<string, (TextBox Entrada, Button Buscar, Action Disparar)> _homeCampos =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _nudging;

    // UI-004 (2.3): tamanho ÚNICO em DIP (1120×760), que o Windows converte para pixels conforme a
    // escala do monitor (125 % ⇒ 1400×950). Fixar elimina o reflow a cada arrasto — e o defeito de
    // área não pintada que ele provocava. O retângulo real é guardado depois que o DPI assenta e um
    // vigia de 300 ms o restaura (maximizar por Win+↑ não passa pelo presenter).
    private const int FixedWidthDip = 1120;
    private const int FixedHeightDip = 760;
    private Native.RECT _rectFixo;
    private double _escalaFixa = 1.0;
    private bool _travado;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _vigia;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Analista Hub";
        AppWindow.Resize(new SizeInt32(FixedWidthDip, FixedHeightDip));
        CenterOnScreen();
        UiKit.ApplyChrome(this, round: false);

        // Fechar no X destrói a janela nesta versão do WinAppSDK (AppWindow.Closing não é acionado
        // nem cancelável). O app continua vivo pela janela oculta da bandeja, e qualquer ação de
        // "abrir" recria a janela na hora — ver App.EnsureMainWindow.
        Closed += (_, _) => AppPaths.Log("janela principal fechada — app segue na bandeja, abrir recria");

        // O empurrão de re-render NÃO roda aqui: mexer no tamanho de uma janela que ainda não foi
        // mostrada deixa a ilha XAML sem conteúdo (janela branca). Só depois de Show + Activate.
        Activated += OnFirstActivated;

        UiKit.ApplyTheme(RootGrid);

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
        LockWindowSize();
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
        timer.Tick += (_, _) =>
        {
            NudgeRender();
            FixarTamanhoAtual();
            IniciarVigia();
        };
        timer.Start();
    }

    /// <summary>
    /// Guarda o retângulo REAL depois que o DPI assentou. É esse retângulo (não um valor em DIP) que
    /// o vigia restaura: maximizar por WM_SYSCOMMAND não passa pelo presenter nem pelo
    /// AppWindow.Changed, então a única testemunha confiável é o GetWindowRect.
    /// </summary>
    private void FixarTamanhoAtual()
    {
        try
        {
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;
            if (r.Right - r.Left <= 0) return;

            _rectFixo = r;
            _escalaFixa = UiKit.Scale(this);
            _travado = true;
            AppPaths.Log($"janela fixa: retângulo {r.Right - r.Left}×{r.Bottom - r.Top} em {r.Left},{r.Top} " +
                         $"(escala {_escalaFixa:0.##})");
        }
        catch (Exception ex) { AppPaths.Log("fixar tamanho: " + ex.Message); }
    }

    private void IniciarVigia()
    {
        try
        {
            if (_vigia is not null) return;
            _vigia = DispatcherQueue.CreateTimer();
            _vigia.Interval = TimeSpan.FromMilliseconds(300);
            _vigia.IsRepeating = true;
            _vigia.Tick += (_, _) => VigiarTamanho();
            _vigia.Start();
        }
        catch (Exception ex) { AppPaths.Log("vigia: " + ex.Message); }
    }

    private void VigiarTamanho()
    {
        try
        {
            if (!_travado || !AppWindow.IsVisible) return;
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter
                { State: Microsoft.UI.Windowing.OverlappedPresenterState.Minimized }) return;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero || !Native.GetWindowRect(hwnd, out var r)) return;

            // Mudou de monitor (DPI diferente)? O Windows já ajustou o tamanho; adotar em vez de
            // brigar — e o alvo passa a ser o novo, coerente com a escala do monitor.
            var escala = UiKit.Scale(this);
            if (Math.Abs(escala - _escalaFixa) > 0.01)
            {
                AppPaths.Log($"janela fixa: DPI {_escalaFixa:0.##}→{escala:0.##}; adotando o novo retângulo");
                FixarTamanhoAtual();
                NudgeRender();
                return;
            }

            var largura = r.Right - r.Left;
            var altura = r.Bottom - r.Top;
            var alvoL = _rectFixo.Right - _rectFixo.Left;
            var alvoA = _rectFixo.Bottom - _rectFixo.Top;
            if (Math.Abs(largura - alvoL) <= 4 && Math.Abs(altura - alvoA) <= 4) return;

            AppPaths.Log($"janela fixa: vigia viu {largura}×{altura} (alvo {alvoL}×{alvoA}); restaurando");
            Native.SetWindowPos(hwnd, IntPtr.Zero, _rectFixo.Left, _rectFixo.Top, alvoL, alvoA,
                Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
            NudgeRender();
        }
        catch (Exception ex) { AppPaths.Log("vigia: " + ex.Message); }
    }

    /// <summary>
    /// Tamanho ÚNICO da janela principal (UI-004): 1120×760 px, centrada na área útil do monitor onde
    /// está o cursor e limitada a 94 % dela. Fixar elimina o reflow a cada arrasto — e o defeito de
    /// área não pintada que aparecia ao esticar. Em DPI diferente de 100 % o Windows pode escalar a
    /// janela ao movê-la entre monitores (limitação do WinUI 3 nesta versão); o tamanho em 100 % é o
    /// que o app garante.
    /// </summary>
    private void CenterOnScreen()
    {
        var (left, top, right, bottom) = UiKit.WorkAreaAtCursor();
        var maxWidth = (int)((right - left) * 0.94);
        var maxHeight = (int)((bottom - top) * 0.94);
        var size = AppWindow.Size;

        var width = Math.Min(size.Width, Math.Max(900, maxWidth));
        var height = Math.Min(size.Height, Math.Max(600, maxHeight));

        AppWindow.MoveAndResize(new RectInt32(
            left + Math.Max(0, (right - left - width) / 2),
            top + Math.Max(0, (bottom - top - height) / 2),
            width, height));

        AppPaths.Log($"janela: {size.Width}×{size.Height} → {width}×{height} px " +
                     $"(área útil {right - left}×{bottom - top})");
    }

    /// <summary>
    /// Tira a borda de redimensionamento e o maximizar. O presenter cuida do lado do WinUI; os
    /// estilos clássicos (WS_THICKFRAME/WS_MAXIMIZEBOX) garantem que o Windows não devolva a alça ao
    /// mover a janela para outro monitor ou ao reaplicar estilos.
    /// Subclass em WM_SYSCOMMAND não serve aqui: o WinUI troca o WndProc da janela depois (a mesma
    /// razão pela qual o WM_CLOSE não é cancelável — SPEC §5.3). Maximizar por Win+↑ é revertido pelo
    /// guard de tamanho em <see cref="HookDpiChanges"/>.
    /// </summary>
    private void LockWindowSize()
    {
        try
        {
            if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
            {
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
            }

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (hwnd == IntPtr.Zero) return;

            var style = Native.GetWindowLongPtr(hwnd, Native.GWL_STYLE).ToInt64();
            var semBorda = style & ~(Native.WS_THICKFRAME | Native.WS_MAXIMIZEBOX);
            if (semBorda == style)
            {
                AppPaths.Log("janela fixa: já sem borda de redimensionamento");
                return;
            }

            Native.SetWindowLongPtr(hwnd, Native.GWL_STYLE, (IntPtr)semBorda);
            Native.SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_FRAMECHANGED);
            AppPaths.Log("janela fixa: borda de redimensionamento e maximizar removidos");
        }
        catch (Exception ex) { AppPaths.Log("lock size: " + ex.Message); }
    }

    /// <summary>Diagnóstico: rola a página atual (usado por <c>--scroll-y</c> para provar blocos abaixo da dobra).</summary>
    public void ScrollContentTo(double offset) => ContentScroll.ChangeView(null, offset, null, true);

    public void SetHotkeyWarning(string message)
    {
        HotkeyWarning.Text = message;
        HotkeyWarning.Visibility = Visibility.Visible;
    }

    public void UpdateHotkeyHint()
    {
        HotkeyHint.Text = $"Atalho de ativação: {App.Settings.Hotkey}";
    }

    public void RebuildNav()
    {
        var selected = (NavList.SelectedItem as NavItem)?.Title;
        var items = new List<NavItem>
        {
            new() { Title = "Início", Glyph = "\uE80F", IconVisual = ToolIcons.Material("home", 15), Kind = "home" }
        };
        items.AddRange(App.Catalog.Tools.Select(t => new NavItem
        {
            Title = t.IsBroken ? $"{t.Name} (com problema)" : t.Name,
            Glyph = t.IconGlyph,
            IconVisual = ToolIcons.Create(t, 20, 15),
            ToolId = t.Id,
            Kind = "tool"
        }));
        items.Add(new NavItem { Title = "Configurações", Glyph = "\uE713", IconVisual = ToolIcons.Material("settings", 15), Kind = "settings" });

        NavList.ItemsSource = items;
        NavList.SelectedItem = items.FirstOrDefault(i => i.Title == selected) ?? items[0];
    }

    private void OnNavSelectionChanged(object sender, SelectionChangedEventArgs e) => RenderSelectedPage();

    /// <summary>
    /// Desenha a página selecionada. Separado do evento porque a troca de tema precisa REDESENHAR a
    /// página: os elementos são criados em código com a cor do tema vigente na criação, e só trocar
    /// RequestedTheme deixava metade da tela com as cores antigas.
    /// </summary>
    public void RenderSelectedPage()
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

    /// <summary>Aplica a preferência de tema nesta janela e redesenha o conteúdo.</summary>
    public void ApplyThemeNow()
    {
        UiKit.ApplyTheme(RootGrid);
        UiKit.ApplyChrome(this, round: false);
        RenderSelectedPage();
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
    /// UPD-003: "O que mudou nesta versão" abre sozinho no 1º início após a atualização, com o texto
    /// RESUMIDO (CHANGELOG_PUBLIC.md) e o scroll no topo. Se o pacote não trouxe o resumo, cai para o
    /// PATCHLOG técnico — é o fallback registrado no patch request.
    /// </summary>
    public async void ShowPublicWhatsNew()
    {
        var versao = UpdateService.CurrentVersion;
        var resumido = PatchNotes.PublicSectionFor(versao);
        var texto = resumido ?? PatchNotes.SectionFor(versao);
        if (string.IsNullOrWhiteSpace(texto))
        {
            AppPaths.Log("whatsNew automático: sem changelog no pacote — aviso não abriu");
            return;
        }

        // O changelog é markdown simples (`**Tema**` e `- item`). O ContentDialog mostra texto puro,
        // então o mínimo é traduzido aqui — asterisco literal na tela pareceria defeito.
        var corpoTexto = new StackPanel { Spacing = 6 };
        foreach (var linha in texto.Replace("\r", "").Split('\n'))
        {
            var t = linha.Trim();
            if (t.Length == 0) { corpoTexto.Children.Add(new TextBlock { Height = 4 }); continue; }

            var negrito = t.StartsWith("**") && t.EndsWith("**") && t.Length > 4;
            if (negrito) t = t[2..^2];
            else if (t.StartsWith("- ")) t = "• " + t[2..];

            corpoTexto.Children.Add(new TextBlock
            {
                Text = t,
                TextWrapping = TextWrapping.Wrap,
                FontSize = 12.5,
                FontWeight = negrito ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal,
                Margin = negrito ? new Thickness(0, 6, 0, 0) : new Thickness(0)
            });
        }

        var corpo = new ScrollViewer
        {
            Content = corpoTexto,
            MaxHeight = 420,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
        // scroll no TOPO assim que o conteúdo mede (o padrão pode herdar a posição do último aviso)
        corpo.Loaded += (_, _) => corpo.ChangeView(null, 0, null, true);

        var dialog = new ContentDialog
        {
            Title = "O que mudou nesta versão",
            Content = corpo,
            CloseButtonText = "Fechar",
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };

        try
        {
            await dialog.ShowAsync();
            // marca só depois de realmente exibido: falha aqui volta a tentar no próximo início
            App.Settings.PublicChangelogSeenVersion = UpdateService.Normalize(versao);
            SettingsService.Save();
            AppPaths.Log($"whatsNew automático: v{versao} exibida ({texto.Length} caracteres, " +
                         $"{(resumido is null ? "PATCHLOG técnico" : "changelog público")})");
        }
        catch (Exception ex) { AppPaths.Log("whatsNew automático: " + ex.Message); }
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

    /// <summary>
    /// UI-001: fechar pede confirmação DENTRO da janela (ContentDialog, foco travado). "Mudei de
    /// ideia" e Esc cancelam; só "Encerrar" termina o processo (código 0).
    /// </summary>
    private async void OnExitClick(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            Title = "Encerrar o Analista Hub?",
            Content = "Isso irá encerrar a aplicação, deseja continuar?",
            PrimaryButtonText = "Encerrar",
            CloseButtonText = "Mudei de ideia",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = (Content as FrameworkElement)?.XamlRoot
        };

        if (Application.Current.Resources.TryGetValue("HubDangerButton", out var danger) && danger is Style dangerStyle)
            dialog.PrimaryButtonStyle = dangerStyle;

        ContentDialogResult resultado;
        try { resultado = await dialog.ShowAsync(); }
        catch (Exception ex) { AppPaths.Log("confirmar saída: " + ex.Message); return; }

        if (resultado == ContentDialogResult.Primary)
        {
            AppPaths.Log("saída confirmada pelo usuário");
            App.ExitApp();
        }
        else AppPaths.Log("saída cancelada pelo usuário");
    }

    private void OnMinimizeClick(object sender, RoutedEventArgs e)
    {
        // UI-002: minimizar = esconder para a bandeja, processo vivo. Não chamamos Minimize() para
        // não ocupar a barra de tarefas, e não fechamos a janela: fechar de verdade desmonta o host
        // XAML e a próxima abertura viria em branco (corrigido no 2.1).
        try
        {
            AppWindow.Hide();
            AppPaths.Log("janela principal: escondida na bandeja (processo segue vivo)");
        }
        catch (Exception ex) { AppPaths.Log("minimizar: " + ex.Message); }
    }

    // ================================================================== captura de atalho (SQ-003)

    private static bool TeclaDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    private static bool EhModificador(VirtualKey key) => key is VirtualKey.Control or VirtualKey.Shift
        or VirtualKey.Menu or VirtualKey.LeftWindows or VirtualKey.RightWindows;

    private static string NomeTecla(VirtualKey key)
    {
        var v = (int)key;
        if (v is >= 0x30 and <= 0x39) return ((char)v).ToString();
        if (v is >= 0x41 and <= 0x5A) return ((char)v).ToString();
        if (v is >= 0x70 and <= 0x87) return "F" + (v - 0x6F);
        return key switch
        {
            VirtualKey.Space => "Space",
            VirtualKey.Enter => "Enter",
            VirtualKey.Tab => "Tab",
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

    /// <summary>Muda a cor de um texto de status sem depender de estilo.</summary>
    private static void PintarStatus(TextBlock alvo, string texto, bool erro)
    {
        alvo.Text = texto;
        alvo.Foreground = new SolidColorBrush(erro ? UiKit.Bad : UiKit.DimText);
    }

    // ================================================================== widgets
    private Border Card(UIElement child, Thickness? padding = null) => new()
    {
        Background = new SolidColorBrush(UiKit.Card),
        BorderBrush = new SolidColorBrush(UiKit.CardStroke),
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
        Foreground = new SolidColorBrush(UiKit.DimText)
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
        _homeCampos.Clear();
        AddPageHeader("Início", "Cada ferramenta agrupa um script e sabe se apresentar: para que serve, como executar, o que exige e como entrega o resultado.");

        

        var tools = App.Catalog.Tools;
        if (tools.Count == 0)
        {
            AddBlock("Nenhuma ferramenta instalada", Para(
                $"Coloque manifestos .json em:\n{AppPaths.ToolsDir}\n\nCada .json descreve uma ferramenta e o script que ela executa."));
            return;
        }

        foreach (var tool in tools)
        {
            var card = new StackPanel { Spacing = 10 };

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var left = new StackPanel { Spacing = 4 };
            var titleRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            titleRow.Children.Add(ToolIcons.Create(tool, 18, 14));
            titleRow.Children.Add(new TextBlock { Text = tool.Name, FontSize = 15, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            left.Children.Add(titleRow);
            left.Children.Add(Muted(tool.IsBroken ? "Manifesto com problema: " + tool.LoadError : tool.Description));
            row.Children.Add(left);

            var status = new TextBlock { Text = "○", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetColumn(status, 1);
            row.Children.Add(status);
            card.Children.Add(row);

            if (!tool.IsBroken)
            {
                // HOME-001: o card traz o input que a ferramenta espera + "Buscar" à direita.
                // Ferramenta de base local (Consulta SSE) pede termo livre.
                var campo = tool.IsDataset ? null : tool.Inputs.FirstOrDefault();

                // Largura fixa no campo: o card vive num StackPanel vertical (largura infinita na
                // medição), então coluna "*" não distribui espaço — a linha inteira estouraria a
                // janela e o botão sairia de vista.
                var linhaBusca = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };

                var entrada = new TextBox
                {
                    PlaceholderText = campo is not null && !string.IsNullOrWhiteSpace(campo.Placeholder)
                        ? campo.Placeholder
                        : tool.IsDataset ? "SIGLA, cidade ou termo" : "valor para consultar",
                    FontSize = 13.5,
                    Width = 460
                };

                var buscar = new Button
                {
                    Content = "Buscar",
                    IsEnabled = false,
                    VerticalAlignment = VerticalAlignment.Bottom
                };
                if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accentStyle) &&
                    accentStyle is Style estilo)
                    buscar.Style = estilo;

                var aviso = new TextBlock
                {
                    FontSize = 11.5,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(UiKit.Bad),
                    Visibility = Visibility.Collapsed
                };

                void Disparar()
                {
                    var valor = entrada.Text.Trim();
                    if (valor.Length == 0) return;

                    // validação básica por ferramenta: IP/MAC inválido não dispara consulta
                    if (campo is not null)
                    {
                        var problema = FieldValidation.Check(campo, valor);
                        if (problema is not null)
                        {
                            aviso.Text = problema;
                            aviso.Visibility = Visibility.Visible;
                            return;
                        }
                    }

                    aviso.Visibility = Visibility.Collapsed;

                    if (tool.IsDataset)
                    {
                        SsePanelController.Open(valor);
                        return;
                    }

                    // HOME-001: mesma execução da busca rápida — abre os campos já preenchidos e
                    // consulta. Não duplica validação nem apresentação de resultado.
                    HudController.ShowTool(tool.Id);
                    HudController.FillFirstField(valor);
                    HudController.Submit();
                }

                entrada.TextChanged += (_, _) =>
                {
                    buscar.IsEnabled = entrada.Text.Trim().Length > 0;
                    if (aviso.Visibility == Visibility.Visible) aviso.Visibility = Visibility.Collapsed;
                };
                entrada.KeyDown += (_, e) =>
                {
                    if (e.Key != VirtualKey.Enter) return;
                    e.Handled = true;
                    if (buscar.IsEnabled) Disparar();
                };
                buscar.Click += (_, _) => Disparar();

                linhaBusca.Children.Add(entrada);
                linhaBusca.Children.Add(buscar);

                card.Children.Add(linhaBusca);
                card.Children.Add(aviso);
                _homeCampos[tool.Id] = (entrada, buscar, Disparar);
            }

            ContentPanel.Children.Add(Card(card));

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
            color = UiKit.Bad;          // vermelho
            tooltip = $"Sem resposta de {tool.Network!.Host}:{tool.Network.Port} — verifique a VPN";
        }
        else if (auth.State == AuthState.Refused)
        {
            color = UiKit.Warn;           // amarelo
            tooltip = "Autenticação recusada pelo serviço: " + auth.Message +
                      " — corrija as credenciais na aba da ferramenta";
        }
        else if (requiresCredentials && !creds.Complete)
        {
            color = UiKit.Warn;           // amarelo
            tooltip = "Credenciais incompletas: faltando " + string.Join(", ", creds.Missing);
        }
        else if (reachable == true && auth.State != AuthState.Unreachable)
        {
            color = UiKit.Ok;          // verde
            tooltip = auth.State == AuthState.Ok
                ? $"{tool.Network?.Label ?? "serviço"} respondeu · credenciais aceitas ({auth.When:HH:mm})"
                : $"{tool.Network?.Label ?? "serviço"} respondeu · credenciais não verificadas ainda";
        }
        else
        {
            color = UiKit.Unknown;          // cinza
            tooltip = "Status não verificado";
        }

        target.Text = "●";
        target.Foreground = new SolidColorBrush(color);
        ToolTipService.SetToolTip(target, tooltip);
    }

    /// <summary>
    /// Diagnóstico (`--home-buscar id valor`): escreve no campo do card da página Início e aciona o
    /// "Buscar" pelo mesmo caminho do usuário (TextChanged → validação → consulta).
    /// </summary>
    public void HomeBuscar(string toolId, string valor)
    {
        if (!_homeCampos.TryGetValue(toolId, out var par))
        {
            AppPaths.Log($"home-buscar: card «{toolId}» não encontrado");
            return;
        }

        par.Entrada.Text = valor ?? "";

        // O TextChanged do WinUI pode ser entregue depois desta chamada (mesma família do UX-002);
        // esperar um tick é o que garante ler o estado real do botão antes de decidir.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            AppPaths.Log($"home-buscar: «{toolId}» = \"{valor}\" · botão={(par.Buscar.IsEnabled ? "habilitado" : "desabilitado")}");
            if (!par.Buscar.IsEnabled) return;   // vazio: nada dispara (aceite HOME-001)
            par.Disparar();
        });
    }

    // HOME-001 substituiu os botões "Abrir"/"Testar" do card por input + "Buscar", que executa pelo
    // MESMO caminho da busca rápida (HUD). O antigo RunFromDashboardAsync saiu junto: era o único
    // chamador do trecho e duplicava validação e apresentação de resultado.

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
        how.Children.Add(KeyValue("Busca / atalho", $"{App.Settings.Hotkey} → digite o nome da ferramenta → Enter"));
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
            // TOOL-CFG-002: "Testar agora" saiu — a consulta rápida é o campo da página Início
            // e a busca rápida; nenhuma outra ação desta seção dependia dele.
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

            // NET-005: de onde veio a lista de NMs (share, variável, pasta do usuário) e o caminho
            // efetivo. Quando a origem configurada falha, o aviso aparece AQUI — não só no log.
            data.Children.Add(KeyValue("Rede interna", catalog.RedeResumo));
            if (catalog.RedeAviso.Length > 0)
                data.Children.Add(Muted(catalog.RedeAviso));

            // recarrega sem fechar o app: o catálogo é singleton e a lista de NMs pode ter chegado
            // ao share depois da abertura
            var recarregar = new Button { Content = "Recarregar dados" };
            recarregar.Click += (_, _) =>
            {
                var novo = SseCatalog.Reload();
                AppPaths.Log($"Recarregar dados: {novo.RedeResumo}");
                RenderSelectedPage();
                Views.ToastController.ShowMessage("Base recarregada",
                    $"{novo.Cities.Count} cidades · {novo.TotalWithAccess} com NM",
                    novo.RedeResumo);
            };
            data.Children.Add(recarregar);

            // SSE-002: importar a lista de NMs pela UI. Antes só dava para editar config.local.json ou
            // copiar o arquivo à mão — e a consulta ficava sem NM sem o usuário saber o motivo.
            var importar = new Button { Content = "Importar sse-rede.local.json…" };
            importar.Click += async (_, _) =>
            {
                var picker = new Windows.Storage.Pickers.FileOpenPicker();
                WinRT.Interop.InitializeWithWindow.Initialize(picker,
                    WinRT.Interop.WindowNative.GetWindowHandle(this));
                picker.FileTypeFilter.Add(".json");

                var arquivo = await picker.PickSingleFileAsync();
                if (arquivo is null) return;

                var validacao = SseCatalog.ValidarRede(arquivo.Path);
                if (!validacao.Ok)
                {
                    ToastController.ShowFailure(null, "Arquivo recusado",
                        "O JSON não serve como lista de NMs.\n" + validacao.Motivo);
                    return;
                }

                if (File.Exists(Path.Combine(AppPaths.DataDir, "sse-rede.local.json")))
                {
                    var confirmar = new ContentDialog
                    {
                        Title = "Substituir a lista atual?",
                        Content = "Já existe uma lista importada. A atual será guardada como .bak.",
                        PrimaryButtonText = "Substituir",
                        CloseButtonText = "Cancelar",
                        XamlRoot = RootGrid.XamlRoot
                    };
                    if (await confirmar.ShowAsync() != ContentDialogResult.Primary) return;
                }

                var importado = SseCatalog.ImportarRede(arquivo.Path, sobrescrever: true);
                if (!importado.Ok)
                {
                    ToastController.ShowFailure(null, "Não consegui importar", importado.Motivo);
                    return;
                }

                RenderSelectedPage();
                ToastController.ShowMessage("Lista de NMs importada",
                    $"{importado.Cidades} cidade(s)", AppPaths.DataDir + "\\sse-rede.local.json");
            };
            data.Children.Add(importar);

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

        // 3a — (o gatilho ":mkt" que ficava aqui saiu no 2.2.1: a invocação é pelo nome na busca ou
        // pelo atalho de teclado da ferramenta. Ver docs/REGRAS-DE-FERRAMENTA.md, R1.)

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

        // 3b.1 — servidor do inventário (MKT-002). O manifesto traz ${CFG:<chave>} e a página passa a
        // ter editor; a chave vem do catálogo (NetworkRequirement.HostConfigKey), sem if por id.
        if (tool.Network is { } redeHost && redeHost.HostConfigKey.Length > 0)
        {
            var servidor = new StackPanel { Spacing = 8 };
            var campoHost = new TextBox
            {
                Header = "Servidor do inventário (IP ou host)",
                Text = redeHost.Host,
                PlaceholderText = "10.0.0.1",
                FontFamily = new FontFamily("Consolas"),
                Width = 320
            };
            var statusHost = Muted("");
            var confirmarTroca = false;

            var salvarHost = new Button { Content = "Salvar" };
            salvarHost.Click += (_, _) =>
            {
                var problema = ServiceHost.Problema(campoHost.Text, redeHost.Port);
                if (problema is not null)
                {
                    statusHost.Text = problema;
                    return;
                }

                var (host, porta) = ServiceHost.Separar(campoHost.Text, redeHost.Port);
                var atual = LocalConfig.Get(redeHost.HostConfigKey);

                // não sobrescreve configuração existente sem um segundo clique explícito
                if (!confirmarTroca && atual.Length > 0 &&
                    !string.Equals(atual, host, StringComparison.OrdinalIgnoreCase))
                {
                    confirmarTroca = true;
                    statusHost.Text = $"Já configurado como {atual}. Clique em Salvar de novo para substituir.";
                    return;
                }

                if (!LocalConfig.Set(redeHost.HostConfigKey, host))
                {
                    statusHost.Text = "Não consegui gravar em " + LocalConfig.FilePath;
                    return;
                }

                // a validação de credenciais usa outra chave: as duas precisam apontar para o mesmo host
                LocalConfig.Set(tool.Id + ".url", ServiceHost.UrlAuth(host, porta));

                App.ReloadCatalog();
                RenderSelectedPage();
                ToastController.ShowMessage("Servidor salvo", $"{host}:{porta}",
                    "Gravado em config.local.json. A validação de credenciais passa a usar este endereço.");
            };

            var testarHost = new Button { Content = "Testar conexão" };
            testarHost.Click += async (_, _) =>
            {
                var problema = ServiceHost.Problema(campoHost.Text, redeHost.Port);
                if (problema is not null)
                {
                    statusHost.Text = problema;
                    return;
                }

                var (host, porta) = ServiceHost.Separar(campoHost.Text, redeHost.Port);
                testarHost.IsEnabled = false;
                statusHost.Text = $"testando TCP em {host}:{porta}…";
                var ok = await Probes.CheckTcpAsync(host, porta, 3000);
                testarHost.IsEnabled = true;
                statusHost.Text = ok
                    ? $"acessível em {host}:{porta} (TCP; não valida login)"
                    : $"sem resposta de {host}:{porta} — verifique a VPN e o endereço";
            };

            var linhaHost = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            linhaHost.Children.Add(campoHost);
            linhaHost.Children.Add(salvarHost);
            linhaHost.Children.Add(testarHost);

            servidor.Children.Add(linhaHost);
            servidor.Children.Add(statusHost);
            servidor.Children.Add(Muted("Fica em " + LocalConfig.FilePath + ". Testar conexão mede só o " +
                "TCP: não consulta o inventário nem valida a senha."));
            requirements.Children.Add(servidor);
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

            var neutral = UiKit.SubtleStroke;
            var green = UiKit.Ok;
            var amber = UiKit.Warn;

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
                        _ => UiKit.DimText
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
                    Foreground = new SolidColorBrush(UiKit.DimText)
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
        // CFG-001: ocultar da busca rápida (a ferramenta continua no menu lateral e na bandeja)
        var visibilityPanel = new StackPanel { Spacing = 8 };
        var hideToggle = new ToggleSwitch
        {
            IsOn = App.Settings.HiddenTools.Contains(tool.Id),
            OnContent = "oculta da busca",
            OffContent = "aparece na busca"
        };
        hideToggle.Toggled += (_, _) =>
        {
            var ocultas = App.Settings.HiddenTools;
            if (hideToggle.IsOn)
            {
                if (!ocultas.Contains(tool.Id)) ocultas.Add(tool.Id);
            }
            else ocultas.Remove(tool.Id);

            SettingsService.Save();
            AppPaths.Log($"ferramenta {tool.Id}: {(hideToggle.IsOn ? "oculta da busca" : "visível na busca")}");
        };
        visibilityPanel.Children.Add(hideToggle);
        visibilityPanel.Children.Add(Muted(
            "Remove a ferramenta da busca rápida. Ela continua no menu ao lado e no menu do ícone da bandeja."));
        AddBlock("Visibilidade", visibilityPanel);

        // SQ-003: atalho global só desta ferramenta (não abre a busca)
        var atalhoPanel = new StackPanel { Spacing = 8 };
        // TOOL-CFG-003: o campo somente-leitura (combinação em Consolas) saiu da UI — era o "campo
        // técnico" com prefixo "ativo:". A combinação vigente aparece só na linha de status.
        var atalhoAtual = App.Settings.ToolHotkeys.TryGetValue(tool.Id, out var atalhoGravado) ? atalhoGravado : "";
        var atalhoBtn = new Button { Content = "Definir atalho" };
        var atalhoLimpar = new Button { Content = "Remover" };
        var atalhoStatus = Muted("");
        var armado = false;

        void MostrarEstado()
        {
            if (App.HotkeyProblems.TryGetValue(tool.Id, out var problema))
                PintarStatus(atalhoStatus, problema, erro: true);
            else if (string.IsNullOrWhiteSpace(atalhoAtual))
                PintarStatus(atalhoStatus, "sem atalho de ativação — a ferramenta só abre pela busca ou pelo menu", erro: false);
            else
                PintarStatus(atalhoStatus, $"registrado: {atalhoAtual}", erro: false);
        }

        void Capturar(object? s, KeyRoutedEventArgs e)
        {
            if (!armado) return;
            var tecla = e.OriginalKey;
            if (tecla == VirtualKey.Escape)
            {
                armado = false;
                atalhoBtn.Content = "Definir atalho";
                PintarStatus(atalhoStatus, "captura cancelada", erro: false);
                e.Handled = true;
                return;
            }
            if (EhModificador(tecla)) return;          // espera a tecla "de verdade"

            e.Handled = true;

            var mods = new List<string>();
            if (TeclaDown(VirtualKey.Control)) mods.Add("Ctrl");
            if (TeclaDown(VirtualKey.Menu)) mods.Add("Alt");
            if (TeclaDown(VirtualKey.Shift)) mods.Add("Shift");
            if (TeclaDown(VirtualKey.LeftWindows) || TeclaDown(VirtualKey.RightWindows)) mods.Add("Win");

            if (mods.Count == 0)
            {
                PintarStatus(atalhoStatus, "use pelo menos um modificador (Ctrl, Alt, Shift ou Win)", erro: true);
                return;
            }

            var combo = string.Join("+", mods.Append(NomeTecla(tecla)));
            armado = false;
            atalhoBtn.Content = "Definir atalho";

            if (App.ApplyToolHotkey(tool.Id, combo, out var erro))
            {
                atalhoAtual = combo;
                MostrarEstado();
            }
            else PintarStatus(atalhoStatus, erro, erro: true);
        }

        atalhoBtn.Click += (_, _) =>
        {
            armado = true;
            atalhoBtn.Content = "pressione a combinação… (Esc cancela)";
            PintarStatus(atalhoStatus, "captura armada — pressione a combinação agora", erro: false);
            atalhoBtn.Focus(FocusState.Programmatic);
        };

        atalhoLimpar.Click += (_, _) =>
        {
            if (App.ApplyToolHotkey(tool.Id, "", out var erro))
            {
                atalhoAtual = "";
                MostrarEstado();
            }
            else PintarStatus(atalhoStatus, erro, erro: true);
        };

        ContentPanel.KeyDown += Capturar;

        atalhoPanel.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { atalhoBtn, atalhoLimpar }
        });
        atalhoPanel.Children.Add(atalhoStatus);
        atalhoPanel.Children.Add(Muted(
            "Atalho de ativação desta ferramenta: dispara sem abrir a busca. O que acontece ao acionar está em " +
            "«Comportamento do atalho», logo abaixo."));
        AddBlock("Atalho de ativação", atalhoPanel);
        MostrarEstado();

        // CFG-003: o que o atalho faz quando é acionado — por ferramenta, com padrão explícito
        if (!tool.IsDataset && tool.Inputs.Count > 0)
        {
            var comportamento = new StackPanel { Spacing = 6 };
            var atual = SettingsService.ShortcutBehaviorFor(tool.Id);
            foreach (var (valor, rotulo, descricao) in new[]
                     {
                         ("ask", "Aguardar input (padrão)",
                             "abre a ferramenta com o primeiro campo em foco para digitar ou colar"),
                         ("consult", "Consultar direto",
                             "consulta na hora, com o que estiver na área de transferência naquele momento")
                     })
            {
                var rb = new RadioButton
                {
                    Content = $"{rotulo} — {descricao}",
                    GroupName = "atalho-" + tool.Id,
                    IsChecked = string.Equals(atual, valor, StringComparison.OrdinalIgnoreCase),
                    FontSize = 13
                };
                var capturado = valor;
                rb.Checked += (_, _) =>
                {
                    SettingsService.SetShortcutBehavior(tool.Id, capturado);
                    AppPaths.Log($"ferramenta {tool.Id}: comportamento do atalho = {capturado}");
                };
                comportamento.Children.Add(rb);
            }

            comportamento.Children.Add(Muted(
                "Em «Consultar direto», área de transferência vazia ou valor inválido cai para «Aguardar " +
                "input», com um único aviso dizendo o motivo."));
            AddBlock("Comportamento do atalho", comportamento);
        }

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
        AddBlock("Atalho de ativação", hotkeyPanel);

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

                // Troca completa (janela + página + janelas flutuantes) fora deste evento
                ThemeService.Apply();
            }
        };
        themePanel.Children.Add(themeCombo);
        themePanel.Children.Add(Muted("O tema vale para todas as janelas do HUB (principal, busca rápida, aviso e consulta)."));
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
        // SSE-002: o mesmo aviso da página da ferramenta, para a ausência da lista não passar batida.
        dataPanel.Children.Add(Muted("Lista de NMs: " + SseCatalog.Current.RedeResumo));
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

        // ícone da bandeja (CFG-004): o que o clique esquerdo faz. Rótulos fixos nas duas pontas e
        // descrição viva abaixo — mesmo desenho de slide, sem inventar controle novo.
        var trayPanel = new StackPanel { Spacing = 8 };
        var trayRow = new Grid { ColumnSpacing = 10 };
        trayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        trayRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var trayWindowLabel = new TextBlock
        {
            Text = "Abrir janela principal",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var traySwitch = new ToggleSwitch
        {
            IsOn = SettingsService.TrayLeftClickActionFor() == "search",
            MinWidth = 0,
            Margin = new Thickness(0),
            // os rótulos do slide são os dois TextBlock das pontas; nada de "Ativado/Desativado"
            OnContent = null,
            OffContent = null
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(traySwitch,
            "Clique esquerdo no ícone da bandeja");
        var traySearchLabel = new TextBlock
        {
            Text = "Abrir busca rápida",
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center
        };
        var trayDesc = Muted("");
        void UpdateTrayDesc() =>
            trayDesc.Text = traySwitch.IsOn
                ? "O clique esquerdo no ícone da bandeja abre a busca rápida."
                : "O clique esquerdo no ícone da bandeja abre a janela principal do Analista HUB.";
        UpdateTrayDesc();

        traySwitch.Toggled += (_, _) =>
        {
            App.Settings.TrayLeftClickAction = traySwitch.IsOn ? "search" : "window";
            SettingsService.Save();
            UpdateTrayDesc();
            App.Tray?.SetTooltip(App.TooltipBandeja());
            AppPaths.Log("bandeja: clique esquerdo = " + App.Settings.TrayLeftClickAction);
        };

        Grid.SetColumn(traySwitch, 1);
        Grid.SetColumn(traySearchLabel, 2);
        trayRow.Children.Add(trayWindowLabel);
        trayRow.Children.Add(traySwitch);
        trayRow.Children.Add(traySearchLabel);

        trayPanel.Children.Add(trayRow);
        trayPanel.Children.Add(trayDesc);
        trayPanel.Children.Add(Muted(
            "O clique direito continua abrindo o menu (Restaurar / Sair). O duplo clique faz o mesmo " +
            "que o clique esquerdo."));
        AddBlock("Ícone da bandeja", trayPanel);

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

        // SQ-002: fechar ao clicar fora (padrão ligado)
        var outsideToggle = new ToggleSwitch
        {
            Header = "Fechar a busca ao clicar fora",
            IsOn = App.Settings.HudCloseOnClickOutside,
            OnContent = "fecha",
            OffContent = "continua aberta"
        };
        outsideToggle.Toggled += (_, _) =>
        {
            App.Settings.HudCloseOnClickOutside = outsideToggle.IsOn;
            SettingsService.Save();
        };
        hudPanel.Children.Add(outsideToggle);
        hudPanel.Children.Add(Muted(
            "Vale para o rodapé da busca rápida (Ctrl+Alt+Space). A contagem continua aparecendo quando " +
            "nenhuma ferramenta é encontrada — ali o número é o próprio aviso."));
        AddBlock("Busca rápida", hudPanel);

        // ping do destino (NET-001): espera entre a última tecla e o ping, e timeout do pacote
        var pingPanel = new StackPanel { Spacing = 8 };
        var pingRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var debounceBox = new NumberBox
        {
            Header = "Espera depois da última tecla (ms)",
            Value = App.Settings.PingDebounceMs,
            Minimum = 100,
            Maximum = 2000,
            SmallChange = 50,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 230
        };
        var pingTimeoutBox = new NumberBox
        {
            Header = "Tempo máximo por pacote (ms)",
            Value = App.Settings.PingTimeoutMs,
            Minimum = 200,
            Maximum = 5000,
            SmallChange = 100,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Width = 210
        };
        var savePing = new Button { Content = "Salvar", VerticalAlignment = VerticalAlignment.Bottom };
        savePing.Click += (_, _) =>
        {
            App.Settings.PingDebounceMs = (int)Math.Clamp(
                double.IsNaN(debounceBox.Value) ? 400 : debounceBox.Value, 100, 2000);
            App.Settings.PingTimeoutMs = (int)Math.Clamp(
                double.IsNaN(pingTimeoutBox.Value) ? 1000 : pingTimeoutBox.Value, 200, 5000);
            SettingsService.Save();
        };
        pingRow.Children.Add(debounceBox);
        pingRow.Children.Add(pingTimeoutBox);
        pingRow.Children.Add(savePing);
        pingPanel.Children.Add(pingRow);
        pingPanel.Children.Add(Muted(
            "Vale para campos de IP que declaram ping (ex.: :mkt). Cada consulta admite até 100 pings; " +
            "reabra a ferramenta para reiniciar a contagem."));
        AddBlock("Ping do destino", pingPanel);

        // MKT-004: tamanho global dos ícones das ferramentas, com preview ao vivo
        var iconPanel = new StackPanel { Spacing = 8 };
        var iconCombo = new ComboBox { Width = 220 };
        iconCombo.Items.Add("Pequeno");
        iconCombo.Items.Add("Médio (padrão)");
        iconCombo.Items.Add("Grande");
        iconCombo.SelectedIndex = App.Settings.IconScale switch
        {
            < 0.9 => 0,
            > 1.1 => 2,
            _ => 1
        };

        var iconPreview = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            VerticalAlignment = VerticalAlignment.Center
        };

        void RenderIconPreview()
        {
            iconPreview.Children.Clear();
            var amostra = App.Catalog.Tools.FirstOrDefault(t => !t.IsBroken);
            if (amostra is null) return;

            iconPreview.Children.Add(ToolIcons.Create(amostra, 18, 14));
            iconPreview.Children.Add(new TextBlock
            {
                Text = $"{amostra.Name} — assim fica o ícone nas listas",
                VerticalAlignment = VerticalAlignment.Center,
                FontSize = 12.5
            });
        }
        RenderIconPreview();

        iconCombo.SelectionChanged += (_, _) =>
        {
            App.Settings.IconScale = iconCombo.SelectedIndex switch { 0 => 0.8, 2 => 1.3, _ => 1.0 };
            SettingsService.Save();
            RenderIconPreview();       // preview muda no mesmo gesto
            RebuildNav();              // menu lateral reflete na hora
            HudController.Rebuild();   // a busca rápida é recriada na próxima abertura
            AppPaths.Log($"ícones: escala {App.Settings.IconScale:0.0}");
        };

        var iconRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        iconRow.Children.Add(iconCombo);
        iconRow.Children.Add(iconPreview);
        iconPanel.Children.Add(iconRow);
        iconPanel.Children.Add(Muted("Vale para todas as ferramentas: menu lateral, página inicial, busca rápida e aviso de resultado."));
        AddBlock("Ícones", iconPanel);

        // atualizações — UPD-001 (revisado): UM fluxo, um botão. A notificação automática usa o mesmo
        // UpdateFlow, então Configurações e aviso nunca mostram estados diferentes (decisão (A)).
        var updatePanel = new StackPanel { Spacing = 8 };
        var updateVersion = Muted($"Versão instalada: v{UpdateService.CurrentVersion}");
        var updateStatus = Muted("");

        // "o que mudou": as notas vêm do canal de atualização e o texto completo vai no pacote
        var updateNotesText = new TextBlock
        {
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            LineHeight = 17
        };
        var updateNotes = new ScrollViewer
        {
            Content = updateNotesText,
            MaxHeight = 190,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Visibility = Visibility.Collapsed,
            Padding = new Thickness(12, 8, 12, 8),
            CornerRadius = new CornerRadius(6),
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(22, 255, 255, 255))
        };
        var updateBar = new ProgressBar
        {
            Minimum = 0, Maximum = 100, Value = 0, Width = 320,
            HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed
        };
        var updateRing = new ProgressRing
        {
            IsActive = false,
            Width = 18,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };

        var updateRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var checkNow = new Button
        {
            Content = "Buscar atualizações",
            Style = (Style)Application.Current.Resources["AccentButtonStyle"]
        };
        var whatsNew = new Button { Content = "O que mudou nesta versão" };
        updateRow.Children.Add(checkNow);
        updateRow.Children.Add(updateRing);
        updateRow.Children.Add(whatsNew);

        whatsNew.Click += async (_, _) =>
        {
            var texto = PatchNotes.SectionFor(UpdateService.CurrentVersion)
                        ?? "Não encontrei o PATCHLOG.md ao lado do programa.";
            var corpo = new ScrollViewer
            {
                Content = new TextBlock { Text = texto, TextWrapping = TextWrapping.Wrap, FontSize = 12.5 },
                MaxHeight = 420,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            var dialog = new ContentDialog
            {
                Title = $"O que mudou na v{UpdateService.CurrentVersion}",
                Content = corpo,
                CloseButtonText = "Fechar",
                XamlRoot = (Content as FrameworkElement)?.XamlRoot
            };
            try { await dialog.ShowAsync(); } catch (Exception ex) { AppPaths.Log("whatsNew: " + ex.Message); }
        };

        void RenderUpdate(UpdateState state)
        {
            updateStatus.Text = string.IsNullOrWhiteSpace(state.Channel)
                ? state.Message
                : $"{state.Message} (canal {state.Channel})";

            updateRing.IsActive = state.Busy;
            updateRing.Visibility = state.Busy ? Visibility.Visible : Visibility.Collapsed;

            var baixando = state.Stage == UpdateStage.Downloading;
            updateBar.Visibility = baixando ? Visibility.Visible : Visibility.Collapsed;
            updateBar.IsIndeterminate = false;
            if (baixando) updateBar.Value = Math.Round(state.Fraction * 100);

            checkNow.IsEnabled = !state.Busy;
            checkNow.Content = state.Stage switch
            {
                UpdateStage.Checking => "Verificando…",
                UpdateStage.Downloading => $"Baixando… {state.Fraction:P0}",
                UpdateStage.Ready => "Instalar",
                UpdateStage.Installing => "Instalando…",
                UpdateStage.Failed => "Tentar novamente",
                _ => "Buscar atualizações"
            };

            var notas = UpdateService.LastCheck is { HasUpdate: true } check &&
                        !string.IsNullOrWhiteSpace(check.Notes)
                ? check.Notes!.Trim()
                : null;

            if (notas is not null)
            {
                updateNotesText.Text = $"O que muda na v{UpdateService.LastCheck!.LatestVersion}\n\n{notas}";
                updateNotes.Visibility = Visibility.Visible;
            }
            else
            {
                updateNotes.Visibility = Visibility.Collapsed;
            }
        }

        void StartUpdateFlow() => _ = UpdateFlow.StartAsync();

        // A página é reconstruída a cada navegação: solta o handler da montagem anterior antes de
        // assinar de novo — senão a tela antiga continuaria sendo atualizada (e a nova, não).
        if (_updateFlowHandler is not null) UpdateFlow.Changed -= _updateFlowHandler;
        _updateFlowHandler = RenderUpdate;
        UpdateFlow.Changed += _updateFlowHandler;

        checkNow.Click += (_, _) =>
        {
            // Botão único (UPD-001 revisado): "Instalar" só age com pacote baixado E conferido.
            if (UpdateFlow.Current.CanInstall)
            {
                UpdateFlow.InstallAndRestart(out _);
                RenderUpdate(UpdateFlow.Current);
                return;
            }
            StartUpdateFlow();
        };

        _runUpdateCheck = StartUpdateFlow;

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
        updatePanel.Children.Add(updateNotes);
        updatePanel.Children.Add(autoCheckToggle);
        updatePanel.Children.Add(Muted("Canais: " +
            string.Join(" → ", UpdateService.Channels().Select(c => c.Name)) +
            " (o primeiro que responder vale). Configuráveis em config.local.json: " +
            UpdateService.GitHubRepoKey + " / " + UpdateService.GitLabRepoKey + "."));
        updatePanel.Children.Add(Muted("A troca de versão fecha o app, substitui a pasta do programa e abre de novo. Se a versão nova não abrir, a anterior volta sozinha."));
        AddBlock("Atualizações", updatePanel);
        RenderUpdate(UpdateFlow.SyncFromDisk());

        // SIG-001: assinatura local das ferramentas (satisfaz a política de execução do PowerShell)
        var assinaturaPanel = new StackPanel { Spacing = 8 };
        var assinaturaStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        var assinaturaDetalhe = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 11, Opacity = 0.75 };

        void MostrarAssinatura(AssinaturaEstado? estado)
        {
            var status = estado?.Status ?? "ausente";
            assinaturaStatus.Text = status switch
            {
                "ok" => "ativa",
                "ausente" => "ausente",
                "invalida" => "inválida",
                "politica_bloqueia" => "bloqueada pela política",
                "somente_leitura" => "somente leitura (quem assina é o instalador)",
                _ => status
            } + (estado is null ? "" : $" · {estado.Motivo}");

            var linhas = new List<string>();
            if (estado is not null && estado.Thumbprint.Length > 0)
                linhas.Add($"impressão digital {estado.ThumbprintCurto} · válido até {estado.ValidoAte}");
            if (estado is not null && estado.Politica.Length > 0)
                linhas.Add($"política de execução: {estado.Politica}");
            linhas.Add("Assinatura local satisfaz a política de execução do PowerShell — não é prova de autoria.");
            assinaturaDetalhe.Text = string.Join(Environment.NewLine, linhas);
            assinaturaDetalhe.Visibility = Visibility.Visible;
        }

        MostrarAssinatura(ToolSigning.LerEstado());

        var reassinar = new Button { Content = "Reassinar ferramentas" };
        reassinar.Click += (_, _) =>
        {
            reassinar.IsEnabled = false;
            assinaturaStatus.Text = "assinando os scripts…";
            _ = Task.Run(() =>
            {
                var estado = ToolSigning.Ensure(forcar: true);
                DispatcherQueue.TryEnqueue(() =>
                {
                    MostrarAssinatura(estado);
                    reassinar.IsEnabled = true;
                });
            });
        };

        assinaturaPanel.Children.Add(assinaturaStatus);
        assinaturaPanel.Children.Add(assinaturaDetalhe);
        assinaturaPanel.Children.Add(reassinar);
        AddBlock("Assinatura local", assinaturaPanel);

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
