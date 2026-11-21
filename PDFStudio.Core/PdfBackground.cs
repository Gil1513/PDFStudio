using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Como a imagem de fundo ocupa a página.</summary>
public enum BackgroundFit
{
    /// <summary>Estica para cobrir a página inteira (pode deformar).</summary>
    Stretch,
    /// <summary>Mantém a proporção e cobre a página inteira (pode cortar as bordas).</summary>
    Cover,
    /// <summary>Mantém a proporção e cabe inteira, centralizada (pode sobrar espaço).</summary>
    Fit,
}

/// <summary>
/// Fundo da página: uma cor ou uma imagem (.png/.jpg), sempre atrás do conteúdo.
/// <paramref name="Opacity"/>: 0,05 a 1 (1 = cor/imagem forte; menos = mais clara).
/// </summary>
public sealed record BackgroundOptions(
    AnnotationColor Color = default,
    string? ImagePath = null,
    double Opacity = 1,
    BackgroundFit Fit = BackgroundFit.Cover);

/// <summary>
/// Etapa 5.7: fundo das páginas. Fica no começo do conteúdo (atrás de tudo), marcado como artefato do
/// PDF Studio para poder ser trocado ou removido. Obs.: PDFs que pintam a página inteira de branco
/// escondem o fundo (o branco deles fica por cima).
/// </summary>
public static class PdfBackground
{
    public static int Add(string input, BackgroundOptions options, string? ranges, string output)
    {
        if (options.Opacity is < 0.05 or > 1) throw new ArgumentOutOfRangeException(nameof(options), "A intensidade precisa estar entre 5% e 100%.");
        using var image = options.ImagePath is { } path ? PdfPageContent.OpenImage(path) : null;
        int count = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            foreach (int index in Pages(doc, ranges))
            {
                var page = doc.Pages[index];
                PdfPageContent.RemoveArtifacts(page, PdfPageContent.ArtifactKind.Background);
                var geometry = PageGeometry.Of(page);
                double w = geometry.ViewWidth, h = geometry.ViewHeight;
                PdfPageContent.DrawInView(page, gfx =>
                {
                    var whole = new XRect(0, 0, w, h);
                    if (image is null)
                    {
                        var c = options.Color;
                        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb((int)Math.Round(options.Opacity * 255), c.R, c.G, c.B)), whole);
                        return;
                    }
                    gfx.Save();
                    gfx.IntersectClip(whole);
                    gfx.DrawImage(image, Place(image.PointWidth, image.PointHeight, w, h, options.Fit));
                    gfx.Restore();
                    // Imagem mais clara: um véu branco por cima dela (ainda atrás do conteúdo).
                    if (options.Opacity < 1)
                        gfx.DrawRectangle(new XSolidBrush(XColor.FromArgb((int)Math.Round((1 - options.Opacity) * 255), 255, 255, 255)), whole);
                }, behind: true, artifact: PdfPageContent.ArtifactKind.Background);
                count++;
            }
        });
        return count;
    }

    /// <summary>Retângulo (em pontos da tela) onde a imagem é desenhada.</summary>
    public static XRect Place(double imageW, double imageH, double pageW, double pageH, BackgroundFit fit)
    {
        if (fit == BackgroundFit.Stretch || imageW <= 0 || imageH <= 0) return new XRect(0, 0, pageW, pageH);
        double scale = fit == BackgroundFit.Cover ? Math.Max(pageW / imageW, pageH / imageH) : Math.Min(pageW / imageW, pageH / imageH);
        double w = imageW * scale, h = imageH * scale;
        return new XRect((pageW - w) / 2, (pageH - h) / 2, w, h);
    }

    /// <summary>Quantas páginas têm fundo colocado pelo PDF Studio.</summary>
    public static int Count(string path)
    {
        using var doc = PdfLoad.Open(new MemoryStream(File.ReadAllBytes(path)), PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Count(p => PdfPageContent.CountArtifacts(p, PdfPageContent.ArtifactKind.Background) > 0);
    }

    /// <summary>Remove o fundo colocado pelo PDF Studio. Devolve quantas páginas tinham.</summary>
    public static int Remove(string input, string? ranges, string output)
    {
        int count = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            foreach (int index in Pages(doc, ranges))
                if (PdfPageContent.RemoveArtifacts(doc.Pages[index], PdfPageContent.ArtifactKind.Background) > 0) count++;
        });
        return count;
    }

    private static IEnumerable<int> Pages(PdfDocument doc, string? ranges) =>
        string.IsNullOrWhiteSpace(ranges)
            ? Enumerable.Range(0, doc.PageCount)
            : PageRanges.Parse(ranges, doc.PageCount).SelectMany(r => r).Distinct().Order();
}
