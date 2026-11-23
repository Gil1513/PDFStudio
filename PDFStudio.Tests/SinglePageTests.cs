using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Ações do menu do botão direito na página: mover e girar uma página.</summary>
public class SinglePageTests
{
    [Fact]
    public void MovePage_UpAndDown()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 4);

        PdfOperations.MovePage(input, 2, 1, input); // página 3 sobe
        Assert.Equal(["Página 1", "Página 3", "Página 2", "Página 4"], TestPdfs.Labels(input));

        PdfOperations.MovePage(input, 0, 1, input); // página 1 desce
        Assert.Equal(["Página 3", "Página 1", "Página 2", "Página 4"], TestPdfs.Labels(input));

        PdfOperations.MovePage(input, 3, 0, input); // última vai para o início
        Assert.Equal(["Página 4", "Página 3", "Página 1", "Página 2"], TestPdfs.Labels(input));
    }

    [Fact]
    public void MovePage_RejectsOutside()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        string output = Path.Combine(dir, "out.pdf");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfOperations.MovePage(input, 0, -1, output));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfOperations.MovePage(input, 1, 2, output));
    }

    [Fact]
    public void RotatePage_OnlyThatPage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);

        PdfOperations.RotatePage(input, 1, 90, input);
        PdfOperations.RotatePage(input, 1, -90, input);
        PdfOperations.RotatePage(input, 2, -90, input);

        Assert.Equal([0, 0, 270], TestPdfs.Rotations(input));
        Assert.Equal(["Página 1", "Página 2", "Página 3"], TestPdfs.Labels(input));
    }
}
