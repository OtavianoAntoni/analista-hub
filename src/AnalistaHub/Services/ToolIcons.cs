using AnalistaHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnalistaHub.Services;

/// <summary>
/// Desenha o ícone de uma ferramenta. Três formas, decididas pelo manifesto:
///   - "icon": "material:router" → ícone da fonte do Google empacotada no app (padrão novo, R10);
///   - "icon": "\uE968"         → glifo Segoe MDL2 Assets (legado, ainda aceito);
///   - "icon": "minha.ico"      → arquivo de imagem ao lado do manifesto (regra R10).
/// Nome desconhecido ou arquivo inválido cai no glifo genérico: nunca deixa a linha sem ícone.
/// </summary>
public static class ToolIcons
{
    private const string FallbackGlyph = MaterialIcons.GlifoReserva;
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// MKT-004: preferência global de tamanho (Configurações → Ícones). Multiplicar aqui faz todas as
    /// telas obedecerem sem cada uma precisar consultar a configuração.
    /// </summary>
    private static double Escala => Math.Clamp(App.Settings.IconScale, 0.5, 2.0);

    /// <summary>Elemento visual do ícone: Image quando há arquivo, FontIcon caso contrário.</summary>
    public static FrameworkElement Create(ToolManifest tool, double size, double? glyphSize = null)
    {
        size *= Escala;
        glyphSize = (glyphSize ?? size / Escala) * Escala;

        var image = LoadFile(tool);
        if (image is not null)
        {
            return new Image
            {
                Source = image,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        if (tool.IconIsMaterial)
            return Material(tool.IconMaterialName, glyphSize ?? size);

        return Glyph(tool.IconIsFile ? FallbackGlyph : tool.IconGlyph, glyphSize ?? size);
    }

    /// <summary>Ícone fixo por glifo do sistema (Início, Configurações…).</summary>
    public static FontIcon Glyph(string glyph, double size, FontFamily? font = null)
    {
        var icon = new FontIcon
        {
            Glyph = string.IsNullOrEmpty(glyph) ? FallbackGlyph : glyph,
            FontSize = size,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };

        // Sem fonte explícita o FontIcon usa o padrão do tema (Segoe Fluent/MDL2) — é o legado.
        if (font is not null) icon.FontFamily = font;
        return icon;
    }

    /// <summary>
    /// Ícone pelo nome da fonte do Google. Cai no glifo do sistema quando o nome não existe no
    /// mapa ou quando a fonte não foi copiada para o pacote (o gate --check-tools aponta os dois).
    /// </summary>
    public static FontIcon Material(string nome, double size)
    {
        var fonte = MaterialIcons.FamiliaMaterial;
        return fonte is not null && MaterialIcons.TentarGlifo(nome, out var glifo)
            ? Glyph(glifo, size, fonte)
            : Glyph(FallbackGlyph, size);
    }

    private static ImageSource? LoadFile(ToolManifest tool)
    {
        if (!tool.IconIsFile) return null;

        var path = tool.ExpandedIconFile;
        lock (Cache)
        {
            if (Cache.TryGetValue(path, out var cached)) return cached;
        }

        ImageSource? source = null;
        try
        {
            if (File.Exists(path))
            {
                var bitmap = new BitmapImage { DecodePixelWidth = 64 };
                bitmap.UriSource = new Uri(path, UriKind.Absolute);
                source = bitmap;
            }
        }
        catch (Exception ex)
        {
            AppPaths.Log($"ícone da ferramenta {tool.Id}: {ex.Message}");
            source = null;
        }

        lock (Cache)
        {
            Cache[path] = source;
        }

        return source;
    }
}
