using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>
/// Textos de cabeçalho e rodapé (esquerda, centro, direita). Podem usar:
/// <c>{n}</c> = número da página, <c>{total}</c> = total de páginas, <c>{data}</c> = data de hoje.
/// <paramref name="MarginPt"/>: distância da borda da página (em pontos).
/// <paramref name="StartNumber"/>: número da primeira página do documento.
/// </summary>
public sealed record HeaderFooterOptions(
    string HeaderLeft = "", string HeaderCenter = "", string HeaderRight = "",
    string FooterLeft = "", string FooterCenter = "", string FooterRight = "",
    string FontFamily = "Arial", double FontSize = 10, AnnotationColor Color = default,
    double MarginPt = 28, int StartNumber = 1)
{
    public bool HasHeader => HeaderLeft.Trim().Length + HeaderCenter.Trim().Length + HeaderRight.Trim().Length > 0;
    public bool HasFooter => FooterLeft.Trim().Length + FooterCenter.Trim().Length + FooterRight.Trim().Length > 0;
}

/// <summary>
/// Etapa 5.6: número de página, cabeçalho e rodapé. Como a marca d'água, ficam no conteúdo da página
/// marcados como artefatos do PDF Studio, e por isso podem ser trocados ou removidos depois.
/// </summary>
public static class PdfHeaderFooter
{
    /// <summary>
    /// Coloca cabeçalho/rodapé nas páginas de <paramref name="ranges"/> (null = todas), trocando os que o
    /// PDF Studio já tinha colocado nelas. Devolve quantas páginas foram alteradas.
    /// </summary>
    public static int Add(string input, HeaderFooterOptions options, string? ranges, string output, DateTime? today = null)
    {
        if (!options.HasHeader && !options.HasFooter) throw new ArgumentException("Escreva ao menos um texto de cabeçalho ou rodapé.", nameof(options));
        if (options.MarginPt < 0) throw new ArgumentOutOfRangeException(nameof(options), "A distância da borda não pode ser negativa.");
        var font = PdfPageContent.CreateFont(new PageTextStyle(options.FontFamily, options.FontSize, options.Color));
        var brush = new XSolidBrush(XColor.FromArgb(options.Color.R, options.Color.G, options.Color.B));
        string date = (today ?? DateTime.Today).ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);
        int count = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            int total = doc.PageCount + options.StartNumber - 1;
            foreach (int index in Pages(doc, ranges))
            {
                var page = doc.Pages[index];
                RemoveFrom(page);
                string Fill(string text) => Expand(text, index + options.StartNumber, total, date);
                var geometry = PageGeometry.Of(page);
                double w = geometry.ViewWidth, h = geometry.ViewHeight, m = options.MarginPt;
                double lineHeight = font.GetHeight();
                if (options.HasHeader)
                    PdfPageContent.DrawInView(page, gfx => DrawRow(gfx, font, brush, w, m, m,
                        Fill(options.HeaderLeft), Fill(options.HeaderCenter), Fill(options.HeaderRight)),
                        artifact: PdfPageContent.ArtifactKind.Header);
                if (options.HasFooter)
                    PdfPageContent.DrawInView(page, gfx => DrawRow(gfx, font, brush, w, m, h - m - lineHeight,
                        Fill(options.FooterLeft), Fill(options.FooterCenter), Fill(options.FooterRight)),
                        artifact: PdfPageContent.ArtifactKind.Footer);
                count++;
            }
        });
        return count;
    }

    /// <summary>Quantas páginas têm cabeçalho ou rodapé colocado pelo PDF Studio.</summary>
    public static int Count(string path)
    {
        using var doc = PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Count(p =>
            PdfPageContent.CountArtifacts(p, PdfPageContent.ArtifactKind.Header) + PdfPageContent.CountArtifacts(p, PdfPageContent.ArtifactKind.Footer) > 0);
    }

    /// <summary>Remove cabeçalhos e rodapés colocados pelo PDF Studio. Devolve quantas páginas tinham.</summary>
    public static int Remove(string input, string? ranges, string output)
    {
        int count = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            foreach (int index in Pages(doc, ranges))
                if (RemoveFrom(doc.Pages[index]) > 0) count++;
        });
        return count;
    }

    /// <summary>Troca {n}, {total} e {data} (sem diferenciar maiúsculas).</summary>
    public static string Expand(string text, int number, int total, string date) =>
        text.Trim()
            .Replace("{n}", number.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{total}", total.ToString(CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{data}", date, StringComparison.OrdinalIgnoreCase);

    private static int RemoveFrom(PdfPage page) =>
        PdfPageContent.RemoveArtifacts(page, PdfPageContent.ArtifactKind.Header)
        + PdfPageContent.RemoveArtifacts(page, PdfPageContent.ArtifactKind.Footer)
        + PdfPageContent.RemoveArtifacts(page, PdfPageContent.ArtifactKind.PageNumber);

    private static void DrawRow(XGraphics gfx, XFont font, XBrush brush, double pageWidth, double margin, double top,
        string left, string center, string right)
    {
        double lineHeight = font.GetHeight();
        var row = new XRect(margin, top, Math.Max(1, pageWidth - 2 * margin), lineHeight);
        if (left.Length > 0) gfx.DrawString(left, font, brush, row, XStringFormats.TopLeft);
        if (center.Length > 0) gfx.DrawString(center, font, brush, row, XStringFormats.TopCenter);
        if (right.Length > 0) gfx.DrawString(right, font, brush, row, XStringFormats.TopRight);
    }

    private static IEnumerable<int> Pages(PdfDocument doc, string? ranges) =>
        string.IsNullOrWhiteSpace(ranges)
            ? Enumerable.Range(0, doc.PageCount)
            : PageRanges.Parse(ranges, doc.PageCount).SelectMany(r => r).Distinct().Order();
}
