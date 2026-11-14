using PDFStudio.Core;

namespace PDFStudio.Tests;

public class OrganizeTests
{
    [Fact]
    public void Reorganize_ChangesOrderAndRotation()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 4);
        string output = Path.Combine(dir, "org.pdf");

        PdfOperations.Reorganize(input, [new(3), new(0, 90), new(2, 180), new(1, -90)], output);

        Assert.Equal(["Página 4", "Página 1", "Página 3", "Página 2"], TestPdfs.Labels(output));
        Assert.Equal([0, 90, 180, 270], TestPdfs.Rotations(output));
    }

    [Fact]
    public void Reorganize_AddsToExistingRotation()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string once = Path.Combine(dir, "1.pdf"), twice = Path.Combine(dir, "2.pdf");

        PdfOperations.Reorganize(input, [new(0, 270)], once);
        PdfOperations.Reorganize(once, [new(0, 180)], twice);

        Assert.Equal([90], TestPdfs.Rotations(twice)); // 270 + 180 = 450 → 90
    }

    [Fact]
    public void Reorganize_CanOverwriteInput()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);

        PdfOperations.Reorganize(input, [new(2), new(1), new(0)], input);

        Assert.Equal(["Página 3", "Página 2", "Página 1"], TestPdfs.Labels(input));
    }

    [Fact]
    public void Reorganize_KeepsLandscapePages()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 5, landscapeEvery: 5);
        string output = Path.Combine(dir, "org.pdf");

        PdfOperations.Reorganize(input, [new(4), new(0)], output);

        Assert.Equal(["Página 5", "Página 1"], TestPdfs.Labels(output));
    }

    [Fact]
    public void Reorganize_RejectsInvalidRotation()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentException>(() => PdfOperations.Reorganize(input, [new(0, 45)], Path.Combine(dir, "x.pdf")));
    }
}
