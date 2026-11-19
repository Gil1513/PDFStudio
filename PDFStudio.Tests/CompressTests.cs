using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.6: comprimir PDF.</summary>
public class CompressTests
{
    /// <summary>PDF com o conteúdo das páginas gravado sem compactação (como alguns programas fazem).</summary>
    private static string Uncompressed(string dir, int pages = 3)
    {
        string path = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), pages);
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Modify);
        doc.Options.NoCompression = true;
        doc.Options.CompressContentStreams = false;
        // Uma miniatura antiga de página e dados de outro programa (lixo que a limpeza tira).
        var thumb = new PdfDictionary(doc);
        thumb.CreateStream(new byte[5000]);
        doc.Internals.AddObject(thumb);
        doc.Pages[0].Elements["/Thumb"] = thumb.Reference;
        doc.Pages[0].Elements["/PieceInfo"] = new PdfDictionary(doc);
        foreach (var page in doc.Pages)
            foreach (var content in page.Contents.Elements.Select(c => (PdfReference)c).Select(r => (PdfDictionary)r.Value))
            {
                byte[] raw = content.Stream.UnfilteredValue;
                content.Elements.Remove("/Filter");
                content.Stream.Value = [.. raw, .. System.Text.Encoding.ASCII.GetBytes(new string(' ', 4000) + "\n")];
            }
        doc.Save(path);
        return path;
    }

    [Fact]
    public void Light_MakesTheFileSmaller_WithoutChangingTheContent()
    {
        string dir = TestPdfs.TempDir();
        string input = Uncompressed(dir);
        string output = Path.Combine(dir, "comprimido.pdf");

        var result = PdfCompressor.Compress(input, output, CompressLevel.Light);

        Assert.Equal(new FileInfo(input).Length, result.Before);
        Assert.Equal(new FileInfo(output).Length, result.After);
        Assert.True(result.After < result.Before * 0.8, $"{result.Before} → {result.After}");
        Assert.Equal(TestPdfs.Labels(input), TestPdfs.Labels(output)); // mesmo texto em cada página
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.False(doc.Pages[0].Elements.ContainsKey("/Thumb"));
        Assert.False(doc.Pages[0].Elements.ContainsKey("/PieceInfo"));
    }

    [Fact]
    public void Medium_ShrinksBigPhotos_UsingTheGivenShrinker()
    {
        string dir = TestPdfs.TempDir();
        string png = TestImages.Png(Path.Combine(dir, "foto.png"), 1200, 900);
        string input = Path.Combine(dir, "fotos.pdf");
        ImagesToPdf.Create([png], ImagePageSize.A4, ImageMargin.None, input);
        string output = Path.Combine(dir, "menor.pdf");

        PdfImageData? received = null;
        (int, int)? asked = null;
        byte[] fakeJpeg = [0xFF, 0xD8, 0xFF, 0xE0, 1, 2, 3, 0xFF, 0xD9];
        var result = PdfCompressor.Compress(input, output, CompressLevel.Medium, (image, maxSide, quality) =>
        {
            received = image;
            asked = (maxSide, quality);
            return new ShrunkImage(fakeJpeg, 600, 450);
        });

        Assert.Equal(1, result.ImagesReduced);
        Assert.NotNull(received);
        Assert.False(received!.IsJpeg); // PNG vira pixels crus para o redutor
        Assert.Equal((1200, 900, 3), (received.Width, received.Height, received.Components));
        Assert.Equal(1200 * 900 * 3, received.Data.Length);
        Assert.Equal(PdfCompressor.Settings(CompressLevel.Medium), asked);

        var image = Images(output).Single();
        Assert.Equal("/DCTDecode", image.Elements.GetName("/Filter"));
        Assert.Equal(600, image.Elements.GetInteger("/Width"));
        Assert.Equal(450, image.Elements.GetInteger("/Height"));
        Assert.Equal(fakeJpeg, image.Stream.Value);
    }

    [Fact]
    public void Photo_IsKept_WhenTheResultIsNotSmaller_OrTheImageIsSmall()
    {
        string dir = TestPdfs.TempDir();
        string big = TestImages.Png(Path.Combine(dir, "grande.png"), 1000, 800);
        string small = TestImages.Png(Path.Combine(dir, "pequena.png"), 100, 80);
        string input = Path.Combine(dir, "fotos.pdf");
        ImagesToPdf.Create([big, small], ImagePageSize.A4, ImageMargin.None, input);
        string output = Path.Combine(dir, "menor.pdf");
        long before = Images(input).Sum(i => (long)i.Stream.Value.Length);

        int calls = 0;
        var result = PdfCompressor.Compress(input, output, CompressLevel.Strong, (image, _, _) =>
        {
            calls++;
            return new ShrunkImage(new byte[image.Data.Length * 2], 10, 10); // "reduzida" ficou maior
        });

        Assert.Equal(1, calls); // a pequena nem é oferecida
        Assert.Equal(0, result.ImagesReduced);
        Assert.Equal(before, Images(output).Sum(i => (long)i.Stream.Value.Length));
        Assert.All(Images(output), i => Assert.NotEqual("/DCTDecode", i.Elements.GetName("/Filter")));
    }

    [Fact]
    public void Light_NeverTouchesImages()
    {
        string dir = TestPdfs.TempDir();
        string png = TestImages.Png(Path.Combine(dir, "foto.png"), 1200, 900);
        string input = Path.Combine(dir, "fotos.pdf");
        ImagesToPdf.Create([png], ImagePageSize.A4, ImageMargin.None, input);

        var result = PdfCompressor.Compress(input, Path.Combine(dir, "x.pdf"), CompressLevel.Light,
            (_, _, _) => throw new InvalidOperationException("não devia ser chamado"));
        Assert.Equal(0, result.ImagesReduced);
    }

    private static List<PdfDictionary> Images(string path)
    {
        var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Internals.GetAllObjects().OfType<PdfDictionary>()
            .Where(d => d.Elements.GetName("/Subtype") == "/Image" && d.Elements.GetName("/Type") != "/Mask").ToList();
    }
}
