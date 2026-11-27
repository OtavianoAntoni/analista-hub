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
        IconVisual = ToolIcons.Create(tool, 22, 16);
        Name = tool.Name;
        Description = tool.Description;
    }

    public ToolManifest Tool { get; }
    /// <summary>Glifo ou imagem: o que o manifesto declarar em "icon".</summary>
    public FrameworkElement IconVisual { get; }
    public string Name { get; }
    public string Description { get; }
}

public sealed partial class CommandHud : Window
{
    private const double WidthDip = 672;
    private const double HeightDip = 396;

    private readonly ObservableCollection<ToolRow> _rows = new();
    private readonly Dictionary<string, TextBox> _boxes = new();
    private readonly Dictionary<string, TextBlock> _hints = new();

    // NET-001 — ping do destino: uma sessão por invocação (trava de 100 disparos)
    private readonly List<PingCoordinator> _pings = new();
    private PingSession? _pingSession;
    private DispatcherTimer? _fillTimer;   // diagnóstico --fill com vários passos

    private ToolManifest? _active;
    private CancellationTokenSource? _cts;
    private bool _busy;
    private bool _suppressHide;
    private bool _closing;
    private DateTime _shownAt = DateTime.MinValue;

    /// <summary>
    /// Último texto que o PRÓPRIO código escreveu em SearchBox. Existe porque o TextChanged pode ser
    /// entregue depois da escrita (UX-002): sem essa marca, o eco da abertura era lido como digitação
    /// do usuário e fechava o painel de campos que ShowFields() tinha acabado de abrir.
    /// </summary>
    private string? _searchTextFromCode;

    public CommandHud()
    {
        InitializeComponent();
        Title = "Analista Hub";
        UiKit.MakeHudWindow(this);
        UiKit.TryMica(this);
        // UI-005: recorta a moldura clara em volta da janela (ver UiKit.TrimFrame)
        UiKit.TrimFrame(this);
        UiKit.ApplyTheme(RootGrid);

        ResultsList.ItemsSource = _rows;
        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.ItemClick += (_, e) => OnRowInvoked(e.ClickedItem as ToolRow);
        Activated += OnActivated;
        Closed += (_, _) => _closing = true;
    }

    // ------------------------------------------------------------------ ciclo de vida
    /// <summary>Reaplica o tema (preferência mudou com a HUD já aberta).</summary>
    public void ApplyThemeNow() => UiKit.ApplyTheme(RootGrid);

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

            ToolManifest? abrirCampos = null;
            if (!string.IsNullOrWhiteSpace(toolId))
            {
                var tool = App.Catalog.Find(toolId);
                if (tool is not null)
                {
                    SetSearchText(tool.Name);
                    var row = _rows.FirstOrDefault(r => ReferenceEquals(r.Tool, tool));
                    if (row is not null) ResultsList.SelectedItem = row;

                    // `--hud <id>` abre já nos campos — o mesmo estado de digitar o nome e dar Enter.
                    if (tool.Inputs.Count > 0 && !tool.IsDataset) abrirCampos = tool;
                }
            }

            SearchBox.Focus(FocusState.Programmatic);
            _suppressHide = false;
            ApplySize();

