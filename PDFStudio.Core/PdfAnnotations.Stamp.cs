using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

/// <summary>
/// Um carimbo: texto principal (em maiúsculas, negrito), cor e uma linha de detalhe opcional
/// (por exemplo, "Ana Souza · 18/11/2026 14:05"). <see cref="Name"/> é o nome técnico gravado no PDF (/Name).
/// </summary>
public sealed record StampDesign(string Text, AnnotationColor Color, string? Detail = null, string? Name = null);

// Etapa 4.5: carimbos com desenho próprio do PDF Studio.
public static partial class PdfAnnotations
{
    private const double StampFontSize = 18, StampDetailSize = 8, StampPadX = 12, StampPadY = 7, StampGap = 3, StampMinWidth = 90;

    /// <summary>Tamanho do carimbo na página (pontos), para mostrar a prévia antes de colocar.</summary>
    public static (double Width, double Height) StampSize(StampDesign design)
    {
        var (main, detail) = StampTexts(design);
        double width = Math.Max(MeasureBold(main, StampFontSize), detail is null ? 0 : Measurer(StampDetailSize)(detail));
        double height = 2 * StampPadY + StampFontSize + (detail is null ? 0 : StampGap + StampDetailSize);
        return (Math.Max(StampMinWidth, width + 2 * StampPadX), height);
    }

    /// <summary>Coloca o carimbo centralizado em (<paramref name="centerX"/>, <paramref name="centerY"/>) da tela.</summary>
    public static void AddStamp(string input, int pageIndex, double centerX, double centerY, StampDesign design, string author, string output)
    {
        var (main, detail) = StampTexts(design);
        if (main.Length == 0) throw new ArgumentException("Digite o texto do carimbo.", nameof(design));
        var (width, height) = StampSize(design);

        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var geometry = PageGeometry.Of(page);
            double left = Math.Max(0, Math.Min(centerX - width / 2, geometry.ViewWidth - width));
            double top = Math.Max(0, Math.Min(centerY - height / 2, geometry.ViewHeight - height));
            var rect = geometry.ViewRectToPdf(left, top, width, height);

            string contents = detail is null ? design.Text.Trim() : $"{design.Text.Trim()} — {detail}";
            var annot = NewAnnotation(doc, page, "/Stamp", rect, new AnnotationStyle(design.Color), author, contents);
            annot.Elements["/Name"] = new PdfName("/" + (string.IsNullOrWhiteSpace(design.Name) ? "PDFStudio" : design.Name));

            // Desenho: moldura dupla com cantos arredondados, texto principal centralizado e o detalhe embaixo.
            string c = design.Color.Pdf;
            var ops = new StringBuilder($"{c} RG {c} rg ");
            ops.Append($"2.2 w {RoundedRect(1.1, 1.1, width - 2.2, height - 2.2, 6)} S ");
            ops.Append($"0.8 w {RoundedRect(4, 4, width - 8, height - 8, 4)} S ");
            double mainWidth = MeasureBold(main, StampFontSize);
            double mainBaseline = height - StampPadY - StampFontSize * 0.86;
            ops.Append($"BT /HeBo {N(StampFontSize)} Tf {N((width - mainWidth) / 2)} {N(mainBaseline)} Td {PdfLiteral(WinAnsiBytes(main))} Tj ET");
            if (detail is not null)
            {
                double detailWidth = Measurer(StampDetailSize)(detail);
                ops.Append($" BT /Helv {N(StampDetailSize)} Tf {N((width - detailWidth) / 2)} {N(StampPadY + 1)} Td {PdfLiteral(WinAnsiBytes(detail))} Tj ET");
            }

            var fonts = new PdfDictionary(doc);
            fonts.Elements["/HeBo"] = StandardFont(doc, "/Helvetica-Bold");
            fonts.Elements["/Helv"] = StandardFont(doc, "/Helvetica");
            var resources = new PdfDictionary(doc);
            resources.Elements["/Font"] = fonts;
            SetAppearance(doc, annot, width, height, ops.ToString(), 0.9, resources, UprightMatrix(geometry.Rotation));
        });
    }

    private static (string Main, string? Detail) StampTexts(StampDesign design) =>
        (design.Text.Trim().ToUpper(System.Globalization.CultureInfo.GetCultureInfo("pt-BR")),
         string.IsNullOrWhiteSpace(design.Detail) ? null : design.Detail.Trim());

    private static double MeasureBold(string text, double size)
    {
        Measurer(size); // garante a configuração das fontes do Windows
        var font = new XFont("Arial", size, XFontStyleEx.Bold);
        var gfx = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        return gfx.MeasureString(WinAnsiText(text), font).Width;
    }

    private static PdfDictionary StandardFont(PdfDocument doc, string baseFont)
    {
        var font = new PdfDictionary(doc);
        font.Elements["/Type"] = new PdfName("/Font");
        font.Elements["/Subtype"] = new PdfName("/Type1");
        font.Elements["/BaseFont"] = new PdfName(baseFont);
        font.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        return font;
    }

    /// <summary>Retângulo com cantos arredondados (raio <paramref name="r"/>), feito com curvas de Bézier.</summary>
    private static string RoundedRect(double x, double y, double w, double h, double r)
    {
        r = Math.Min(r, Math.Min(w, h) / 2);
        double k = r * 0.5522847498, x2 = x + w, y2 = y + h;
        return $"{N(x + r)} {N(y)} m {N(x2 - r)} {N(y)} l {N(x2 - r + k)} {N(y)} {N(x2)} {N(y + r - k)} {N(x2)} {N(y + r)} c " +
               $"{N(x2)} {N(y2 - r)} l {N(x2)} {N(y2 - r + k)} {N(x2 - r + k)} {N(y2)} {N(x2 - r)} {N(y2)} c " +
               $"{N(x + r)} {N(y2)} l {N(x + r - k)} {N(y2)} {N(x)} {N(y2 - r + k)} {N(x)} {N(y2 - r)} c " +
               $"{N(x)} {N(y + r)} l {N(x)} {N(y + r - k)} {N(x + r - k)} {N(y)} {N(x + r)} {N(y)} c h";
    }
}
