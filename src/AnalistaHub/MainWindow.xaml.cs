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
    public string? ToolId { get; init; }
    public string Kind { get; init; } = "tool";
}

public sealed partial class MainWindow : Window
{
    private static readonly Dictionary<string, (bool ok, DateTime when)> ProbeCache = new();
    private Action<bool>? _armHotkeyCapture;

    public MainWindow()
    {
        InitializeComponent();
        Title = "Analista Hub";
        AppWindow.Resize(new SizeInt32(1120, 760));
        CenterOnScreen();
        UiKit.ApplyDarkChrome(this, round: false);

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

    private void CenterOnScreen()
    {
        var (left, top, right, bottom) = UiKit.WorkAreaAtCursor();
        var size = AppWindow.Size;
        AppWindow.Move(new PointInt32(
            left + Math.Max(0, (right - left - size.Width) / 2),
            top + Math.Max(0, (bottom - top - size.Height) / 2)));
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
            new() { Title = "Início", Glyph = "\uE80F", Kind = "home" }
        };
        items.AddRange(App.Catalog.Tools.Select(t => new NavItem
        {
            Title = t.IsBroken ? $"{t.Name} (com problema)" : t.Name,
            Glyph = t.IconGlyph,
            ToolId = t.Id,
            Kind = "tool"
        }));
        items.Add(new NavItem { Title = "Configurações", Glyph = "\uE713", Kind = "settings" });

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
    }

    /// <summary>
    /// Abre Configurações e arma a captura do atalho (mesmo caminho do botão "Definir atalho").
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

    private void OnOpenHudClick(object sender, RoutedEventArgs e) => HudController.Show();

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
            titleRow.Children.Add(new FontIcon { Glyph = tool.IconGlyph, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
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

            if (!tool.IsBroken && tool.Network is not null)
                _ = UpdateStatusAsync(tool, status);
        }
    }

    private async Task UpdateStatusAsync(ToolManifest tool, TextBlock target)
    {
        var ok = await Probes.CheckTcpAsync(tool.Network!.Host, tool.Network.Port, 2500);
        ProbeCache[tool.Id] = (ok, DateTime.Now);
        target.Text = ok ? "●" : "●";
        target.Foreground = new SolidColorBrush(ok
            ? Windows.UI.Color.FromArgb(255, 108, 214, 138)
            : Windows.UI.Color.FromArgb(255, 232, 122, 122));
        ToolTipService.SetToolTip(target, ok
            ? $"{tool.Network.Label} respondeu"
            : $"Sem resposta de {tool.Network.Host}:{tool.Network.Port} — verifique a VPN");
    }

    private async Task RunFromDashboardAsync(ToolManifest tool)
    {
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
        var openHud = new Button { Content = "Abrir no comando", Style = (Style)Application.Current.Resources["AccentButtonStyle"] };
        openHud.Click += (_, _) => HudController.ShowTool(tool.Id);
        actions.Children.Add(openHud);
        var testNow = new Button { Content = "Testar agora (usa o clipboard)" };
        testNow.Click += async (_, _) => await RunFromDashboardAsync(tool);
        actions.Children.Add(testNow);
        how.Children.Add(actions);
        AddBlock("Como executar", how);

        // 3 — configurações e credenciais
        var requirements = new StackPanel { Spacing = 12 };

        // 3a — gatilho configurável
        var triggerRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var triggerBox = new TextBox
        {
            Header = "Gatilho desta ferramenta",
            Text = SettingsService.TriggerFor(tool),
            PlaceholderText = ":mkt",
            Width = 200
        };
        var triggerStatus = Muted("");
        var saveTrigger = new Button { Content = "Salvar" };
        var commitTrigger = () =>
        {
            SettingsService.SetTrigger(tool, triggerBox.Text);
            triggerBox.Text = SettingsService.TriggerFor(tool);
            triggerStatus.Text = $"gatilho salvo: {SettingsService.TriggerFor(tool)}";
        };
        saveTrigger.Click += (_, _) => commitTrigger();
        triggerBox.LostFocus += (_, _) => commitTrigger();
        triggerRow.Children.Add(triggerBox);
        triggerRow.Children.Add(saveTrigger);
        requirements.Children.Add(triggerRow);
        requirements.Children.Add(triggerStatus);
        requirements.Children.Add(Muted("Aparece na busca do HUD e nos cartões. O texto é salvo sozinho ao sair do campo."));

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

        // 3c — credenciais digitadas aqui, guardadas cifradas
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

            var credBoxes = new List<(CredentialField field, Func<string> read, Action clear)>();
            var credPanel = new StackPanel { Spacing = 8 };
            var credStatus = Muted("");

            void SaveCredentials(bool announce)
            {
                foreach (var (field, read, _) in credBoxes)
                    CredentialStore.Set(tool.Id, field.Key, read());

                var state = Probes.CheckCredentials(tool);
                credStatus.Text = state.Complete
                    ? "credenciais salvas e cifradas neste computador"
                    : "faltando: " + string.Join(", ", state.Missing);
                if (announce) credStatus.Text += " · salvo agora";
            }

            foreach (var field in tool.Credentials.Fields)
            {
                var currentValue = CredentialStore.Get(tool.Id, field.Key);
                if (field.Secret)
                {
                    var box = new PasswordBox
                    {
                        Header = field.Label,
                        PlaceholderText = field.Placeholder,
                        Password = currentValue,
                        PasswordRevealMode = PasswordRevealMode.Peek,
                        Width = 320
                    };
                    box.LostFocus += (_, _) => SaveCredentials(false);
                    credBoxes.Add((field, () => box.Password, () => box.Password = ""));
                    credPanel.Children.Add(box);
                }
                else
                {
                    var box = new TextBox
                    {
                        Header = field.Label,
                        PlaceholderText = field.Placeholder,
                        Text = currentValue,
                        Width = 320
                    };
                    box.LostFocus += (_, _) => SaveCredentials(false);
                    credBoxes.Add((field, () => box.Text, () => box.Text = ""));
                    credPanel.Children.Add(box);
                }
            }

            var credActions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
            var saveCred = new Button { Content = "Salvar credenciais" };
            saveCred.Click += (_, _) => SaveCredentials(true);
            var clearCred = new Button { Content = "Apagar" };
            clearCred.Click += (_, _) =>
            {
                CredentialStore.Clear(tool.Id);
                foreach (var (_, _, clear) in credBoxes) clear();
                credStatus.Text = "credenciais apagadas deste computador";
            };
            credActions.Children.Add(saveCred);
            credActions.Children.Add(clearCred);
            credPanel.Children.Add(credActions);
            credPanel.Children.Add(credStatus);
            credPanel.Children.Add(Muted("Guardadas com a criptografia do seu usuário do Windows (DPAPI), no cofre do próprio HUB. Nada é enviado para fora desta máquina."));

            var initialState = Probes.CheckCredentials(tool);
            credStatus.Text = initialState.Complete
                ? "credenciais salvas e cifradas neste computador"
                : "faltando: " + string.Join(", ", initialState.Missing);

            requirements.Children.Add(credPanel);
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
                var ok = await Probes.CheckTcpAsync(tool.Network!.Host, tool.Network.Port, 3000);
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