            if (abrirCampos is not null) ShowFields(abrirCampos);

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
            DisposePings();          // nenhum ping continua depois que a HUD sai da tela
            AppWindow.Hide();
        }
        catch (Exception ex) { AppPaths.Log("HideHud: " + ex.Message); }
    }

    /// <summary>SQ-005: a casa fecha a busca e invoca a janela principal (focando se já estiver aberta).</summary>
    private void OnHomeClick(object sender, RoutedEventArgs e)
    {
        AppPaths.Log("hud: casa clicada — abrindo a janela principal");
        HideHud();
        App.ShowMainWindow();
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_suppressHide) return;
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 600) return;
            if (!SettingsService.Current.HudCloseOnClickOutside) return;   // SQ-002: preferência do usuário
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
                AppPaths.Log("foco aplicado: " + args.WindowActivationState + " em " + DescreverFoco());
            }
            catch (Exception ex) { AppPaths.Log("foco falhou: " + ex.Message); }
        });
    }

    private void ResetState()
    {
        _cts?.Cancel();
        _cts = null;
        DisposePings();
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
        SetSearchText("");
        RefreshResults("");
    }

    // ------------------------------------------------------------------ busca
    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        if (_busy) return;

        // UX-002: o TextChanged da escrita feita por ShowHud pode chegar atrasado — depois de
        // ShowFields(). Na 2ª invocação do atalho ele caía no ramo "voltou a digitar" e fechava os
        // campos recém-abertos, deixando a ferramenta só destacada na lista (era preciso Enter).
        // Texto igual ao que o código escreveu é eco, não digitação: mantém a lista coerente e sai.
        if (_active is not null &&
            string.Equals(SearchBox.Text, _searchTextFromCode, StringComparison.Ordinal))
        {
            AppPaths.Log("hud: eco da escrita do próprio HUD ignorado (campos preservados)");
            RefreshResults(SearchBox.Text);
            return;
        }

        if (_active is not null)
        {
            // voltou a digitar: sai do modo campos
            DisposePings();
            _active = null;
            _boxes.Clear();
            _hints.Clear();
            FieldsPanel.Children.Clear();
            FieldsPanel.Visibility = Visibility.Collapsed;
            ErrorBanner.Visibility = Visibility.Collapsed;
        }
        RefreshResults(SearchBox.Text);
    }

    /// <summary>Escreve na busca marcando a escrita como do código (o eco não é digitação).</summary>
    private void SetSearchText(string text)
    {
        _searchTextFromCode = text;
        SearchBox.Text = text;
    }

    private void RefreshResults(string query)
    {
        var previousId = (_rows.Count > 0 && ResultsList.SelectedItem is ToolRow prev) ? prev.Tool.Id : null;

        // CFG-001: ferramenta oculta não aparece na busca (segue no menu lateral e na bandeja)
        var matches = App.Catalog.Search(query)
            .Where(t => !SettingsService.Current.HiddenTools.Contains(t.Id))
            .ToList();

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
        if (SettingsService.Current.HideToolCount)
            StatusText.Text = "";   // preferência do usuário: rodapé sem a contagem
        else if (string.IsNullOrWhiteSpace(StatusText.Text) || !StatusText.Text.StartsWith("No", StringComparison.Ordinal))
            StatusText.Text = $"{_rows.Count} ferramenta(s).";
        ApplySize();
    }

    /// <summary>
    /// Altura acompanha o conteúdo. Com a faixa de erro visível a lista de resultados SAI de cena:
    /// era ela que empurrava a faixa (mensagem de 2 linhas + botões) para fora da janela.
    /// </summary>
    private void ApplySize()
    {
        try
        {
            // erro ocupa o lugar da lista, não soma com ela. A row * também precisa zerar: com o filho
            // colapsado ela continuava reservando o espaço e empurrava a faixa para fora da janela.
            var comErro = ErrorBanner.Visibility == Visibility.Visible;
            ResultsList.Visibility = comErro ? Visibility.Collapsed : Visibility.Visible;
            RootGrid.RowDefinitions[1].Height = comErro
                ? new GridLength(0)
                : new GridLength(1, GridUnitType.Star);

            double height = 66 + 34; // busca + rodapé
            if (ResultsList.Visibility == Visibility.Visible)
                height += Math.Clamp(Math.Max(_rows.Count, 1), 1, 8) * 56;
            if (FieldsPanel.Visibility == Visibility.Visible)
                height += _boxes.Count * 82 + _hints.Count * 18 + 10 + 46;   // +46 = botão "Consultar"

            if (ErrorBanner.Visibility == Visibility.Visible)
            {
                ErrorBanner.Measure(new Windows.Foundation.Size(WidthDip - 28, double.PositiveInfinity));
                height += Math.Max(150, ErrorBanner.DesiredSize.Height + 40);
            }

            height = Math.Clamp(height, 190, 660);

            var scale = UiKit.Scale(this);
            AppWindow.Resize(new Windows.Graphics.SizeInt32(
                (int)Math.Round(WidthDip * scale), (int)Math.Round(height * scale)));
            UiKit.TrimFrame(this);   // UI-005: a região acompanha o novo tamanho
            AppPaths.Log($"hud: altura {height:0} dip (itens={_rows.Count}, campos={_boxes.Count}, faixa={ErrorBanner.Visibility})");
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
        // ferramenta de base local: não executa processo, abre o painel de consulta
        if (tool.IsDataset)
        {
            HideHud();
            SsePanelController.Open();
            return;
        }

        if (tool.Inputs.Count == 0)
        {
            await ExecuteAsync(tool);
            return;
        }
        ShowFields(tool);
    }

    private void ShowFields(ToolManifest tool)
    {
        DisposePings();          // trocar de ferramenta reinicia a sessão de ping (trava de 100)

        // SQ-004: ferramenta de base local (Consulta SSE) ou sem campo não tem painel de campos.
        // Antes o painel abria vazio para ela e o Enter seguinte mandava a ferramenta para o
        // ToolRunner, que devolvia "Script da ferramenta não encontrado:" — truncado, porque o
        // script é vazio. Agora ela fica só destacada na lista e o Enter abre pelo caminho próprio.
        if (tool.IsDataset || tool.Inputs.Count == 0)
        {
            _active = null;
            _boxes.Clear();
            _hints.Clear();
            FieldsPanel.Children.Clear();
            FieldsPanel.Visibility = Visibility.Collapsed;
            ErrorBanner.Visibility = Visibility.Collapsed;
            StatusText.Text = "";
            ApplySize();

            // o campo que tinha o foco saiu da tela: sem devolver o foco à busca, as setas e o Enter
            // morrem junto com o painel. Voltando para a busca, ↑↓ seguem trocando e o Enter abre.
            SearchBox.Focus(FocusState.Programmatic);

            AppPaths.Log($"hud: «{tool.Id}» fica na seleção (dataset ou sem campo) — Enter abre pelo caminho próprio");
            return;
        }

        _active = tool;
        _boxes.Clear();
        _hints.Clear();
        FieldsPanel.Children.Clear();
        ErrorBanner.Visibility = Visibility.Collapsed;

        foreach (var field in tool.Inputs)
        {
            var box = new TextBox
            {
                Name = "campo-" + field.Key,
                Header = field.Label,
                PlaceholderText = field.Placeholder,
                FontSize = 14
            };
            box.KeyDown += OnFieldKeyDown;
            box.TextChanged += (_, _) => UpdateHint(field);
            _boxes[field.Key] = box;

            // NET-001: campo de IP declarado com "ping" ganha o retorno ICMP ao lado do valor.
            var chip = field.Ping && field.Type == "ip" ? CreatePingLine(box, tool) : null;
            if (chip is null)
            {
                FieldsPanel.Children.Add(box);
            }
            else
            {
                var linha = new Grid();
                linha.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                linha.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
                linha.Children.Add(box);
                Grid.SetColumn(chip, 1);
                linha.Children.Add(chip);
                FieldsPanel.Children.Add(linha);
            }

            if (field.EmptyUsesClipboard)
            {
                var hint = new TextBlock
                {
                    FontSize = 11,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(UiKit.DimText),
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
            Foreground = new SolidColorBrush(UiKit.DimText)
        });
        FieldsPanel.Children.Add(actions);

        // Posição na lista + dica das setas (SQ-001): sem essa linha ninguém descobre que ↑↓ troca
        // de ferramenta com o painel de campos aberto.
        var lista = _rows.ToList();
        var pos = lista.FindIndex(r => ReferenceEquals(r.Tool, tool));
        if (pos >= 0 && lista.Count > 1)
        {
            FieldsPanel.Children.Insert(0, new TextBlock
            {
                Text = $"{pos + 1} de {lista.Count} · ↑↓ troca de ferramenta",
                FontSize = 11,
                Foreground = new SolidColorBrush(UiKit.DimText),
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        FieldsPanel.Visibility = Visibility.Visible;

        // O rodapé NÃO repete a descrição: ela já está na linha do resultado e, ao abrir os campos,
        // repetia o mesmo texto duas vezes na mesma tela (regra R12).
        StatusText.Text = "";
        ApplySize();
        if (_boxes.Count > 0)
        {
            var first = _boxes.Values.First();
            first.Focus(FocusState.Programmatic);
            // UX-001: prova objetiva do "campo focado" — o diagnóstico --tool-hotkey lê esta linha.
            AppPaths.Log($"hud: «{tool.Id}» campos abertos, foco em {DescreverFoco()}");
        }
    }

    /// <summary>Nome do controle com foco — usado no log de abertura (UX-001).</summary>
    private string DescreverFoco()
    {
        try
        {
            var raiz = RootGrid.XamlRoot;
            var elemento = raiz is null ? null : FocusManager.GetFocusedElement(raiz) as FrameworkElement;
            return elemento switch
            {
                Control c when !string.IsNullOrEmpty(c.Name) => c.Name,
                Control c => c.GetType().Name,
                null => "nenhum",
                _ => elemento.GetType().Name
            };
        }
        catch { return "?"; }
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

        // Sem eco do conteúdo da área de transferência. Antes o rodapé virava "agora: <clipboard>",
        // o que duplicava metadado e jogava texto alheio para dentro da HUD (regra R12).
        var declarado = string.IsNullOrWhiteSpace(field.Hint) ? "" : field.Hint.Trim();
        hint.Text = declarado.Length > 0
            ? $"{declarado} · vazio usa a área de transferência"
            : "vazio usa a área de transferência";
    }

    // ------------------------------------------------------------------ ping do destino (NET-001)
    /// <summary>
    /// Monta a linha de retorno ICMP ao lado do campo (uma linha, opacidade 75%) e liga o campo ao
    /// coordenador de debounce. A sessão é da invocação: 100 disparos, reiniciada a cada abertura.
    /// </summary>
    private TextBlock CreatePingLine(TextBox box, ToolManifest tool)
    {
        _pingSession ??= new PingSession(App.Settings.PingTimeoutMs);

        var chip = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,                                   // requisito NET-001
            Margin = new Thickness(12, 0, 2, 9),
            VerticalAlignment = VerticalAlignment.Bottom,
            Text = "",
            Foreground = new SolidColorBrush(UiKit.DimText)
        };

        var coordinator = new PingCoordinator(_pingSession, App.Settings.PingDebounceMs,
            sample => DispatcherQueue.TryEnqueue(() => ShowPingSample(chip, sample)));
        _pings.Add(coordinator);
        box.TextChanged += (_, _) => coordinator.Edit(box.Text);

        AppPaths.Log($"hud: ping ativo em «{tool.Id}» · debounce={coordinator.DebounceMs} ms · " +
                     $"timeout={_pingSession.TimeoutMs} ms · trava={PingSession.MaxProbes}");
        return chip;
    }

    private void ShowPingSample(TextBlock chip, PingSample? sample)
    {
        if (sample is null)
        {
            chip.Text = "";
            return;
        }

        chip.Text = sample.Text;
        chip.Foreground = new SolidColorBrush(sample.Outcome switch
        {
            PingOutcome.Ok => UiKit.Ok,
            PingOutcome.BudgetExhausted => UiKit.Warn,
            _ => UiKit.Bad
        });

        // a trava precisa de mensagem clara; a frase inteira não cabe ao lado do campo
        if (sample.Outcome == PingOutcome.BudgetExhausted) StatusText.Text = PingSession.BudgetMessage;
    }

    private void DisposePings()
    {
        if (_pings.Count > 0)
            AppPaths.Log($"hud ping: sessão encerrada ({_pingSession?.Used}/{PingSession.MaxProbes})");
        foreach (var coordinator in _pings) coordinator.Dispose();
        _pings.Clear();
        _pingSession = null;
    }

    /// <summary>
    /// Diagnóstico (`--hud <id> --fill <valor>`): escreve no primeiro campo aberto pelo mesmo caminho
    /// da digitação, para provar a linha de ping sem teclado sintético na máquina do usuário.
    /// </summary>
    public void FillFirstField(string value)
    {
        if (_boxes.Count == 0)
        {
            AppPaths.Log("hud --fill: nenhum campo aberto");
            return;
        }

        // aceita passos separados por "|": cada um é uma edição do campo (prova do debounce e
        // do cancelamento na tela, sem teclado sintético)
        var passos = (value ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries);
        if (passos.Length == 0) return;
        var indice = 0;

        void Aplicar()
        {
            var box = _boxes.Values.First();
            box.Text = passos[indice];
            box.Focus(FocusState.Programmatic);
            AppPaths.Log($"hud --fill [{indice + 1}/{passos.Length}]: «{_active?.Id}» = {passos[indice]} " +
                         $"(lido de volta: \"{box.Text}\", campos={_boxes.Count})");
            indice++;
            if (indice >= passos.Length)
            {
                _fillTimer?.Stop();
                _fillTimer = null;
            }
        }

        _fillTimer?.Stop();
        Aplicar();
        if (passos.Length <= 1) return;

        _fillTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _fillTimer.Tick += (_, _) => Aplicar();
        _fillTimer.Start();
    }

    /// <summary>
    /// Diagnóstico (`--submit`): executa o MESMO caminho do Enter — campos abertos executam a
    /// ferramenta ativa; sem campos, invoca o item destacado na lista (é o que o Enter faz na busca).
    /// </summary>
    public Task SubmitActiveAsync() => AdvanceAsync();

    /// <summary>
    /// Diagnóstico (`--cycle <id>`): troca de ferramenta com o painel de campos aberto, pelo MESMO
    /// caminho das setas (SQ-001), até destacar a ferramenta pedida. Existe para reproduzir o SQ-004
    /// (Consulta SSE alcançada ciclando) sem teclado sintético na máquina de quem trabalha.
    /// </summary>
    public void CycleTo(string toolId)
    {
        if (_active is null && ResultsList.SelectedItem is null)
        {
            AppPaths.Log("hud --cycle: nenhuma ferramenta destacada");
            return;
        }

        for (var passo = 0; passo <= _rows.Count; passo++)
        {
            var destacado = (ResultsList.SelectedItem as ToolRow)?.Tool.Id;
            if (string.Equals(destacado, toolId, StringComparison.OrdinalIgnoreCase))
            {
                AppPaths.Log($"hud --cycle: destacado «{toolId}» após {passo} passo(s)");
                return;
            }

            // mesma decisão das setas: com campos abertos, TrocarFerramenta; sem campos
            // (ferramenta de base local), só move a seleção — é o que a busca faz.
            if (_active is not null)
            {
                if (!TrocarFerramenta(1))
                {
                    AppPaths.Log($"hud --cycle: setas pararam antes de «{toolId}» (atual: {destacado})");
                    return;
                }
            }
            else
            {
                MoveSelection(1);
            }
        }

        AppPaths.Log($"hud --cycle: «{toolId}» não alcançada ({(ResultsList.SelectedItem as ToolRow)?.Tool.Id}, {_rows.Count} itens)");
    }

    private void OnFieldKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            if (_active is not null) _ = ExecuteAsync(_active);
        }
        else if (e.Key == VirtualKey.Up || e.Key == VirtualKey.Down)
        {
            // SQ-001/D5: setas trocam de ferramenta — a menos que o campo declare que as usa.
            var campo = _active?.Inputs.FirstOrDefault(f =>
                _boxes.TryGetValue(f.Key, out var box) && ReferenceEquals(box, sender));
            if (campo is not null && campo.UsesArrows) return;

            if (TrocarFerramenta(e.Key == VirtualKey.Up ? -1 : +1)) e.Handled = true;
        }
        else if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            if (_busy) { _cts?.Cancel(); StatusText.Text = "Cancelando…"; }
            else HideHud();
        }
    }

    /// <summary>
    /// Move a seleção entre as ferramentas do resultado com wrap-around, mantendo o painel de campos
    /// aberto (antes, ao dar Enter, o foco ia para o campo e as setas morriam ali).
    /// </summary>
    private bool TrocarFerramenta(int passo)
    {
        var lista = _rows.ToList();
        if (lista.Count < 2 || _active is null) return false;

        var atual = lista.FindIndex(r => ReferenceEquals(r.Tool, _active));
        if (atual < 0) return false;

        var proximo = (atual + passo + lista.Count) % lista.Count;
        var alvo = lista[proximo];

        AppPaths.Log($"hud: ↑↓ {atual + 1}→{proximo + 1} de {lista.Count} — {alvo.Tool.Id}");
        ResultsList.SelectedIndex = proximo;
        ShowFields(alvo.Tool);
        return true;
    }

    // ------------------------------------------------------------------ execução
    private async Task ExecuteAsync(ToolManifest tool)
    {
        if (_busy) return;

        AppPaths.Log($"hud execute: «{tool.Id}» kind={(tool.IsDataset ? "dataset" : "script")} " +
                     $"script=\"{tool.Script}\" campos={tool.Inputs.Count}");

        // SQ-004: base local nunca executa processo. Se algum caminho chegar aqui com uma ferramenta
        // dataset, o destino certo é o painel dela — não o ToolRunner.
        if (tool.IsDataset)
        {
            AppPaths.Log($"hud execute: «{tool.Id}» é base local — abrindo o painel");
            HideHud();
            SsePanelController.Open();
            return;
        }

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

            var problem = FieldValidation.Check(field, value);
            if (problem is not null) { validationError ??= problem; continue; }
            inputs[field.Key] = value;
        }

        if (validationError is not null)
        {
            // MKT-003: erro de digitação não tem detalhe para copiar nem consulta para repetir.
            ShowError(validationError);
            return;
        }

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        SetBusy(true);
        ErrorBanner.Visibility = Visibility.Collapsed;
        StatusText.Text = $"Consultando «{tool.Name}»…";

        var result = await ToolRunner.RunAsync(tool, inputs, _cts.Token);
        SetBusy(false);

        var inputLabel = tool.HistoryLabel(inputs);
        HistoryStore.Append(new HistoryEntry(DateTime.Now, tool.Id, tool.Name, inputLabel,
            result.Ok, result.Elapsed.TotalSeconds, result.Ok ? result.Value : result.Detail, "hud"));

        if (result.Ok) await PresentSuccessAsync(tool, result, inputLabel, inputs);
        else await PresentFailureAsync(tool, result);
    }

    private async Task PresentSuccessAsync(ToolManifest tool, RunResult result, string inputLabel,
        IReadOnlyDictionary<string, string> inputs)
    {
        // NET-001: os 2 últimos retornos de ping do endereço consultado vão no aviso de resultado.
        // Lido ANTES de HideHud(), que encerra a sessão de ping desta invocação.
        var pingField = tool.Inputs.FirstOrDefault(i => i.Ping);
        var pingLine = pingField is not null && inputs.TryGetValue(pingField.Key, out var ipText)
            ? _pingSession?.Summary(ipText, 2) ?? ""
            : "";

        var mode = SettingsService.OutputModeFor(tool.Id, tool.Output.Default);
        var value = result.Value;

        // MKT-001: "sem SN" tem vocabulário próprio e NÃO copia/digita — o clipboard anterior fica
        // intacto (mesmo princípio do script em falha de rede).
        if (result.IsZeroComNota(tool))
        {
            var titulo = tool.Output.ZeroValue!.Title;
            var nota = result.ZeroSubtitle(tool, inputs);
            HideHud();
            ToastController.ShowSuccess(tool, titulo, nota, "notify", pingLine);
            return;
        }

        if (mode is not "notify")
        {
            if (!ClipboardService.SetText(value))
                AppPaths.Log("clipboard: SetText falhou");
        }

        // NET-004: "0" é resposta, não erro — mas precisa de contexto ("<IP> sem SN").
        var subtitle = result.SuccessSubtitle(tool, tool.Output.ValueLabel);

        HideHud();

        if (mode == "type")
        {
            await Task.Delay(200);
            var typed = TypingService.TypeText(value);
            if (!typed) subtitle = "não foi possível digitar — o valor está na área de transferência";
        }

        ToastController.ShowSuccess(tool, value, subtitle, mode, pingLine);
    }

    private async Task PresentFailureAsync(ToolManifest tool, RunResult result)
    {
        var detail = result.Detail;

        // MAC-001: "endereço localmente administrado" é resposta de negócio, não falha técnica — não
        // existe o que repetir nem detalhe para copiar. O script grafa "Endereco" sem acento.
        if (!string.IsNullOrWhiteSpace(detail) &&
            detail.Contains("localmente administrado", StringComparison.OrdinalIgnoreCase))
        {
            ShowError(detail);
            return;
        }

        if (tool.Network is not null && !result.TimedOut)
        {
            var reachable = await Probes.CheckTcpAsync(tool.Network.Host, tool.Network.Port, 2500);
            if (!reachable)
            {
                ShowError("Sem conexão com o servidor. Verifique a VPN e tente novamente.");
                return;
            }
        }

        ShowError(string.IsNullOrWhiteSpace(detail) ? "A ferramenta não devolveu resultado." : detail);
    }

    /// <summary>
    /// SQ-006: erro na busca rápida é TEXTO — nenhum botão. Repetir a consulta, testar a conexão e
    /// copiar detalhe saíram daqui; quem quiser repetir dá Enter de novo, e o aviso fecha com Esc,
    /// clique fora ou a próxima consulta. (MKT-003/MAC-001 já tinham tirado botões dos erros de dado.)
    /// </summary>
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorBanner.Visibility = Visibility.Visible;
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
    // SQ-006: os botões do estado de erro (repetir, verificar conexão, copiar detalhe, fechar)
    // foram removidos junto com a faixa que os hospedava. A HUD não tem mais ação nesse estado.

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
