using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>"Separar linhas": um bloco de várias linhas vira um item por linha, com o mesmo estilo.</summary>
public class SplitLinesTests
{
    [Fact]
    public void SplitLines_OneBlockPerLine_KeepsStyle()
    {
        var block = new ContentBlock(BlockKind.Text, 25, 100, 300, 50,
            [
                new BlockLine("Primeira linha", 25, 100, 200, 14, 110),
                new BlockLine("Segunda linha mais longa", 25, 120, 300, 14, 130),
                new BlockLine("Terceira", 30, 140, 80, 14, 150),
            ],
            "ABCDEF+Helvetica", "Helvetica", 12, new AnnotationColor(10, 20, 30), Bold: true);

        var lines = PdfContentBlocks.SplitLines(block);

        Assert.Equal(3, lines.Count);
        Assert.Equal(["Primeira linha", "Segunda linha mais longa", "Terceira"], lines.Select(b => b.Text));
        Assert.Equal((30.0, 140.0, 80.0, 14.0), (lines[2].X, lines[2].Y, lines[2].Width, lines[2].Height));
        Assert.All(lines, b => Assert.Equal(("Helvetica", 12.0, true, (byte)10), (b.FontFamily, b.FontSize, b.Bold, b.Color.R)));
    }

    [Fact]
    public void SplitLines_SingleLineOrImage_Unchanged()
    {
        var one = new ContentBlock(BlockKind.Text, 0, 0, 10, 10, [new BlockLine("a", 0, 0, 10, 10, 8)]);
        var image = new ContentBlock(BlockKind.Image, 0, 0, 10, 10, []);
        Assert.Same(one, Assert.Single(PdfContentBlocks.SplitLines(one)));
        Assert.Same(image, Assert.Single(PdfContentBlocks.SplitLines(image)));
    }
}
