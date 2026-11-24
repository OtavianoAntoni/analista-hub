using AnalistaHub.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnalistaHub.Views;

/// <summary>
/// Painel "Dados extras" da Consulta SSE — construído uma vez e usado em DOIS lugares:
///   • no menu ⚙ do painel de consulta (janela flutuante);
///   • na página da ferramenta, na janela principal do HUB.
/// Uma fonte só, para rótulo e comportamento nunca divergirem.
/// Todas as opções afetam as duas telas e ficam gravadas em settings.json.
/// </summary>
public static class SseExtras
{
    public const int MinRows = 10;
    public const int MaxRows = 500;

    /// <summary>Cartões por busca: preferência do usuário (padrão 30), presa entre 10 e 500.</summary>
    public static int MaxRowsAtual
    {
        get
        {
            var v = SettingsService.Current.SseMaxRows;
            if (v < MinRows) v = MinRows;
            if (v > MaxRows) v = MaxRows;
            return v;
        }
    }

    /// <summary>
    /// Monta o painel. <paramref name="onChange"/> roda depois de gravar, para a tela que hospeda
    /// atualizar o que já está desenhado (lista, cabeçalho, rodapé).
    /// </summary>
    public static StackPanel Build(Action? onChange = null, bool comTitulo = true)
    {
        var painel = new StackPanel { Spacing = 8 };

        if (comTitulo)
        {
            painel.Children.Add(new TextBlock
            {
                Text = "Dados extras",
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(0, 0, 0, 2)
            });
        }

        painel.Children.Add(new TextBlock
        {
            Text = "Ocultos por padrão: a consulta abre limpa, só com cidade, estado e sigla.",
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 2)
        });

        var ckNm = new CheckBox
        {
            Content = "Registro de NM por cidade",
            IsChecked = SettingsService.Current.SseShowNoNodes
        };
        ToolTipService.SetToolTip(ckNm, "Mostra \"somente cadastro · sem NM\" nas cidades sem rede");
        ckNm.Checked += (_, _) => Salvar(s => s.SseShowNoNodes = true, onChange);
        ckNm.Unchecked += (_, _) => Salvar(s => s.SseShowNoNodes = false, onChange);

        var ckFonte = new CheckBox
        {
            Content = "Informação de fonte",
            IsChecked = SettingsService.Current.SseShowSource
        };
        ToolTipService.SetToolTip(ckFonte, "Mostra o caminho da base no rodapé da consulta");
        ckFonte.Checked += (_, _) => Salvar(s => s.SseShowSource = true, onChange);
        ckFonte.Unchecked += (_, _) => Salvar(s => s.SseShowSource = false, onChange);

        var ckBase = new CheckBox
        {
            Content = "Resumo da base",
            IsChecked = SettingsService.Current.SseShowBaseInfo
        };
        ToolTipService.SetToolTip(ckBase, "Mostra \"N cidades · N com NM · base de <data>\" no cabeçalho");
        ckBase.Checked += (_, _) => Salvar(s => s.SseShowBaseInfo = true, onChange);
        ckBase.Unchecked += (_, _) => Salvar(s => s.SseShowBaseInfo = false, onChange);

        var porBusca = new NumberBox
        {
            Header = "Resultados por busca",
            Minimum = MinRows,
            Maximum = MaxRows,
            SmallChange = 10,
            LargeChange = 50,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            Value = MaxRowsAtual,
            Margin = new Thickness(0, 6, 0, 0)
        };
        porBusca.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue)) return;
            var valor = (int)Math.Round(e.NewValue);
            if (valor < MinRows) valor = MinRows;
            if (valor > MaxRows) valor = MaxRows;
            Salvar(s => s.SseMaxRows = valor, onChange);
        };

        painel.Children.Add(ckNm);
        painel.Children.Add(ckFonte);
        painel.Children.Add(ckBase);
        painel.Children.Add(porBusca);
        return painel;
    }

    private static void Salvar(Action<AppSettings> mudar, Action? onChange)
    {
        mudar(SettingsService.Current);
        SettingsService.Save();
        onChange?.Invoke();
    }
}
