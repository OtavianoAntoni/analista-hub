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
    public SseCityRow(SseCity city)
    {
        Source = city;
        Name = city.Name;
        Uf = city.Uf;
        Sigla = city.Sigla;
        Rows = city.Rows.Select(r => new SseLineRow(r)).ToList();
    }

    public SseCity Source { get; }
    public string Name { get; }
    public string Uf { get; }
    public string Sigla { get; }
    public List<SseLineRow> Rows { get; }
}

/// <summary>
/// Painel da Consulta SSE: digitar a cidade e copiar Sigla Anatel + NM + OLT + VPN.
/// Mesmo comportamento de janela da HUD (sem moldura, no topo, Esc fecha), porque a
/// consulta é feita no meio de um atendimento e não pode roubar o fluxo do analista.
/// </summary>
public sealed partial class ConsultaSseWindow : Window
{
    private const double WidthDip = 780;
    private const double HeightDip = 580;

    private readonly ObservableCollection<SseCityRow> _rows = new();
    private DateTime _shownAt = DateTime.MinValue;
    private bool _suppressHide;

    public ConsultaSseWindow()
    {
        InitializeComponent();
        Title = "Analista Hub — Consulta SSE";
        UiKit.MakeHudWindow(this);
        UiKit.TryMica(this);
        RootGrid.RequestedTheme = ElementTheme.Dark;

        ResultsList.ItemsSource = _rows;
        SearchBox.TextChanged += (_, _) => Refresh();
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.ItemClick += (_, e) => { if (e.ClickedItem is SseCityRow row) Copy(row); };
        Activated += OnActivated;
    }

    // ------------------------------------------------------------------ ciclo de vida
    public void ShowPanel(string? query = null)
    {
        try
        {
            _suppressHide = true;
            var catalog = SseCatalog.Current;

            HeaderInfo.Text = catalog.Loaded
                ? $"{catalog.Cities.Count} cidades · {catalog.TotalLines} linhas · base de {catalog.Generated}"
                : "base indisponível";
            SourceText.Text = catalog.Loaded
                ? "fonte: " + catalog.DataPath
                : catalog.Error ?? "";

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
        _rows.Clear();
        foreach (var city in matches) _rows.Add(new SseCityRow(city));

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
        StatusText.Text = term.Length == 0
            ? $"{_rows.Count} cidades · digite para filtrar"
            : $"{_rows.Count} resultado(s)";
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
        var text = SseCatalog.Format(row.Source);
        if (!ClipboardService.SetText(text)) AppPaths.Log("ConsultaSse: clipboard falhou");

        var subtitle = $"copiado · Sigla Anatel {row.Sigla} · {row.Rows.Count} rede(s)";
        HidePanel();
        ToastController.ShowMessage("Consulta SSE", $"{row.Name} ({row.Uf})", subtitle);

        AppPaths.Log($"ConsultaSse: copiado {row.Name}/{row.Uf} ({row.Rows.Count} linha(s))");
    }
}

/// <summary>Instância única do painel — aberto pelo gatilho, pelo card da ferramenta e pelo menu da bandeja.</summary>
public static class SsePanelController
{
    private static ConsultaSseWindow? _panel;

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
}
