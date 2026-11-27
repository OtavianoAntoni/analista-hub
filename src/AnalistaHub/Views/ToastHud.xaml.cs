using AnalistaHub.Models;
using AnalistaHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AnalistaHub.Views;

/// <summary>Aviso flutuante de resultado, perto do cursor (o "popup" dos scripts, agora nativo).</summary>
public sealed partial class ToastHud : Window
{
    private const double WidthDip = 460;
    private double _heightDip = 140;

    private DispatcherTimer? _timer;
    private int _secondsLeft;
    private bool _pinned;
    private string _value = "";

    // UPD-001: quando o cartão é o aviso de atualização, os botões são do fluxo (Baixar/Instalar),
    // não os de resultado (Copiar/Manter aberto).
    private bool _updateMode;
    private Action? _updateAction;

    public ToastHud()
    {
        InitializeComponent();
        Title = "Analista Hub — resultado";
        UiKit.MakeHudWindow(this, alwaysOnTop: true, hideFromAltTab: true);
        UiKit.TryMica(this);
        UiKit.ApplyTheme(RootGrid);

        RootGrid.PointerEntered += (_, _) => StopTimer();
        RootGrid.PointerExited += (_, _) => { if (!_pinned) StartTimer(_secondsLeft); };
        RootGrid.KeyDown += OnRootKeyDown;
    }

    public void Present(ToolManifest? tool, string value, string subtitle, string mode, bool ok,
        string context, int seconds, string pingLine = "", bool progress = false)
    {
        _value = value;
        _pinned = false;
        _secondsLeft = Math.Clamp(seconds, 4, 120);

        // CFG-003: "Consultando …" usa o ícone de sincronismo, não o de sucesso nem o de alerta.
        HeaderIcon.Glyph = progress ? "\uE895" : ok ? "\uE73E" : "\uE7BA";
        // MKT-004: ferramenta que dispensa título no card (o próprio valor já diz o que é)
        HeaderText.Text = tool?.Name ?? "Analista Hub";
        HeaderText.Visibility = tool?.Output.HideTitle == true ? Visibility.Collapsed : Visibility.Visible;

        // MKT-004: sufixo do valor (o "1" somado ao SN) em cor de destaque
        var sufixo = tool?.Output.HighlightSuffix;
        ValueText.Inlines.Clear();
        if (!string.IsNullOrEmpty(sufixo) && value.Length > sufixo!.Length &&
            value.EndsWith(sufixo, StringComparison.Ordinal))
        {
            ValueText.Inlines.Add(new Run { Text = value[..^sufixo.Length] });
            ValueText.Inlines.Add(new Run
            {
                Text = sufixo,
                Foreground = new SolidColorBrush(UiKit.Suffix)
            });
        }
        else
        {
            ValueText.Inlines.Add(new Run { Text = value });
        }

        ValueText.FontSize = value.Length > 28 ? 19 : 26;
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        // NET-001: os 2 últimos retornos de ping do endereço consultado
        PingText.Text = string.IsNullOrWhiteSpace(pingLine) ? "" : $"ping · {pingLine}";
        PingText.Visibility = string.IsNullOrWhiteSpace(pingLine) ? Visibility.Collapsed : Visibility.Visible;

        var modeLabel = mode switch
        {
            "type" => "digitado no campo",
            "clipboard" => "copiado para a área de transferência",
            _ => "somente aviso"
        };
        ContextText.Text = string.IsNullOrWhiteSpace(context) ? modeLabel : $"{context} · {modeLabel}";

        // cartão de resultado: garante que os controles do fluxo de atualização saiam de cena
        _updateMode = false;
        _updateAction = null;
        UpdateButton.Visibility = Visibility.Collapsed;
        UpdateRing.IsActive = false;
        UpdateRing.Visibility = Visibility.Collapsed;

        // MKT-004 + decisão 5: "Copiar"/"Manter aberto" só quando há valor de consulta para copiar.
        // "SEM SN" (modo somente aviso) e cartões de erro não oferecem cópia.
        var copiavel = tool is null || mode is "clipboard" or "type";
        CopyButton.Visibility = copiavel ? Visibility.Visible : Visibility.Collapsed;
        PinButton.Visibility = copiavel ? Visibility.Visible : Visibility.Collapsed;

        AppWindow.Show();
        Activate();
        ResizeToContent();
        UiKit.PlaceNearCursor(this, WidthDip, _heightDip);

        StartTimer(_secondsLeft);
    }

