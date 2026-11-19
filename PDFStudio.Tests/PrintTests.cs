using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.3: imprimir.</summary>
public class PrintTests
{
    [Fact]
    public void Fit_ScalesAndCenters()
    {
        // A4 (595 × 842) num papel Carta com área 600 × 780: cabe pela altura.
        var p = PrintLayout.Place(595, 842, 10, 10, 600, 780, fit: true);
        Assert.False(p.Rotated);
        Assert.Equal(780, p.Height, 3);
        Assert.Equal(595 * 780 / 842.0, p.Width, 3);
        Assert.Equal(10 + (600 - p.Width) / 2, p.X, 3);
        Assert.Equal(10, p.Y, 3);
    }

    [Fact]
    public void ActualSize_KeepsTheSize()
    {
        var p = PrintLayout.Place(200, 100, 0, 0, 600, 800, fit: false, autoRotate: false);
        Assert.Equal((200.0, 100.0, 200.0, 350.0), (p.Width, p.Height, p.X, p.Y));
    }

    [Fact]
    public void LandscapePage_OnPortraitPaper_IsTurned()
    {
        var p = PrintLayout.Place(842, 595, 0, 0, 600, 800, fit: true);
        Assert.True(p.Rotated);
        Assert.True(p.Height > p.Width); // fica em pé no papel
        Assert.Equal(800, p.Height, 3); // 595 × 842 em pé: cabe pela altura

        Assert.False(PrintLayout.Place(842, 595, 0, 0, 600, 800, fit: true, autoRotate: false).Rotated);
    }

    [Fact]
    public void Pages_FromTheWindowsRanges()
    {
        Assert.Equal([0, 1, 2, 3], PrintLayout.Pages(4, null));
        Assert.Equal([0, 2, 3], PrintLayout.Pages(4, [(1, 1), (3, 9)]));
        Assert.Equal([1, 0], PrintLayout.Pages(4, [(2, 2), (1, 2)])); // sem repetir
    }

    [Fact]
    public void Dpi_GoesDownForBigDocuments()
    {
        Assert.True(PrintLayout.Dpi(10) > PrintLayout.Dpi(80));
        Assert.True(PrintLayout.Dpi(80) > PrintLayout.Dpi(500));
    }

    [Fact]
    public void Permission_ToPrint_IsRead()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.True(PdfPasswords.AllowsPrinting(input));

        string blocked = Path.Combine(dir, "sem-imprimir.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("", "dono123", AllowPrint: false, AllowCopy: true, AllowEdit: true, AllowComments: true), blocked);
        Assert.False(PdfPasswords.AllowsPrinting(blocked));

        string allowed = Path.Combine(dir, "com-senha.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("abrir", "dono123", AllowPrint: true, AllowCopy: false, AllowEdit: false, AllowComments: false), allowed);
        Assert.True(PdfPasswords.AllowsPrinting(allowed, "abrir"));
    }
}
