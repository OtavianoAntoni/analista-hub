using System.Collections.ObjectModel;
using AnalistaHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace AnalistaHub.Views;

/// <summary>Linha NM/OLT/VPN exibida no cartão da cidade.</summary>
public sealed class SseLineRow
{
    public SseLineRow(SseRow row)
    {
        Nm = row.Nm;
        Olt = row.Olt;
        Vpn = row.Vpn;
    }

    public string Nm { get; }
    public string Olt { get; }
    public string Vpn { get; }
}

/// <summary>Cartão de uma cidade na lista de resultados.</summary>
public sealed class SseCityRow
{
    public SseCityRow(SseCity city, bool showNodes, bool showNoNodes)
    {
        Source = city;
        Name = city.Name;
        Uf = city.Uf;
        Sigla = city.Sigla;
        HasAccess = city.HasAccess;

        // o botão "NMs" só decide se as redes são materializadas na lista
        Rows = showNodes && city.HasAccess
            ? city.Rows.Select(r => new SseLineRow(r)).ToList()
            : new List<SseLineRow>();

        NodesVisibility = Rows.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        // "somente cadastro · sem NM" é opcional: por padrão fica oculto (Dados extras)
        NoNodesVisibility = !city.HasAccess && showNoNodes ? Visibility.Visible : Visibility.Collapsed;
    }

    public SseCity Source { get; }
    public string Name { get; }
    public string Uf { get; }
    public string Sigla { get; }
    public bool HasAccess { get; }
    public List<SseLineRow> Rows { get; }
    public Visibility NodesVisibility { get; }
    public Visibility NoNodesVisibility { get; }
}

/// <summary>
/// Painel da Consulta: digitar a cidade e copiar Sigla Anatel + estado + NM/OLT/VPN.
/// Mesmo comportamento de janela da HUD (sem moldura, no topo, Esc fecha), porque a
/// consulta é feita no meio de um atendimento e não pode roubar o fluxo do analista.
///
/// O botão "NMs" do cabeçalho oculta as informações de acesso (NM · OLT · VPN) para
/// deixar a lista limpa; a escolha fica gravada e volta na próxima abertura.
/// </summary>
public sealed partial class ConsultaSseWindow : Window
{
    private const double WidthDip = 780;
    private const double HeightDip = 580;

    private readonly ObservableCollection<SseCityRow> _rows = new();
    private DateTime _shownAt = DateTime.MinValue;
    private bool _suppressHide;
    private bool _showNodes = true;
    private bool _showNoNodes;
    private bool _syncToggle;

    public ConsultaSseWindow()
    {
        InitializeComponent();
        Title = "Analista Hub — Consulta SSE";
        UiKit.MakeHudWindow(this);
        UiKit.TryMica(this);
        UiKit.ApplyTheme(RootGrid);

        ResultsList.ItemsSource = _rows;
        SearchBox.TextChanged += (_, _) => Refresh();
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.ItemClick += (_, e) => { if (e.ClickedItem is SseCityRow row) Copy(row); };
        Activated += OnActivated;

        _showNodes = SettingsService.Current.SseShowNodes;
        NmsToggle.IsChecked = _showNodes;   // antes de assinar: não dispara evento
        NmsToggle.Checked += OnNmsToggleChanged;
        NmsToggle.Unchecked += OnNmsToggleChanged;

        BuildExtraMenu();
    }

    /// <summary>
    /// "Dados extras" no menu ⚙: o painel é o mesmo usado na página da ferramenta na janela principal.
    /// </summary>
    private void BuildExtraMenu() =>
        ExtraButton.Flyout = new Flyout { Content = SseExtras.Build(AplicarExtras) };

    /// <summary>Reaplica as opções de "Dados extras" quando a mudança vem de fora desta janela.</summary>
    public void AplicarExtrasPublico() => AplicarExtras();

