using Microsoft.UI.Xaml.Media;

namespace AnalistaHub.Services;

/// <summary>
/// Fonte de ícones do Google (Material Icons, Apache 2.0) empacotada junto do aplicativo.
///
/// O manifesto aponta pelo NOME do ícone — <c>"icon": "material:router"</c> — e o código resolve
/// nome → codepoint pelo mapa que veio no pacote (<c>MaterialIcons-Regular.codepoints</c>). Assim,
/// trocar de ícone não exige recompilar nada, e a fonte viaja no .zip do release: atualização e
/// instalação nova recebem o mesmo conjunto de ícones.
///
/// Arquivo ausente (build que não copiou Assets\Fonts) ou nome desconhecido NUNCA derruba a
/// ferramenta: cai no glifo do sistema, e o gate <c>--check-tools</c> aponta o problema.
/// </summary>
public static class MaterialIcons
{
    /// <summary>Prefixo do campo <c>icon</c> que marca "ícone do Google, por nome".</summary>
    public const string Prefixo = "material:";

    public const string Familia = "Material Icons";
    private const string ArquivoFonte = "Assets/Fonts/MaterialIcons-Regular.ttf";
    private const string ArquivoMapa = "Assets/Fonts/MaterialIcons-Regular.codepoints";

    /// <summary>Glifo genérico do sistema (Segoe MDL2): usado quando o ícone pedido não resolve.</summary>
    public const string GlifoReserva = "\uE8A7";

    private static readonly Lazy<Dictionary<string, string>> Mapa = new(CarregarMapa);
    private static readonly Lazy<FontFamily?> Fonte = new(CarregarFonte);

    public static bool TemPrefixo(string? icon) =>
        !string.IsNullOrWhiteSpace(icon) && icon.StartsWith(Prefixo, StringComparison.OrdinalIgnoreCase);

    /// <summary>Nome do ícone depois do prefixo ("material:router" → "router").</summary>
    public static string Nome(string? icon) =>
        TemPrefixo(icon) ? icon![Prefixo.Length..].Trim() : "";

    public static FontFamily? FamiliaMaterial => Fonte.Value;

    /// <summary>Quantidade de nomes no mapa — só para diagnóstico/gate.</summary>
    public static int TotalNomes => Mapa.Value.Count;

    /// <summary>Caminho da fonte que o app espera encontrar ao lado do executável.</summary>
    public static string CaminhoFonte => Path.Combine(AppContext.BaseDirectory, ArquivoFonte.Replace('/', '\\'));

    /// <summary>Fonte e mapa carregados? Sem isso, "material:..." não tem como renderizar.</summary>
    public static bool Disponivel => Fonte.Value is not null && Mapa.Value.Count > 0;

    /// <summary>Motivo legível quando <see cref="Disponivel"/> é falso.</summary>
    public static string ProblemaDeInstalacao()
    {
        if (!File.Exists(CaminhoFonte)) return "fonte ausente no pacote: " + CaminhoFonte;
        if (Mapa.Value.Count == 0) return "mapa de nomes ausente ou vazio: " + ArquivoMapa;
        return "fonte de ícones não pôde ser carregada";
    }

    /// <summary>Nome do ícone → glifo que a fonte desenha. Falso quando o nome não existe no mapa.</summary>
    public static bool TentarGlifo(string nome, out string glifo)
    {
        glifo = "";
        if (string.IsNullOrWhiteSpace(nome)) return false;
        if (!Mapa.Value.TryGetValue(nome.Trim(), out var codepoint)) return false;
        glifo = codepoint;
        return true;
    }

    /// <summary>Glifo resolvido ou o reserva do sistema — nunca devolve string vazia.</summary>
    public static string Glifo(string nome) =>
        TentarGlifo(nome, out var g) ? g : GlifoReserva;

    private static Dictionary<string, string> CarregarMapa()
    {
        var mapa = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var caminho = Path.Combine(AppContext.BaseDirectory, ArquivoMapa.Replace('/', '\\'));
            if (!File.Exists(caminho)) return mapa;

            foreach (var linha in File.ReadLines(caminho))
            {
                // Formato do arquivo do Google: "nome e5c3" por linha.
                var partes = linha.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (partes.Length < 2) continue;
                if (!int.TryParse(partes[1], System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var codigo))
                    continue;
                if (codigo < 0 || codigo > 0x10FFFF) continue;
                mapa[partes[0]] = char.ConvertFromUtf32(codigo);
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"mapa de ícones Material: {ex.Message}");
        }

        return mapa;
    }

    private static FontFamily? CarregarFonte()
    {
        try
        {
            if (!File.Exists(CaminhoFonte)) return null;
            // WinUI 3: a fonte vem por URI. ms-appx:/// aponta para a pasta do executável
            // (app não empacotado); o "#Material Icons" é o nome da família dentro do .ttf.
            return new FontFamily($"ms-appx:///{ArquivoFonte}#{Familia}");
        }
        catch (Exception ex)
        {
            AppPaths.Log($"fonte Material Icons: {ex.Message}");
            return null;
        }
    }
}
