using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Onde a marca d'água fica em relação ao conteúdo da página.</summary>
public enum WatermarkLayer { Front, Behind }

/// <summary>
/// Opções da marca d'água de texto.
/// <paramref name="Opacity"/>: 0 (invisível) a 1 (sólida).
/// <paramref name="Angle"/>: graus, sentido anti-horário como a página aparece; null = na diagonal da página.
/// <paramref name="FontSize"/>: 0 = automático (o maior que cabe em 80% da página).
/// </summary>
public sealed record WatermarkOptions(
    string Text,
    string FontFamily,
    AnnotationColor Color,
    double Opacity = 0.3,
    double? Angle = null,
    double FontSize = 0,
    bool Bold = true,
    WatermarkLayer Layer = WatermarkLayer.Front);

/// <summary>
/// Etapa 5.5: marca d'água de texto. Ela é gravada no conteúdo da página (não é um comentário), marcada como
/// "artefato" do PDF com a assinatura do PDF Studio — por isso o app consegue encontrá-la e removê-la depois.
/// </summary>
public static class PdfWatermark
{
    /// <summary>
    /// Coloca a marca d'água nas páginas de <paramref name="ranges"/> ("1-3, 5"; null ou vazio = todas).
    /// Com <paramref name="replaceExisting"/>, tira antes as marcas d'água que o PDF Studio já colocou nessas páginas.
    /// Devolve quantas páginas receberam a marca.
    /// </summary>
    public static int Add(string input, WatermarkOptions options, string? ranges, string output, bool replaceExisting = true)
    {
        if (string.IsNullOrWhiteSpace(options.Text)) throw new ArgumentException("Digite o texto da marca d'água.", nameof(options));
        if (options.Opacity is <= 0 or > 1) throw new ArgumentOutOfRangeException(nameof(options), "A opacidade precisa estar entre 0 e 1.");
        var lines = PdfPageContent.SplitLines(options.Text.Trim());
        // A fonte é medida em tamanho 100 e depois ajustada à página.
        var probe = PdfPageContent.CreateFont(new PageTextStyle(options.FontFamily, 100, options.Color, options.Bold));
        int count = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            foreach (int index in Pages(doc, ranges))
            {
                var page = doc.Pages[index];
                if (replaceExisting) PdfPageContent.RemoveArtifacts(page, PdfPageContent.ArtifactKind.Watermark);
                Draw(page, options, lines, probe);
                count++;
            }
        });
        return count;
    }

    /// <summary>Quantas páginas têm marca d'água colocada pelo PDF Studio.</summary>
    public static int Count(string path)
    {
        using var doc = PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Count(p => PdfPageContent.CountArtifacts(p, PdfPageContent.ArtifactKind.Watermark) > 0);
    }

    /// <summary>
    /// Remove as marcas d'água colocadas pelo PDF Studio nas páginas de <paramref name="ranges"/> (null = todas).
    /// Marcas de outros programas não são reconhecidas. Devolve quantas páginas tinham marca.
    /// </summary>
    public static int Remove(string input, string? ranges, string output)
    {
        int count = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            foreach (int index in Pages(doc, ranges))
                if (PdfPageContent.RemoveArtifacts(doc.Pages[index], PdfPageContent.ArtifactKind.Watermark) > 0) count++;
        });
        return count;
    }

    /// <summary>Tamanho de letra usado na página quando o tamanho é automático (para mostrar na tela).</summary>
    public static double AutoFontSize(double viewWidth, double viewHeight, WatermarkOptions options)
    {
        var lines = PdfPageContent.SplitLines(options.Text.Trim());
        var probe = PdfPageContent.CreateFont(new PageTextStyle(options.FontFamily, 100, options.Color, options.Bold));
        return FitSize(viewWidth, viewHeight, lines, probe, AngleFor(viewWidth, viewHeight, options));
    }

    // ---------------- Desenho ----------------

    private static void Draw(PdfPage page, WatermarkOptions options, List<string> lines, XFont probe)
    {
        var geometry = PageGeometry.Of(page);
        double w = geometry.ViewWidth, h = geometry.ViewHeight;
        double angle = AngleFor(w, h, options);
        double size = options.FontSize > 0 ? options.FontSize : FitSize(w, h, lines, probe, angle);
        var font = PdfPageContent.CreateFont(new PageTextStyle(options.FontFamily, size, options.Color, options.Bold));
        var brush = new XSolidBrush(XColor.FromArgb((int)Math.Round(options.Opacity * 255), options.Color.R, options.Color.G, options.Color.B));
        double lineHeight = font.GetHeight();

        PdfPageContent.DrawInView(page, gfx =>
        {
            gfx.TranslateTransform(w / 2, h / 2);
            gfx.RotateTransform(-angle); // na tela o y cresce para baixo: girar "para cima" é ângulo negativo
            double top = -lineHeight * lines.Count / 2;
            for (int i = 0; i < lines.Count; i++)
            {
                if (lines[i].Length == 0) continue;
                gfx.DrawString(lines[i], font, brush, new XRect(-50000, top + i * lineHeight, 100000, lineHeight), XStringFormats.Center);
            }
        }, behind: options.Layer == WatermarkLayer.Behind, artifact: PdfPageContent.ArtifactKind.Watermark);
    }

    /// <summary>Ângulo da diagonal (do canto inferior esquerdo ao superior direito) ou o escolhido.</summary>
    private static double AngleFor(double w, double h, WatermarkOptions options) =>
        options.Angle ?? Math.Atan2(h, w) * 180 / Math.PI;

    /// <summary>O maior tamanho em que o texto girado cabe em 80% da largura e da altura da página.</summary>
    private static double FitSize(double w, double h, List<string> lines, XFont probe, double angle)
    {
        double textW = lines.Max(l => l.Length == 0 ? 0 : XGraphics.CreateMeasureContext(new XSize(1, 1), XGraphicsUnit.Point, XPageDirection.Downwards).MeasureString(l, probe).Width);
        double textH = probe.GetHeight() * lines.Count;
        double rad = angle * Math.PI / 180, cos = Math.Abs(Math.Cos(rad)), sin = Math.Abs(Math.Sin(rad));
        double boxW = textW * cos + textH * sin, boxH = textW * sin + textH * cos;
        if (boxW <= 0 || boxH <= 0) return 12;
        double scale = Math.Min(0.8 * w / boxW, 0.8 * h / boxH);
        return Math.Clamp(100 * scale, 6, 500);
    }

    private static IEnumerable<int> Pages(PdfDocument doc, string? ranges) =>
        string.IsNullOrWhiteSpace(ranges)
            ? Enumerable.Range(0, doc.PageCount)
            : PageRanges.Parse(ranges, doc.PageCount).SelectMany(r => r).Distinct().Order();
}