    /// <summary>
    /// Reaplica o que o painel "Dados extras" controla. Chamado quando o usuário muda uma opção —
    /// inclusive da janela principal, porque as duas telas leem a mesma preferência.
    /// </summary>
    private void AplicarExtras()
    {
        _showNoNodes = SettingsService.Current.SseShowNoNodes;
        HeaderInfo.Visibility = SettingsService.Current.SseShowBaseInfo ? Visibility.Visible : Visibility.Collapsed;
        SourceText.Visibility = SettingsService.Current.SseShowSource ? Visibility.Visible : Visibility.Collapsed;
        Refresh();
    }

    // ------------------------------------------------------------------ ciclo de vida
    /// <summary>Termo digitado agora (usado ao recriar o painel na troca de tema).</summary>
    public string CurrentQuery => SearchBox.Text;

    /// <summary>Painel está visível?</summary>
    public bool Visible => AppWindow.IsVisible;

    /// <summary>Reaplica o tema no painel.</summary>
    public void ApplyThemeNow() => UiKit.ApplyTheme(RootGrid);

    public void ShowPanel(string? query = null)
    {
        try
        {
            _suppressHide = true;
            var catalog = SseCatalog.Current;

            HeaderInfo.Text = catalog.Loaded
                ? $"{catalog.Cities.Count} cidades · {catalog.TotalWithAccess} com NM · base de {catalog.Generated}"
                : "base indisponível";
            SourceText.Text = catalog.Loaded
                ? "fonte: " + catalog.DataPath
                : catalog.Error ?? "";

            // resumo da base e fonte ficam OCULTOS por padrão; ligados em "Dados extras"
            HeaderInfo.Visibility = SettingsService.Current.SseShowBaseInfo ? Visibility.Visible : Visibility.Collapsed;
            SourceText.Visibility = SettingsService.Current.SseShowSource ? Visibility.Visible : Visibility.Collapsed;
            _showNoNodes = SettingsService.Current.SseShowNoNodes;

            // a preferência pode ter mudado em outra janela do app
            _syncToggle = true;
            _showNodes = SettingsService.Current.SseShowNodes;
            NmsToggle.IsChecked = _showNodes;
            _syncToggle = false;

            SearchBox.Text = query ?? "";
            Refresh();

            UiKit.PlaceHud(this, WidthDip, HeightDip);
            AppWindow.Show();
            Activate();
            UiKit.ForceForeground(this);
            _shownAt = DateTime.UtcNow;
            SearchBox.Focus(FocusState.Programmatic);
            _suppressHide = false;

            AppPaths.Log($"ConsultaSse.ShowPanel: {_rows.Count} resultado(s), base={catalog.DataPath}");
        }
        catch (Exception ex)
        {
            _suppressHide = false;
            AppPaths.Log("ConsultaSse.ShowPanel FALHOU: " + ex);
        }
    }

    public void HidePanel()
    {
        try { AppWindow.Hide(); }
        catch (Exception ex) { AppPaths.Log("ConsultaSse.HidePanel: " + ex.Message); }
    }

