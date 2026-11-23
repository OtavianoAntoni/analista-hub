using System.Collections.ObjectModel;
using AnalistaHub.Models;
using AnalistaHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;

namespace AnalistaHub.Views;

public sealed class ToolRow
{
    public ToolRow(ToolManifest tool)
    {
        Tool = tool;
        Icon = tool.IconGlyph;
        Name = tool.Name;
        Description = tool.Description;
        Trigger = SettingsService.TriggerFor(tool);
    }

    public ToolManifest Tool { get; }
    public string Icon { get; }
    public string Name { get; }
    public string Description { get; }
    public string Trigger { get; }
}

public sealed partial class CommandHud : Window
{
    private const double WidthDip = 672;
    private const double HeightDip = 396;

    private readonly ObservableCollection<ToolRow> _rows = new();
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Dictionary<string, TextBlock> _hints = new();

    private ToolManifest? _active;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _suppressHide;
    private bool _closing;
    private DateTime _shownAt = DateTime.MinValue;

    public CommandHud()
    {
        InitializeComponent();
        Title = "Analista Hub";
        UiKit.MakeHudWindow(this);
        UiKit.TryMica(this);
        RootGrid.RequestedTheme = ElementTheme.Dark;

        ResultsList.ItemsSource = _rows;
        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.ItemClick += (_, e) => OnRowInvoked(e.ClickedItem as ToolRow);
        Activated += OnActivated;
        Closed += (_, _) => _closing = true;
    }

    // ------------------------------------------------------------------ ciclo de vida
    public void ShowHud(string? toolId)
    {
        try
        {
            _suppressHide = true;
            ResetState();
            UiKit.PlaceHud(this, WidthDip, HeightDip);
            AppWindow.Show();
            Activate();
            UiKit.ForceForeground(this);
            _shownAt = DateTime.UtcNow;

            if (!string.IsNullOrWhiteSpace(toolId))
            {
                var tool = App.Catalog.Find(toolId);
                if (tool is not null)
                {
                    SearchBox.Text = tool.Name;
                    var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Tool, tool));
                    if (row is not null) ResultsList.SelectedItem = row;
                }
            }

            SearchBox.Focus(FocusState.Programmatic);
            _suppressHide = false;
            ApplySize();

