using AnalistaHub.Models;
using AnalistaHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
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

    public ToastHud()
    {
        InitializeComponent();
        Title = "Analista Hub — resultado";
        UiKit.MakeHudWindow(this, alwaysOnTop: true, hideFromAltTab: true);
        UiKit.TryMica(this);
        RootGrid.RequestedTheme = ElementTheme.Dark;

        RootGrid.PointerEntered += (_, _) => StopTimer();
        RootGrid.PointerExited += (_, _) => { if (!_pinned) StartTimer(_secondsLeft); };
        RootGrid.KeyDown += OnRootKeyDown;
    }

    public void Present(ToolManifest? tool, string value, string subtitle, string mode, bool ok,
        string context, int seconds)
    {
        _value = value;
        _pinned = false;
        _secondsLeft = Math.Clamp(seconds, 4, 120);

        HeaderIcon.Glyph = ok ? "\uE73E" : "\uE7BA";
        HeaderText.Text = tool?.Name ?? "Analista Hub";
        ValueText.Text = value;
        ValueText.FontSize = value.Length > 28 ? 19 : 26;
        SubtitleText.Text = subtitle;
        SubtitleText.Visibility = string.IsNullOrWhiteSpace(subtitle) ? Visibility.Collapsed : Visibility.Visible;

        var modeLabel = mode switch
        {
            "type" => "digitado no campo",
            "clipboard" => "copiado para a área de transferência",
            _ => "somente aviso"
        };
        ContextText.Text = string.IsNullOrWhiteSpace(context) ? modeLabel : $"{context} · {modeLabel}";
        PinButton.Visibility = Visibility.Visible;

        AppWindow.Show();
        Activate();
        ResizeToContent();
        UiKit.PlaceNearCursor(this, WidthDip, _heightDip);

        StartTimer(_secondsLeft);
    }

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

    public static void ShowSuccess(ToolManifest tool, string value, string subtitle, string mode)
    {
        var context = string.Join(" · ", SettingsService.Current.LastInput.TryGetValue(tool.Id, out var last) ? last : "");
        _toast ??= new ToastHud();
        _toast.Present(tool, value, subtitle, mode, true, context: "", seconds: SettingsService.Current.ToastSeconds);
    }

    public static void ShowMessage(string title, string value, string subtitle)
    {
        _toast ??= new ToastHud();
        _toast.Present(null, value, subtitle, "notify", true, context: "", seconds: SettingsService.Current.ToastSeconds);
    }
}

public static class HudController
{
    private static CommandHud? _hud;

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

    public static void Show()
    {
        try { Ensure().ShowHud(null); }
        catch (Exception ex) { AppPaths.Log("HudController.Show: " + ex.Message); }
    }

    public static void ShowTool(string toolId)
    {
        try { Ensure().ShowHud(toolId); }
        catch (Exception ex) { AppPaths.Log("HudController.ShowTool: " + ex.Message); }
    }

    public static void Invalidate()
    {
        // o catálogo é lido a cada busca; a HUD não precisa ser recriada
    }
}