    private void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            if (_suppressHide) return;
            if ((DateTime.UtcNow - _shownAt).TotalMilliseconds < 600) return;
            HidePanel();
            return;
        }

        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            try { SearchBox.Focus(FocusState.Programmatic); }
            catch (Exception ex) { AppPaths.Log("ConsultaSse foco: " + ex.Message); }
        });
    }

    // ------------------------------------------------------------------ busca
    private void Refresh()
    {
        var catalog = SseCatalog.Current;
        var term = SearchBox.Text.Trim();
        var previous = (ResultsList.SelectedItem as SseCityRow)?.Source.Name;

        var matches = catalog.Search(term);
        var limite = SseExtras.MaxRowsAtual;
        var mostrados = matches.Count > limite ? matches.Take(limite).ToList() : matches;

        _rows.Clear();
        foreach (var city in mostrados) _rows.Add(new SseCityRow(city, _showNodes, _showNoNodes));

        var empty = _rows.Count == 0;
        ResultsList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        EmptyText.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;

        if (empty)
        {
            EmptyText.Text = catalog.Loaded
                ? $"Nenhuma cidade encontrada para «{term}»."
                : catalog.Error;
            StatusText.Text = catalog.Loaded ? "0 cidades" : "base não carregada";
            return;
        }

        var keep = previous is null ? null : _rows.FirstOrDefault(r => r.Source.Name == previous);
        ResultsList.SelectedItem = keep ?? _rows[0];

        StatusText.Text = matches.Count > mostrados.Count
            ? $"mostrando {mostrados.Count} de {matches.Count}"
            : term.Length == 0
                ? $"{_rows.Count} cidades · digite para filtrar"
                : $"{_rows.Count} resultado(s)";
    }

    /// <summary>Botão "NMs": liga/desliga as informações de acesso e grava a preferência.</summary>
    private void OnNmsToggleChanged(object sender, RoutedEventArgs e)
    {
        if (_syncToggle) return;
        _showNodes = NmsToggle.IsChecked == true;
        SettingsService.Current.SseShowNodes = _showNodes;
        SettingsService.Save();
        Refresh();
        SearchBox.Focus(FocusState.Programmatic);
        AppPaths.Log($"ConsultaSse: NMs {(_showNodes ? "visíveis" : "ocultos")}");
    }

    private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case VirtualKey.Down:
                e.Handled = true;
                MoveSelection(1);
                break;
            case VirtualKey.Up:
                e.Handled = true;
                MoveSelection(-1);
                break;
            case VirtualKey.Enter:
                e.Handled = true;
                if (ResultsList.SelectedItem is SseCityRow row) Copy(row);
                break;
            case VirtualKey.Escape:
                e.Handled = true;
                HidePanel();
                break;
        }
    }

    private void MoveSelection(int delta)
    {
        if (_rows.Count == 0) return;
        var index = Math.Clamp(ResultsList.SelectedIndex + delta, 0, _rows.Count - 1);
        ResultsList.SelectedIndex = index;
        ResultsList.ScrollIntoView(_rows[index]);
    }

    private void Copy(SseCityRow row)
    {
        // o texto copiado sempre leva o que existe, mesmo com o botão "NMs" desligado
        var text = SseCatalog.Format(row.Source);
        if (!ClipboardService.SetText(text)) AppPaths.Log("ConsultaSse: clipboard falhou");

        var subtitle = row.HasAccess
            ? $"copiado · Sigla Anatel {row.Sigla} · {row.Source.Rows.Count} rede(s)"
            : $"copiado · Sigla Anatel {row.Sigla} · somente cadastro";
        HidePanel();
        ToastController.ShowMessage("Consulta SSE", $"{row.Name} ({row.Uf})", subtitle);

        AppPaths.Log($"ConsultaSse: copiado {row.Name}/{row.Uf} ({row.Source.Rows.Count} linha(s))");
    }
}

/// <summary>Instância única do painel — aberto pela busca, pelo card da ferramenta e pelo menu da bandeja.</summary>
public static class SsePanelController
{
    private static ConsultaSseWindow? _panel;

    /// <summary>
    /// Recria o painel preservando o termo digitado. Necessário na troca de tema: os cartões são
    /// montados em código e ficariam com as cores do tema anterior.
    /// </summary>
    public static void Rebuild()
    {
        if (_panel is null) return;
        var termo = _panel.CurrentQuery;
        var visivel = _panel.Visible;
        try { _panel.HidePanel(); } catch { }
        _panel = null;
        if (visivel) Open(termo);
    }

    public static void Open(string? query = null)
    {
        try
        {
            _panel ??= new ConsultaSseWindow();
            _panel.ShowPanel(query);
        }
        catch (Exception ex) { AppPaths.Log("SsePanelController.Open: " + ex); }
    }

    public static void HideAll()
    {
        try { _panel?.HidePanel(); } catch { }
    }

    /// <summary>
    /// Reaplica as opções de "Dados extras" se o painel já existe — usado quando a mudança vem da
    /// janela principal, para a tela aberta refletir na hora.
    /// </summary>
    public static void AplicarExtras()
    {
        try { _panel?.AplicarExtrasPublico(); } catch (Exception ex) { AppPaths.Log("SsePanel.AplicarExtras: " + ex.Message); }
    }
}