            AppPaths.Log($"ShowHud: visivel={AppWindow.IsVisible} pos={AppWindow.Position.X},{AppWindow.Position.Y} " +
                         $"size={AppWindow.Size.Width}x{AppWindow.Size.Height} fg={Native.GetForegroundWindow()}");
        }
        catch (Exception ex)
        {
            AppPaths.Log("ShowHud FALHOU: " + ex);
        }
    }

    public void HideHud()
    {
        try
        {
            _cts?.Cancel();
            AppWindow.Hide();
        }
        catch (Exception ex) { AppPaths.Log("HideHud: " + ex.Message); }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_suppressHide) return;
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 600) return;
            HideHud();
            return;
        }

        // Só dá para mover o foco do XAML depois que a janela está ativa:
        // fazer isso dentro de ShowHud() não pega, e o que o usuário digita se perde.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try
            {
                if (FieldsPanel.Visibility == Visibility.Visible && _boxes.Count > 0)
                    _boxes.Values.First().Focus(FocusState.Programmatic);
                else
                    SearchBox.Focus(FocusState.Programmatic);
                AppPaths.Log("foco aplicado: " + args.WindowActivationState);
            }
            catch (Exception ex) { AppPaths.Log("foco falhou: " + ex.Message); }
        });
    }

    private void ResetState()
    {
        _cts?.Cancel();
        _cts = null;
        _busy = false;
        _active = null;
        _boxes.Clear();
        _hints.Clear();
        FieldsPanel.Children.Clear();
        FieldsPanel.Visibility = Visibility.Collapsed;
        ErrorBanner.Visibility = Visibility.Collapsed;
        ErrorText.Text = "";
        BusyRing.IsActive = false;
        StatusText.Text = "";
        SearchBox.Text = "";
        RefreshResults("");
    }

    // ------------------------------------------------------------------ busca
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_busy) return;
        if (_active is not null)
        {
            // voltou a digitar: sai do modo campos
            _active = null;
            _boxes.Clear();
            _hints.Clear();
            FieldsPanel.Children.Clear();
            FieldsPanel.Visibility = Visibility.Collapsed;
            ErrorBanner.Visibility = Visibility.Collapsed;
        }
        RefreshResults(SearchBox.Text);
    }

    private void RefreshResults(string query)
    {
        var previousId = (_rows.Count > 0 && ResultsList.SelectedItem is ToolRow prev) ? prev.Tool.Id : null;
        var matches = App.Catalog.Search(query).ToList();

        _rows.Clear();
        foreach (var tool in matches) _rows.Add(new ToolRow(tool));

        if (_rows.Count == 0)
        {
            ResultsList.Visibility = matches.Count == 0 && !string.IsNullOrWhiteSpace(query)
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusText.Text = string.IsNullOrWhiteSpace(query)
                ? "Nenhuma ferramenta instalada."
                : $"Nenhuma ferramenta encontrada para «{query}».";
            ApplySize();
            return;
        }

        ResultsList.Visibility = Visibility.Visible;
        var keep = previousId is null ? null : _rows.FirstOrDefault(r => r.Tool.Id == previousId);
        ResultsList.SelectedItem = keep ?? _rows[0];
        if (string.IsNullOrWhiteSpace(StatusText.Text) || !StatusText.Text.StartsWith("No", StringComparison.Ordinal))
            StatusText.Text = $"{_rows.Count} ferramenta(s).";
        ApplySize();
    }

    /// <summary>Altura acompanha o conteúdo: 2 ferramentas = janela pequena.</summary>
    private void ApplySize()
    {
        try
        {
            double height = 66 + 34; // busca + rodapé
            if (ResultsList.Visibility == Visibility.Visible)
                height += Math.Clamp(Math.Max(_rows.Count, 1), 1, 8) * 56;
            if (FieldsPanel.Visibility == Visibility.Visible)
                height += _boxes.Count * 82 + _hints.Count * 18 + 10;
            if (ErrorBanner.Visibility == Visibility.Visible)
                height += 108;
            height = Math.Clamp(height, 190, 620);

            var scale = UiKit.Scale(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(WidthDip * scale), (int)Math.Round(height * scale)));
        }
        catch (Exception ex) { AppPaths.Log("ApplySize: " + ex.Message); }
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                MoveSelection(1);
                e.Handled = true;
                break;
            case VirtualKey.Up:
                MoveSelection(-1);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                _ = AdvanceAsync();
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                if (_busy) { _cts?.Cancel(); StatusText.Text = "Cancelando…"; }
                else HideHud();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0) return;
        var index = ResultsList.SelectedIndex;
        index = Math.Clamp(index + delta, 0, _rows.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(_rows[index]);
    }

    private void OnRowInvoked(ToolRow? row)
    {
        if (row is null || _busy) return;
        _ = InvokeToolAsync(row.Tool);
    }

    // ------------------------------------------------------------------ fluxo
    private async Task AdvanceAsync()
    {
        if (_busy) return;

        if (_active is not null && FieldsPanel.Visibility == Visibility.Visible)
        {
            await ExecuteAsync(_active);
            return;
        }

        var tool = (ResultsList.SelectedItem as ToolRow)?.Tool;
        if (tool is null) return;
        await InvokeToolAsync(tool);
    }

    private async Task InvokeToolAsync(ToolManifest tool)
    {
        if (tool.Inputs.Count == 0)
        {
            await ExecuteAsync(tool);
            return;
        }
        ShowFields(tool);
    }

    private void ShowFields(ToolManifest tool)
    {
        _active = tool;
        _boxes.Clear();
        _hints.Clear();
        FieldsPanel.Children.Clear();
        ErrorBanner.Visibility = Visibility.Collapsed;

        foreach (var field in tool.Inputs)
        {
            var box = new TextBox
            {
                Header = field.Label,
                PlaceholderText = field.Placeholder,
                FontSize = 14
            };
            box.KeyDown += OnFieldKeyDown;
            box.TextChanged += (_, _) => UpdateHint(field);
            FieldsPanel.Children.Add(box);
            _boxes[field.Key] = box;

            if (field.EmptyUsesClipboard)
            {
                var hint = new TextBlock
                {
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255)),
                    Margin = new Thickness(2, -4, 0, 2)
                };
                FieldsPanel.Children.Add(hint);
                _hints[field.Key] = hint;
                UpdateHint(field);
            }
        }

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Margin = new Thickness(0, 4, 0, 0)
        };
        var consult = new Button
        {
            Content = "Consultar",
            Padding = new Thickness(18, 6, 18, 6)
        };
        if (Application.Current.Resources.TryGetValue("AccentButtonStyle", out var accent) && accent is Style accentStyle)
            consult.Style = accentStyle;
        consult.Click += async (_, _) =>
        {
            if (_active is not null) await ExecuteAsync(_active);
        };
        actions.Children.Add(consult);
        actions.Children.Add(new TextBlock
        {
            Text = "Enter também consulta",
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 255, 255, 255))
        });
        FieldsPanel.Children.Add(actions);

        FieldsPanel.Visibility = Visibility.Visible;
        StatusText.Text = tool.Description;
        ApplySize();
        if (_boxes.Count > 0)
        {
            var first = _boxes.Values.First();
            first.Focus(FocusState.Programmatic);
        }
    }

    private void UpdateHint(ToolInput field)
    {
        if (!_hints.TryGetValue(field.Key, out var hint)) return;
        var typed = _boxes.TryGetValue(field.Key, out var box) ? box.Text.Trim() : "";
        if (typed.Length > 0)
        {
            hint.Text = "Enter para consultar.";
            return;
        }
        var clip = ClipboardService.GetText();
        hint.Text = string.IsNullOrWhiteSpace(clip)
            ? "vazio = área de transferência · clipboard está vazio"
            : $"vazio = área de transferência · agora: {Truncate(clip, 60)}";
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (_active is not null) _ = ExecuteAsync(_active);
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_busy) { _cts?.Cancel(); StatusText.Text = "Cancelando…"; }
            else HideHud();
        }
    }

    // ------------------------------------------------------------------ execução
    private async Task ExecuteAsync(ToolManifest tool)
    {
        if (_busy) return;

        var inputs = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string? validationError = null;
        var clipboard = ClipboardService.GetText();

        foreach (var field in tool.Inputs)
        {
            var value = _boxes.TryGetValue(field.Key, out var box) ? box.Text.Trim() : "";
            if (value.Length == 0 && field.EmptyUsesClipboard)
            {
                if (string.IsNullOrWhiteSpace(clipboard))
                {
                    validationError ??= $"Área de transferência vazia:\ncole o valor de «{field.Label}» e tente de novo.";
                    continue;
                }
                value = clipboard;
            }
            if (value.Length == 0)
            {
                if (field.Required) validationError ??= $"Preencha «{field.Label}».";
                continue;
            }

            var problem = ValidateField(field, value);
            if (problem is not null) { validationError ??= problem; continue; }
            inputs[field.Key] = value;
        }

        if (validationError is not null)
        {
            ShowError(validationError, tool, canRetry: false);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        ErrorBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Consultando «{tool.Name}»…";

        var result = await ToolRunner.RunAsync(tool, inputs, _cts.Token);
        SetBusy(false);

        var inputLabel = string.Join(" · ", inputs.Values);
        HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, inputLabel,
            result.Ok, result.Elapsed.TotalSeconds, result.Ok ? result.Value : result.Detail));

        if (result.Ok) await PresentSuccessAsync(tool, result, inputLabel);
        else await PresentFailureAsync(tool, result);
    }

    private static string? ValidateField(ToolInput field, string value)
    {
        switch (field.Type)
        {
            case "ip":
            {
                var text = value;
                var slash = text.IndexOf('/');
                if (slash > 0) text = text[..slash];
                var parts = text.Split('.');
                if (parts.Length != 4) return $"«{field.Label}»: esperado um IPv4, ex.: 10.0.0.1";
                foreach (var part in parts)
                {
                    if (!byte.TryParse(part, out var octet)) return $"«{field.Label}»: octeto inválido em «{value}».";
                    _ = octet;
                }
                return null;
            }
            case "mac":
            {
                var hex = new string(value.Where(Uri.IsHexDigit).ToArray());
                return hex.Length >= 6 ? null : $"«{field.Label}»: preciso de pelo menos 6 dígitos hex (ex.: 00:1A:2B).";
            }
            default:
                return null;
        }
    }

    private async Task PresentSuccessAsync(ToolManifest tool, RunResult result, string inputLabel)
    {
        var mode = SettingsService.OutputModeFor(tool.Id, tool.Output.Default);
        var value = result.Value;

        if (mode is not "notify")
        {
            if (!ClipboardService.SetText(value))
                AppPaths.Log("clipboard: SetText falhou");
        }

        var subtitle = value.Trim() == "0" && !string.IsNullOrWhiteSpace(tool.Output.SuccessLabel)
            ? tool.Output.SuccessLabel
            : tool.Output.ValueLabel;

        HideHud();

        if (mode == "type")
        {
            await Task.Delay(200);
            var typed = TypingService.TypeText(value);
            if (!typed) subtitle = "não foi possível digitar — o valor está na área de transferência";
        }

        ToastController.ShowSuccess(tool, value, subtitle, mode);
    }

    private async Task PresentFailureAsync(ToolManifest tool, RunResult result)
    {
        var detail = result.Detail;

        if (tool.Network is not null && !result.TimedOut)
        {
            var reachable = await Probes.CheckTcpAsync(tool.Network.Host, tool.Network.Port, 2500);
            if (!reachable)
            {
                ShowError("Sem conexão com o servidor. Verifique a VPN e tente novamente.", tool, canRetry: true, showProbe: true);
                return;
            }
        }

        ShowError(string.IsNullOrWhiteSpace(detail) ? "A ferramenta não devolveu resultado." : detail,
            tool, canRetry: true);
    }

    private void ShowError(string message, ToolManifest? tool, bool canRetry, bool showProbe = false)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
        RetryButton.Visibility = canRetry ? Visibility.Visible : Visibility.Collapsed;
        ProbeButton.Visibility = showProbe || tool?.Network is not null ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = "";
        ApplySize();
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        BusyRing.IsActive = busy;
        SearchBox.IsEnabled = !busy;
        foreach (var box in _boxes.Values) box.IsEnabled = !busy;
        ResultsList.IsEnabled = !busy;
    }

    // ------------------------------------------------------------------ botões
    private async void OnRetryClick(object sender, RoutedEventArgs e)
    {
        if (_active is not null) await ExecuteAsync(_active);
        else await AdvanceAsync();
    }

    private async void OnProbeClick(object sender, RoutedEventArgs e)
    {
        var tool = _active ?? (ResultsList.SelectedItem as ToolRow)?.Tool;
        if (tool?.Network is null) return;
        ProbeButton.IsEnabled = false;
        StatusText.Text = $"Testando {tool.Network.Label} ({tool.Network.Host}:{tool.Network.Port})…";
        var ok = await Probes.CheckTcpAsync(tool.Network.Host, tool.Network.Port, 3000);
        ProbeButton.IsEnabled = true;
        StatusText.Text = ok
            ? $"Servidor respondeu ({tool.Network.Host}:{tool.Network.Port}). Se a consulta falhou, o problema é credencial ou resposta do serviço."
            : $"Sem resposta de {tool.Network.Host}:{tool.Network.Port}. Verifique a VPN.";
    }

    private void OnCopyDetailClick(object sender, RoutedEventArgs e)
    {
        ClipboardService.SetText(ErrorText.Text);
        StatusText.Text = "Detalhe copiado.";
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    public bool IsHudVisible
    {
        get
        {
            try { return !_closing && AppWindow.IsVisible; }
            catch { return false; }
        }
    }
}
