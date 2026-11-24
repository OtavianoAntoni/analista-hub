using AnalistaHub.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AnalistaHub.Services;

/// <summary>
/// Desenha o ícone de uma ferramenta. Duas formas, decididas pelo manifesto:
///   - "icon": "\uE968"        → glifo Segoe MDL2 Assets (padrão, não precisa de arquivo);
///   - "icon": "minha.ico"      → arquivo de imagem ao lado do manifesto (regra R10).
/// Arquivo ausente ou inválido cai no glifo genérico: nunca deixa a linha sem ícone.
/// </summary>
public static class ToolIcons
{
    private const string FallbackGlyph = "\uE8A7";
    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Elemento visual do ícone: Image quando há arquivo, FontIcon caso contrário.</summary>
    public static FrameworkElement Create(ToolManifest tool, double size, double? glyphSize = null)
    {
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

        return Glyph(tool.IconIsFile ? FallbackGlyph : tool.IconGlyph, glyphSize ?? size);
    }

    /// <summary>Ícone fixo por glifo (Início, Configurações…).</summary>
    public static FontIcon Glyph(string glyph, double size) => new()
    {
        Glyph = string.IsNullOrEmpty(glyph) ? FallbackGlyph : glyph,
        FontSize = size,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center
    };

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
