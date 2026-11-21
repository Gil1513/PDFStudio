using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.4: PDF→imagem (escolha das páginas, tamanho e nomes dos arquivos).</summary>
public class PageImagesTests
{
    [Fact]
    public void Pages_EmptyMeansAll_AndRangesComeInOrderWithoutRepeating()
    {
        Assert.Equal([0, 1, 2, 3], PageImages.Pages("", 4));
        Assert.Equal([0, 1, 2, 3], PageImages.Pages(null, 4));
        Assert.Equal([0, 2, 3], PageImages.Pages("4, 1, 3-4", 4));
        Assert.Throws<FormatException>(() => PageImages.Pages("9", 4));
    }

    [Fact]
    public void ScreenSize_HugePageIsShrunk_NormalPageIsNot()
    {
        Assert.Equal((1587u, 2245u), PageImages.ScreenSize(793.3, 1122.7, 2)); // A4 com zoom: sem mudança
        var (w, h) = PageImages.ScreenSize(200 * 96, 200 * 96, 4); // 200 × 200 polegadas, zoom 4×
        Assert.True((long)w * h <= PageImages.MaxScreenPixels);
        Assert.Equal(w, h); // mesma proporção
        var (tw, th) = PageImages.ScreenSize(100000, 10, 1); // faixa comprida
        Assert.True(tw <= PageImages.MaxSide && th >= 1);
    }

    [Fact]
    public void PixelSize_FollowsTheResolution()
    {
        // A4 em DIPs (595 × 842 pontos = 793,3 × 1122,7 DIPs).
        Assert.Equal((793u, 1123u), PageImages.PixelSize(793.3, 1122.7, 96));
        Assert.Equal((2479u, 3508u), PageImages.PixelSize(793.3, 1122.7, 300)); // o A4 clássico em 300 dpi
    }

    [Fact]
    public void PixelSize_HugePage_IsReducedKeepingTheShape()
    {
        var (w, h) = PageImages.PixelSize(20000, 5000, 300);
        Assert.Equal((uint)PageImages.MaxSide, w);
        Assert.Equal(2500u, h);
    }

    [Fact]
    public void FileName_HasThePageNumber_PaddedToKeepTheOrder()
    {
        Assert.Equal("contrato-pagina-03.png", PageImages.FileName("contrato.pdf", 3, 9, PageImageFormat.Png));
        Assert.Equal("contrato-pagina-007.jpg", PageImages.FileName("contrato.pdf", 7, 120, PageImageFormat.Jpeg));
        Assert.Equal("documento-pagina-01.png", PageImages.FileName(".pdf", 1, 1, PageImageFormat.Png));
    }

    [Fact]
    public void FreePath_NeverOverwrites()
    {
        string dir = TestPdfs.TempDir();
        Assert.Equal(Path.Combine(dir, "a-pagina-01.png"), PageImages.FreePath(dir, "a-pagina-01.png"));
        File.WriteAllText(Path.Combine(dir, "a-pagina-01.png"), "");
        File.WriteAllText(Path.Combine(dir, "a-pagina-01 (2).png"), "");
        Assert.Equal(Path.Combine(dir, "a-pagina-01 (3).png"), PageImages.FreePath(dir, "a-pagina-01.png"));
    }
}
