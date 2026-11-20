using PDFStudio.Core;
using UglyToad.PdfPig;

namespace PDFStudio.Tests;

/// <summary>Tela de impressão própria: ordem das folhas, encaixe na folha, configuração e envio direto.</summary>
public class DirectPrintTests
{
    [Fact]
    public void Sequence_CollatedCopies()
    {
        Assert.Equal([0, 1, 2, 0, 1, 2], PrintLayout.Sequence([0, 1, 2], 2, collate: true, duplex: false));
        // Frente e verso: a 2ª cópia começa numa folha nova (verso em branco), mas não sobra branco no fim.
        Assert.Equal([0, 1, 2, null, 0, 1, 2], PrintLayout.Sequence([0, 1, 2], 2, collate: true, duplex: true));
        Assert.Equal([0, 1, 0, 1], PrintLayout.Sequence([0, 1], 2, collate: true, duplex: true));
    }

    [Fact]
    public void Sequence_UncollatedCopies()
    {
        Assert.Equal([0, 0, 1, 1, 2, 2], PrintLayout.Sequence([0, 1, 2], 2, collate: false, duplex: false));
        // Frente e verso sem agrupar: repete a folha (1|2), depois a folha (3|branco).
        Assert.Equal([0, 1, 0, 1, 2, null, 2], PrintLayout.Sequence([0, 1, 2], 2, collate: false, duplex: true));
    }

    [Fact]
    public void Sequence_EmptyOrOneCopy()
    {
        Assert.Empty(PrintLayout.Sequence([], 3, true, false));
        Assert.Empty(PrintLayout.Sequence([0], 0, true, false));
        Assert.Equal([4, 2], PrintLayout.Sequence([4, 2], 1, collate: false, duplex: true));
    }

    [Fact]
    public void Parity_KeepsOddOrEvenPageNumbers()
    {
        int[] pages = [0, 1, 2, 3, 4];
        Assert.Equal([0, 2, 4], PrintLayout.Parity(pages, PageParity.Odd)); // páginas 1, 3, 5
        Assert.Equal([1, 3], PrintLayout.Parity(pages, PageParity.Even));
        Assert.Equal(pages, PrintLayout.Parity(pages, PageParity.All));
    }

    [Fact]
    public void PlaceOnSheet_FitAndActualSize()
    {
        // A4 a 600 dpi com margem de 0,25" (150 px) em cada lado.
        var sheet = new SheetMetrics(600, 600, 4960, 7016, 150, 150, 4660, 6716);
        double w = 595 / 72.0, h = 842 / 72.0;

        var fit = PrintLayout.PlaceOnSheet(sheet, w, h, fit: true);
        Assert.False(fit.Rotated);
        Assert.True(fit.Width <= 4660 + 0.01 && fit.Height <= 6716 + 0.01);
        Assert.True(fit.X >= -0.01 && fit.Y >= -0.01);

        var real = PrintLayout.PlaceOnSheet(sheet, w, h, fit: false);
        Assert.Equal(w * 600, real.Width, 3);
        // Centralizada no papel inteiro: o zero do desenho fica 150 px para dentro.
        Assert.Equal((4960 - w * 600) / 2 - 150, real.X, 3);

        var landscape = PrintLayout.PlaceOnSheet(sheet, h, w, fit: true);
        Assert.True(landscape.Rotated);
    }

    [Fact]
    public void DevMode_SetsFieldsAndFlags()
    {
        var dm = new PrintDevMode(new byte[220]);
        Assert.Equal(0, dm.PaperSize);
        Assert.Equal(1, dm.Duplex); // sem informação = só frente

        dm.PaperSize = 9;
        dm.Copies = 1;
        dm.Duplex = 2;
        dm.Color = false;
        dm.Collate = true;
        Assert.Equal(9, dm.PaperSize);
        Assert.Equal(2, dm.Duplex);
        Assert.False(dm.Color);
        Assert.True(dm.Collate);
        Assert.Equal(9, BitConverter.ToInt16(dm.Bytes, 78));
        uint fields = BitConverter.ToUInt32(dm.Bytes, 72);
        Assert.Equal(0x2u | 0x100 | 0x1000 | 0x800 | 0x8000, fields);

        var copy = dm.Clone();
        copy.PaperSize = 1;
        Assert.Equal(9, dm.PaperSize);
        Assert.Throws<ArgumentException>(() => new PrintDevMode(new byte[50]));
    }