    /// <summary>
    /// Aviso de nova versão com ação (UPD-001): o botão é o MESMO fluxo de Configurações →
    /// Atualizações (<see cref="Services.UpdateFlow"/>). Fica fixo até o usuário decidir.
    /// </summary>
    public void PresentUpdate(string version, string subtitle, Action onClick)
    {
        _value = "";
        _pinned = true;
        _updateMode = true;
        _updateAction = onClick;
        HeaderIcon.Glyph = "\uE895";
        HeaderText.Text = "Atualização";
        ValueText.Text = $"Analista Hub v{version}";
        ValueText.FontSize = 22;
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        PingText.Visibility = Visibility.Collapsed;
        ContextText.Text = "Configurações → Atualizações para ver os detalhes";
        TimerText.Text = "fixo — feche quando quiser";

        CopyButton.Visibility = Visibility.Collapsed;
        PinButton.Visibility = Visibility.Collapsed;
        UpdateButton.Content = "Baixar";
        UpdateButton.IsEnabled = true;
        UpdateButton.Visibility = Visibility.Visible;
        UpdateRing.IsActive = false;
        UpdateRing.Visibility = Visibility.Collapsed;

        StopTimer();
        AppWindow.Show();
        Activate();
        ResizeToContent();
        UiKit.PlaceNearCursor(this, WidthDip, _heightDip);
    }

    /// <summary>Reflete o estado do fluxo no cartão de atualização (rótulo, spinner, legenda).</summary>
    public void SetUpdateState(bool busy, string buttonLabel, string? subtitle)
    {
        if (!_updateMode) return;
        UpdateRing.IsActive = busy;
        UpdateRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        UpdateButton.IsEnabled = !busy;
        UpdateButton.Content = buttonLabel;
        if (subtitle is not null)
        {
            SubtitleText.Text = subtitle;
            SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void OnUpdateClick(object sender, RoutedEventArgs e) => _updateAction?.Invoke();

    private void ResizeToContent()
    {
        RootGrid.Measure(new Size(WidthDip, double.PositiveInfinity));
        var desired = RootGrid.DesiredSize.Height;
        if (double.IsNaN(desired) || desired < 60) desired = 160;
        _heightDip = Math.Min(desired + 4, 420);

        var scale = UiKit.Scale(this);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(
            AppWindow.Position.X, AppWindow.Position.Y,
            (int)Math.Round(WidthDip * scale), (int)Math.Round(_heightDip * scale)));
        UiKit.TrimFrame(this);   // UI-005: recorta a moldura clara em volta do aviso
    }

    private void StartTimer(int seconds)
    {
        StopTimer();
        _secondsLeft = Math.Max(1, seconds);
        TimerText.Text = $"fecha em {_secondsLeft}s · clique para fechar";

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) =>
        {
            _secondsLeft--;
            if (_secondsLeft <= 0)
            {
                StopTimer();
                AppWindow.Hide();
                return;
            }
            TimerText.Text = $"fecha em {_secondsLeft}s · clique para fechar";
        };
        _timer.Start();
    }

    /// <summary>Reaplica o tema na janela de aviso.</summary>
    public void ApplyThemeNow() => UiKit.ApplyTheme(RootGrid);

    private void StopTimer()
    {
        _timer?.Stop();
        _timer = null;
    }

    private void OnPinClick(object sender, RoutedEventArgs e)
    {
        _pinned = true;
        StopTimer();
        TimerText.Text = "fixo — feche quando quiser";
        PinButton.Visibility = Visibility.Collapsed;
    }

    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        ClipboardService.SetText(_value);
        HeaderText.Text = "copiado";
        StartTimer(3);
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => AppWindow.Hide();

    private void OnRootKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape) AppWindow.Hide();
    }
}

public static class ToastController
{
    private static ToastHud? _toast;

    /// <summary>O aviso é transitório: descartar é mais seguro do que repintar texto já criado.</summary>
    public static void Rebuild()
    {
        try { _toast?.Close(); } catch { }
        _toast = null;
    }

    public static void ShowSuccess(ToolManifest tool, string value, string subtitle, string mode,
        string pingLine = "")
    {
        var context = string.Join(" · ", SettingsService.Current.LastInput.TryGetValue(tool.Id, out var last) ? last : "");
        _toast ??= new ToastHud();
        _toast.Present(tool, value, subtitle, mode, true, context: "", seconds: SettingsService.Current.ToastSeconds,
            pingLine: pingLine);
    }

    public static void ShowMessage(string title, string value, string subtitle)
    {
        _toast ??= new ToastHud();
        _toast.Present(null, value, subtitle, "notify", true, context: "", seconds: SettingsService.Current.ToastSeconds);
    }

