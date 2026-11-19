using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.5: imagem→PDF.</summary>
public class ImagesToPdfTests
{
    private static List<(double W, double H)> Pages(string pdf)
    {
        using var doc = PdfReader.Open(pdf, PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfSharp.Pdf.PdfPage>().Select(p => (Math.Round(p.Width.Point), Math.Round(p.Height.Point))).ToList();
    }

    [Fact]
    public void EachImage_BecomesOnePage_InTheOrderOfTheList()
    {
        string dir = TestPdfs.TempDir();
        string tall = TestImages.Png(Path.Combine(dir, "alta.png"), 30, 60);
        string wide = TestImages.Png(Path.Combine(dir, "larga.png"), 80, 40);
        string output = Path.Combine(dir, "fotos.pdf");

        int reported = 0;
        ImagesToPdf.Create([tall, wide, tall], ImagePageSize.A4, ImageMargin.Small, output, new SyncProgress(n => reported = n));

        // A4 em pé para a imagem alta, deitado para a larga.
        Assert.Equal([(595, 842), (842, 595), (595, 842)], Pages(output));
        Assert.Equal(3, reported);
        using var pig = UglyToad.PdfPig.PdfDocument.Open(output);
        Assert.All(pig.GetPages(), p => Assert.Single(p.GetImages()));
    }

    [Fact]
    public void SameAsImage_PageHasTheImageSizePlusMargins()
    {
        string dir = TestPdfs.TempDir();
        string png = TestImages.Png(Path.Combine(dir, "a.png"), 200, 100, dpi: 72); // 72 ppp: 1 pixel = 1 ponto
        string output = Path.Combine(dir, "a.pdf");

        ImagesToPdf.Create([png], ImagePageSize.SameAsImage, ImageMargin.None, output);
        Assert.Equal([(200, 100)], Pages(output));

        ImagesToPdf.Create([png], ImagePageSize.SameAsImage, ImageMargin.Large, output);
        Assert.Equal([(Math.Round(200 + 4 * 72 / 2.54), Math.Round(100 + 4 * 72 / 2.54))], Pages(output));
    }

    [Fact]
    public void ImageIsCentered_AndFitsInsideTheMargins()
    {
        string dir = TestPdfs.TempDir();
        string png = TestImages.Png(Path.Combine(dir, "q.png"), 50, 50);
        string output = Path.Combine(dir, "q.pdf");

        ImagesToPdf.Create([png], ImagePageSize.A4, ImageMargin.Small, output);

        using var pig = UglyToad.PdfPig.PdfDocument.Open(output);
        var box = Assert.Single(pig.GetPage(1).GetImages()).Bounds;
        double margin = 72 / 2.54;
        Assert.Equal(595.28 - 2 * margin, box.Width, 1); // quadrada: ocupa toda a largura útil
        Assert.Equal(box.Width, box.Height, 1);
        Assert.Equal(595.28 / 2, (box.Left + box.Right) / 2, 1);
        Assert.Equal(841.89 / 2, (box.Bottom + box.Top) / 2, 1);
    }

    [Fact]
    public void ExifRotation_ReadsTheOrientationFromPhones()
    {
        Assert.Equal(90, ImagesToPdf.ExifRotation(JpegHeaderWithOrientation(6, bigEndian: true)));
        Assert.Equal(270, ImagesToPdf.ExifRotation(JpegHeaderWithOrientation(8, bigEndian: false)));
        Assert.Equal(180, ImagesToPdf.ExifRotation(JpegHeaderWithOrientation(3, bigEndian: true)));
        Assert.Equal(0, ImagesToPdf.ExifRotation(JpegHeaderWithOrientation(1, bigEndian: true)));
        Assert.Equal(0, ImagesToPdf.ExifRotation([0x89, (byte)'P', (byte)'N', (byte)'G'])); // PNG: sem EXIF
        Assert.Equal(0, ImagesToPdf.ExifRotation(JpegHeaderWithOrientation(6, bigEndian: true)[..14])); // cortado: não quebra
    }

    [Fact]
    public void SidewaysPhoto_StandsUp()
    {
        // Foto deitada (paisagem gravada) com a marcação "girar 90°": no PDF ela fica em pé, então a página A4 fica em pé.
        string wallpaper = @"C:\Windows\Web\Wallpaper\Windows\img0.jpg";
        if (!File.Exists(wallpaper)) return;
        string dir = TestPdfs.TempDir();
        byte[] original = File.ReadAllBytes(wallpaper);
        byte[] header = JpegHeaderWithOrientation(6, bigEndian: true);
        string photo = Path.Combine(dir, "celular.jpg");
        File.WriteAllBytes(photo, [.. header, .. original[2..]]); // SOI + EXIF + resto da foto
        string output = Path.Combine(dir, "foto.pdf");

        ImagesToPdf.Create([photo], ImagePageSize.A4, ImageMargin.None, output);

        Assert.Equal([(595, 842)], Pages(output));
        using var pig = UglyToad.PdfPig.PdfDocument.Open(output);
        var box = Assert.Single(pig.GetPage(1).GetImages()).Bounds;
        // A imagem está girada: mede a extensão dela na página pelos cantos.
        double[] xs = [box.TopLeft.X, box.TopRight.X, box.BottomLeft.X, box.BottomRight.X];
        double[] ys = [box.TopLeft.Y, box.TopRight.Y, box.BottomLeft.Y, box.BottomRight.Y];
        double width = xs.Max() - xs.Min(), height = ys.Max() - ys.Min();
        Assert.True(height > width, $"{width} x {height}"); // em pé
        Assert.Equal(841.89, height, 0); // foto em pé e estreita: ocupa toda a altura da página (sem margem)
    }

    /// <summary>Começo de um JPG (SOI) com o bloco EXIF que só tem a marcação de orientação.</summary>
    private static byte[] JpegHeaderWithOrientation(ushort orientation, bool bigEndian)
    {
        var tiff = new List<byte>();
        void U16(int v) { if (bigEndian) { tiff.Add((byte)(v >> 8)); tiff.Add((byte)v); } else { tiff.Add((byte)v); tiff.Add((byte)(v >> 8)); } }
        void U32(int v) { if (bigEndian) { U16(v >> 16); U16(v & 0xFFFF); } else { U16(v & 0xFFFF); U16(v >> 16); } }
        tiff.AddRange(bigEndian ? "MM"u8.ToArray() : "II"u8.ToArray());
        U16(42); U32(8);                     // cabeçalho TIFF; primeiro diretório no byte 8
        U16(1);                              // 1 marcação
        U16(0x0112); U16(3); U32(1); U16(orientation); U16(0);
        U32(0);                              // não há próximo diretório
        int length = 2 + 6 + tiff.Count;
        return [0xFF, 0xD8, 0xFF, 0xE1, (byte)(length >> 8), (byte)length, .. "Exif\0\0"u8.ToArray(), .. tiff];
    }

    /// <summary>IProgress que avisa na hora (o Progress padrão avisa depois, em outra thread).</summary>
    private sealed class SyncProgress(Action<int> report) : IProgress<int>
    {
        public void Report(int value) => report(value);
    }
}