    [Fact]
    public void Bitmap_RotateGrayAndBgr()
    {
        // 2 × 1: vermelho, azul (BGRA).
        byte[] img = [0, 0, 255, 255, 255, 0, 0, 255];
        byte[] r = PrintBitmap.Rotate90(img, 2, 1); // vira 1 × 2: vermelho em cima, azul embaixo
        Assert.Equal(img, r);
        // 1 × 2 (vermelho em cima, azul embaixo) girado no horário = 2 × 1 com azul à esquerda.
        byte[] r2 = PrintBitmap.Rotate90(img, 1, 2);
        Assert.Equal(new byte[] { 255, 0, 0, 255, 0, 0, 255, 255 }, r2);

        byte[] g = (byte[])img.Clone();
        PrintBitmap.ToGray(g);
        Assert.Equal(g[0], g[1]);
        Assert.Equal(g[1], g[2]);
        Assert.Equal(255, g[3]);
        Assert.Equal(76, g[0]); // vermelho puro: 0,299 × 255

        byte[] bgr = PrintBitmap.ToBgr24(img, 2, 1, out int stride);
        Assert.Equal(8, stride); // 6 bytes completados até 8
        Assert.Equal(new byte[] { 0, 0, 255, 255, 0, 0, 0, 0 }, bgr);
    }

    /// <summary>
    /// Imprime de verdade, mas só na impressora "Microsoft Print to PDF", gravando num arquivo temporário (nada vai
    /// para papel). Se ela não estiver instalada, o teste não faz nada.
    /// </summary>
    [Fact]
    public void PrintToPdf_WritesOnePagePerSheet()
    {
        if (!OperatingSystem.IsWindows()) return;
        var printer = WindowsPrinters.List().FirstOrDefault(p => p.Name == "Microsoft Print to PDF");
        if (printer is null) return;

        var dm = WindowsPrinters.DefaultDevMode(printer.Name);
        var caps = WindowsPrinters.Capabilities(printer, dm);
        Assert.NotEmpty(caps.Papers);
        if (caps.Papers.FirstOrDefault(p => p.Id == 9) is { } a4) dm.PaperSize = a4.Id;
        dm = WindowsPrinters.Validate(printer.Name, dm);
        var measured = WindowsPrinters.Measure(printer.Name, dm);
        Assert.True(measured.DpiX > 0 && measured.PhysicalWidth > 0);

        string output = Path.Combine(TestPdfs.TempDir(), "impresso.pdf");
        using (var session = WindowsPrintSession.Start(printer.Name, dm, "Teste PDF Studio", output))
        {
            var sheet = session.Sheet;
            var place = PrintLayout.PlaceOnSheet(sheet, 595 / 72.0, 842 / 72.0, fit: true);
            int w = 60, h = 85;
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 40; bgra[i + 1] = 120; bgra[i + 2] = 200; bgra[i + 3] = 255; }
            session.AddImageSheet(bgra, w, h, (int)place.X, (int)place.Y, (int)place.Width, (int)place.Height);
            session.AddBlankSheet();
            session.AddImageSheet(bgra, w, h, 0, 0, 300, 300);
            session.Finish();
        }

        // A fila da impressora grava o arquivo logo depois; espera até 30 s.
        var until = DateTime.UtcNow.AddSeconds(30);
        int pages = -1;
        while (DateTime.UtcNow < until)
        {
            try
            {
                if (File.Exists(output) && new FileInfo(output).Length > 0)
                {
                    using var doc = PdfDocument.Open(output);
                    pages = doc.NumberOfPages;
                    break;
                }
            }
            catch (IOException) { }
            catch (UglyToad.PdfPig.Core.PdfDocumentFormatException) { }
            Thread.Sleep(300);
        }
        Assert.Equal(3, pages);
    }

    [Fact]
    public void Session_DisposedWithoutFinish_Cancels()
    {
        if (!OperatingSystem.IsWindows()) return;
        var printer = WindowsPrinters.List().FirstOrDefault(p => p.Name == "Microsoft Print to PDF");
        if (printer is null) return;
        string output = Path.Combine(TestPdfs.TempDir(), "cancelado.pdf");
        using (var session = WindowsPrintSession.Start(printer.Name, null, "Teste cancelado", output))
            session.AddBlankSheet(); // sem Finish: cancela
        Thread.Sleep(1500);
        Assert.False(File.Exists(output) && new FileInfo(output).Length > 0);
    }
}
