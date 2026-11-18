using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Tests;

/// <summary>Gera PDFs de teste com páginas numeradas e fáceis de reconhecer.</summary>
public static class TestPdfs
{
    static TestPdfs()
    {
        if (OperatingSystem.IsWindows()) GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public static string TempDir()
    {
        string dir = Path.Combine(Path.GetTempPath(), "PDFStudioTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Cria um PDF com <paramref name="pages"/> páginas com o texto "{prefix} {n}".</summary>
    public static string Create(string path, int pages, string prefix = "Página", int landscapeEvery = 0)
    {
        using var doc = new PdfDocument();
        doc.Info.Title = $"{prefix} - teste";
        // WinAnsi grava o texto legível no conteúdo da página, o que permite conferi-lo nos testes.
        var labelFont = new XFont("Arial", 48, XFontStyleEx.Bold, new XPdfFontOptions(PdfFontEncoding.WinAnsi));
        var smallFont = new XFont("Arial", 14, XFontStyleEx.Regular, new XPdfFontOptions(PdfFontEncoding.WinAnsi));
        XColor[] colors = [XColors.SteelBlue, XColors.IndianRed, XColors.SeaGreen, XColors.DarkOrange, XColors.MediumPurple];
        for (int i = 1; i <= pages; i++)
        {
            var page = doc.AddPage();
            page.Size = PdfSharp.PageSize.A4;
            if (landscapeEvery > 0 && i % landscapeEvery == 0) page.Orientation = PdfSharp.PageOrientation.Landscape;

            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawString($"{prefix} {i}", labelFont, XBrushes.Black,
                new XRect(0, 0, page.Width.Point, page.Height.Point), XStringFormats.Center);
            gfx.DrawRectangle(new XSolidBrush(colors[(i - 1) % colors.Length]), 0, 0, page.Width.Point, 60);
            gfx.DrawString("topo", smallFont, XBrushes.White, new XPoint(20, 38));
        }
        doc.Save(path);
        return path;
    }

    /// <summary>Lê o rótulo (primeiro texto) de cada página, na ordem do arquivo.</summary>
    public static List<string> Labels(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => Texts(ContentReader.ReadContent(p)).FirstOrDefault() ?? "").ToList();
    }

    private static IEnumerable<string> Texts(CObject obj)
    {
        switch (obj)
        {
            case COperator op when op.OpCode.Name is "Tj" or "'" or "\"":
                foreach (var o in op.Operands) if (o is CString s) yield return s.Value;
                break;
            case CSequence seq:
                foreach (var child in seq)
                    foreach (var t in Texts(child)) yield return t;
                break;
        }
    }

    /// <summary>Tamanho de cada página como aparece na tela (pontos, considerando recorte e rotação).</summary>
    public static List<(double Width, double Height)> VisualSizes(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(PDFStudio.Core.PdfOperations.VisualSize).ToList();
    }

    /// <summary>/CropBox de cada página (x1, y1, x2, y2), ou null quando a página não tem recorte.</summary>
    public static List<(double, double, double, double)?> CropBoxes(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>()
            .Select(p => p.Elements.ContainsKey("/CropBox")
                ? ((double, double, double, double)?)(Math.Round(p.CropBox.X1, 3), Math.Round(p.CropBox.Y1, 3), Math.Round(p.CropBox.X2, 3), Math.Round(p.CropBox.Y2, 3))
                : null)
            .ToList();
    }

    /// <summary>Rotação (/Rotate) de cada página, normalizada para 0..359.</summary>
    public static List<int> Rotations(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => ((p.Rotate % 360) + 360) % 360).ToList();
    }
}