    /// <summary>
    /// Aviso de versão nova com ação (UPD-001): o botão dispara o MESMO <see cref="UpdateFlow"/> da
    /// tela de Configurações — notificação e tela nunca mostram estados diferentes.
    /// </summary>
    public static void ShowUpdate(string version, Action onClick)
    {
        _toast ??= new ToastHud();
        _toast.PresentUpdate(version, "Uma versão nova está disponível.", onClick);
    }

    /// <summary>Espelha o estado do fluxo no cartão de atualização (rótulo, spinner, legenda).</summary>
    public static void ReflectUpdate(UpdateState state)
    {
        if (_toast is null) return;
        var label = state.Stage switch
        {
            UpdateStage.Checking => "Verificando…",
            UpdateStage.Downloading => $"Baixando… {state.Fraction:P0}",
            UpdateStage.Ready => "Instalar e reiniciar",
            UpdateStage.Installing => "Instalando…",
            UpdateStage.Failed => "Tentar novamente",
            _ => "Baixar"
        };
        _toast.SetUpdateState(state.Busy, label, state.Message);
    }

    /// <summary>
    /// Falha: o mesmo cartão do resultado, com ícone de alerta e o nome da ferramenta no topo.
    /// Antes a falha ia por ShowMessage, que desenha ícone verde de "deu certo" e transforma o texto
    /// em valor — era o aviso "Não foi possível" com cara de sucesso (NET-004).
    /// </summary>
    public static void ShowFailure(ToolManifest? tool, string headline, string detail)
    {
        _toast ??= new ToastHud();
        _toast.Present(tool, headline, detail, "notify", false, context: "",
            seconds: SettingsService.Current.ToastSeconds);
    }

    /// <summary>
    /// "Consultando &lt;valor&gt;…" antes do resultado (CFG-003): o analista vê o que foi usado.
    /// O cartão é substituído pelo resultado assim que a consulta termina.
    /// </summary>
    public static void ShowProgress(ToolManifest tool, string headline, string subtitle)
    {
        _toast ??= new ToastHud();
        _toast.Present(tool, headline, subtitle, "notify", true, context: "",
            seconds: SettingsService.Current.ToastSeconds, progress: true);
    }
}

public static class HudController
{
    private static CommandHud? _hud;

    /// <summary>
    /// A HUD é recriada na próxima abertura: os textos e dicas montados em código guardam a cor do
    /// tema em que nasceram, então reaproveitar a janela deixaria metade com a cor antiga.
    /// </summary>
    public static void Rebuild()
    {
        try { _hud?.HideHud(); } catch { }
        _hud = null;
    }

    private static CommandHud Ensure()
    {
        _hud ??= new CommandHud();
        return _hud;
    }

    public static void Toggle()
    {
        try
        {
            AppPaths.Log("HudController.Toggle: entrada");
            var hud = Ensure();
            AppPaths.Log($"HudController.Toggle: visivel={hud.IsHudVisible}");
            if (hud.IsHudVisible) hud.HideHud();
            else hud.ShowHud(null);
            AppPaths.Log("HudController.Toggle: saiu");
        }
        catch (Exception ex) { AppPaths.Log("HudController.Toggle: " + ex); }
    }

    public static void Show(string? toolId = null)
    {
        try { Ensure().ShowHud(toolId); }
        catch (Exception ex) { AppPaths.Log("HudController.Show: " + ex.Message); }
    }

    public static void ShowTool(string toolId)
    {
        try { Ensure().ShowHud(toolId); }
        catch (Exception ex) { AppPaths.Log("HudController.ShowTool: " + ex.Message); }
    }

    /// <summary>Diagnóstico: preenche o primeiro campo da HUD já aberta (ver `--fill`).</summary>
    public static void FillFirstField(string value)
    {
        try { _hud?.FillFirstField(value); }
        catch (Exception ex) { AppPaths.Log("HudController.FillFirstField: " + ex.Message); }
    }

    /// <summary>Diagnóstico: executa a ferramenta aberta como o Enter faria (ver `--submit`).</summary>
    public static void Submit()
    {
        try { _ = _hud?.SubmitActiveAsync(); }
        catch (Exception ex) { AppPaths.Log("HudController.Submit: " + ex.Message); }
    }

    /// <summary>Diagnóstico: cicla até a ferramenta pedida pelas mesmas setas da HUD (ver `--cycle`).</summary>
    public static void CycleTo(string toolId)
    {
        try { _hud?.CycleTo(toolId); }
        catch (Exception ex) { AppPaths.Log("HudController.CycleTo: " + ex.Message); }
    }

    public static void Invalidate()
    {
        // o catálogo é lido a cada busca; a HUD não precisa ser recriada
    }

    public static void HideAll()
    {
        try { _hud?.HideHud(); } catch { }
    }
}
