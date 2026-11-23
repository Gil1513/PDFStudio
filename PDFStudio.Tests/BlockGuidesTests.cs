using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Guias ao arrastar: alinhar começo, centro e fim com outros itens e espaço igual ao dos vizinhos.</summary>
public class BlockGuidesTests
{
    // Dois textos empilhados com 10 pt de espaço entre eles.
    private static readonly GuideBox First = new(50, 100, 200, 20);
    private static readonly GuideBox Second = new(50, 130, 150, 20);

    [Fact]
    public void NearTheSameSpace_SnapsAndMarksBothSpacesEqual()
    {
        var moving = new GuideBox(53, 162, 120, 20); // 12 pt abaixo do segundo, começo 3 pt para a direita
        var r = PdfBlockGuides.Snap(moving, [First, Second], tolerance: 4);

        Assert.Equal(-3, r.Dx, 3);  // começo alinhado com os de cima
        Assert.Equal(-2, r.Dy, 3);  // mesmo espaço (10 pt)
        Assert.Contains(r.Lines, l => l.Vertical && Math.Abs(l.At - 50) < 0.01);
        var gap = Assert.Single(r.Gaps, g => !g.Reference);
        Assert.True(gap.Equal);
        Assert.Equal(10, gap.Size, 3);
        var reference = Assert.Single(r.Gaps, g => g.Reference);
        Assert.Equal(120, reference.Top, 3);
        Assert.Equal(130, reference.Bottom, 3);
    }

    [Fact]
    public void FarFromTheSameSpace_ShowsTheMeasureWithoutSnapping()
    {
        var moving = new GuideBox(80, 175, 60, 20); // 25 pt abaixo
        var r = PdfBlockGuides.Snap(moving, [First, Second], tolerance: 4);

        Assert.Equal(0, r.Dx);
        Assert.Equal(0, r.Dy);
        var gap = Assert.Single(r.Gaps);
        Assert.False(gap.Equal);
        Assert.Equal(25, gap.Size, 3);
    }

    [Fact]
    public void RightEdgeAndCenter_AlsoAlign()
    {
        // Fim (borda direita) a 2 pt do fim do primeiro texto (250).
        var right = PdfBlockGuides.Snap(new GuideBox(132, 300, 120, 20), [First], tolerance: 4);
        Assert.Equal(-2, right.Dx, 3);
        Assert.Contains(right.Lines, l => l.Vertical && Math.Abs(l.At - 250) < 0.01);

        // Centro a 1 pt do centro do primeiro (150).
        var center = PdfBlockGuides.Snap(new GuideBox(111, 300, 80, 20), [First], tolerance: 4);
        Assert.Equal(-1, center.Dx, 3);
        Assert.Contains(center.Lines, l => l.Vertical && Math.Abs(l.At - 150) < 0.01);
    }

    [Fact]
    public void BetweenTwo_SnapsToTheMiddle()
    {
        var top = new GuideBox(50, 100, 200, 20);
        var bottom = new GuideBox(50, 200, 200, 20);
        var r = PdfBlockGuides.Snap(new GuideBox(50, 148, 200, 20), [top, bottom], tolerance: 4);

        Assert.Equal(2, r.Dy, 3); // 30 pt em cima e embaixo
        Assert.Equal(2, r.Gaps.Count(g => g.Equal && !g.Reference));
    }

    [Fact]
    public void ZeroTolerance_NeverMoves()
    {
        var r = PdfBlockGuides.Snap(new GuideBox(53, 162, 120, 20), [First, Second], tolerance: 0);
        Assert.Equal(0, r.Dx);
        Assert.Equal(0, r.Dy);
        Assert.Empty(r.Lines);
    }
}
