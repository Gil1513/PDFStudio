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

    /// <summary>Operandos do primeiro comando "cm" (matriz a b c d e f) da página, ou null.</summary>
    public static double[]? FirstMatrix(string path, int pageIndex)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return FindMatrix(ContentReader.ReadContent(doc.Pages[pageIndex]));
    }

    private static double[]? FindMatrix(CObject obj) => obj switch
    {
        COperator { OpCode.Name: "cm" } op => op.Operands.Select(o => o switch
        {
            CInteger i => (double)i.Value,
            CReal r => r.Value,
            _ => double.NaN,
        }).ToArray(),
        CSequence seq => seq.Select(FindMatrix).FirstOrDefault(m => m is not null),
        _ => null,
    };

    /// <summary>Acrescenta um link (anotação) na primeira página, em coordenadas do PDF.</summary>
    public static void AddLink(string path, double x1, double y1, double x2, double y2)
    {
        using (var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify))
        {
            doc.Pages[0].AddWebLink(new PdfRectangle(new XPoint(x1, y1), new XPoint(x2, y2)), "https://example.com");
            doc.Save(path + ".tmp");
        }
        File.Move(path + ".tmp", path, overwrite: true);
    }

    /// <summary>/Subtype de todas as anotações da primeira página, na ordem.</summary>
    public static List<string> AnnotationSubtypes(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var annots = doc.Pages[0].Elements.GetArray("/Annots");
        return annots is null ? [] : Enumerable.Range(0, annots.Elements.Count)
            .Select(i => annots.Elements.GetDictionary(i)!.Elements.GetName("/Subtype")).ToList();
    }

    private static PdfDictionary Annotation(PdfDocument doc, int index) =>
        doc.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(index)!;

    /// <summary>Números de um array da anotação <paramref name="index"/> da primeira página (ex.: /L).</summary>
    public static double[] AnnotationArray(string path, int index, string key)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var array = Annotation(doc, index).Elements.GetArray(key)!;
        return Enumerable.Range(0, array.Elements.Count).Select(i => array.Elements.GetReal(i)).ToArray();
    }

    /// <summary>Nomes de um array da anotação (ex.: /LE = [/None /OpenArrow]).</summary>
    public static string[] AnnotationNames(string path, int index, string key)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var array = Annotation(doc, index).Elements.GetArray(key)!;
        return Enumerable.Range(0, array.Elements.Count).Select(i => array.Elements.GetName(i)).ToArray();
    }

    /// <summary>Quantidade de números em cada traço do /InkList da primeira anotação.</summary>
    public static int[] InkStrokeLengths(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var ink = Annotation(doc, 0).Elements.GetArray("/InkList")!;
        return Enumerable.Range(0, ink.Elements.Count).Select(i => ink.Elements.GetArray(i)!.Elements.Count).ToArray();
    }

    /// <summary>/Rect da primeira anotação da primeira página (x1, y1, x2, y2).</summary>
    public static double[] FirstAnnotationRect(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var rect = doc.Pages[0].Elements.GetArray("/Annots")!.Elements.GetDictionary(0)!.Elements.GetArray("/Rect")!;
        return Enumerable.Range(0, 4).Select(i => rect.Elements.GetReal(i)).ToArray();
    }

    /// <summary>Rotação (/Rotate) de cada página, normalizada para 0..359.</summary>
    public static List<int> Rotations(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Select(p => ((p.Rotate % 360) + 360) % 360).ToList();
    }
}
