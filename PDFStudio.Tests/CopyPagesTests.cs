using PDFStudio.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Tests;

/// <summary>Copiar páginas: cópias idênticas, na posição escolhida.</summary>
public class CopyPagesTests
{
    private static readonly AnnotationStyle Red = new(new AnnotationColor(255, 0, 0), LineWidth: 3);

    [Fact]
    public void Copies_GoWhereChosen_InTheOrderTyped()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 4, prefix: "P");
        string output = Path.Combine(dir, "b.pdf");

        Assert.Equal(8, PdfOperations.CopyPages(input, "2, 1", 2, 3, output)); // depois da página 3
        Assert.Equal(["P 1", "P 2", "P 3", "P 2", "P 1", "P 2", "P 1", "P 4"], TestPdfs.Labels(output));

        Assert.Equal(5, PdfOperations.CopyPages(input, "4", 1, 0, output)); // no início
        Assert.Equal(["P 4", "P 1", "P 2", "P 3", "P 4"], TestPdfs.Labels(output));
        Assert.Equal(4, PdfOperations.GetPageCount(input)); // o original não muda
    }

    [Fact]
    public void Copy_KeepsContentAndSize_AndCommentsAreIndependent()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 2, prefix: "P", landscapeEvery: 2);
        PdfAnnotations.AddShape(input, 1, AnnotationKind.Rectangle, 100, 150, 200, 80, Red, "Ana", input, "Revisar");
        string output = Path.Combine(dir, "b.pdf");
        PdfOperations.CopyPages(input, "2", 1, 2, output);

        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var original = doc.Pages[1];
        var copy = doc.Pages[2];
        Assert.Equal(original.Width.Point, copy.Width.Point);
        Assert.Equal(original.Height.Point, copy.Height.Point);
        Assert.Equal(original.Orientation, copy.Orientation);
        Assert.Equal(Content(original), Content(copy)); // mesmo desenho: letras, fontes, posições

        var comments = PdfAnnotations.List(output);
        Assert.Equal(2, comments.Count);
        Assert.Equal([1, 2], comments.Select(c => c.PageIndex));
        Assert.All(comments, c => Assert.Equal("Revisar", c.Contents));
        // Objetos diferentes: mexer num comentário não mexe no outro.
        var a = original.Elements.GetArray("/Annots")!.Elements[0] as PdfReference;
        var b = copy.Elements.GetArray("/Annots")!.Elements[0] as PdfReference;
        Assert.NotEqual(a!.ObjectNumber, b!.ObjectNumber);
    }

    [Fact]
    public void Copy_LinkToAnotherPage_StillGoesToThatPage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 4, prefix: "P");
        PdfAnnotations.AddLink(input, 0, 50, 300, 80, 20, LinkTarget.ToPage(3), input); // página 1 → página 4
        string output = Path.Combine(dir, "b.pdf");
        PdfOperations.CopyPages(input, "1", 1, 1, output); // cópia vira a página 2; a página 4 vira a 5

        var links = PdfAnnotations.ListLinks(output);
        Assert.Equal(2, links.Count);
        Assert.All(links, l => Assert.Equal(LinkTarget.ToPage(4), l.Target));
        Assert.Equal(5, PdfOperations.GetPageCount(output));
    }

    [Fact]
    public void Copy_FormField_GetsItsOwnName_AndSignatureIsNotCopied()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 2, prefix: "P");
        PdfSignatureFields.Add(input, 0, 100, 200, 180, 50, "Cliente", input);
        string output = Path.Combine(dir, "b.pdf");
        PdfOperations.CopyPages(input, "1", 2, 2, output);

        var fields = PdfSignatureFields.List(output).OrderBy(f => f.Page).ToList();
        Assert.Equal(["Cliente", "Cliente (cópia 2)", "Cliente (cópia 3)"], fields.Select(f => f.Name));
        Assert.Equal([0, 2, 3], fields.Select(f => f.Page));
        Assert.All(fields, f => Assert.False(f.Signed));
    }

    [Fact]
    public void PdfWithEditRestrictions_IsCopiedLikeInsertPages()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 3, prefix: "P");
        string restricted = Path.Combine(dir, "restrito.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("", "dono", AllowEdit: false), restricted);
        string output = Path.Combine(dir, "b.pdf");

        Assert.Equal(4, PdfOperations.CopyPages(restricted, "3", 1, 1, output));
        Assert.Equal(["P 1", "P 3", "P 2", "P 3"], TestPdfs.Labels(output));
    }

    [Fact]
    public void PdfWithOpenPassword_IsRefusedWithAClearMessage()
    {
        string dir = TestPdfs.TempDir();
        string locked = Path.Combine(dir, "fechado.pdf");
        PdfPasswords.Protect(TestPdfs.Create(Path.Combine(dir, "a.pdf"), 2), new PasswordOptions("abrir"), locked);
        var ex = Assert.Throws<InvalidOperationException>(() => PdfOperations.CopyPages(locked, "1", 1, 1, Path.Combine(dir, "b.pdf")));
        Assert.Contains("senha", ex.Message);
    }

    [Theory]
    [InlineData("1", 0, 5)]   // posição fora do documento
    [InlineData("9", 1, 1)]   // página que não existe
    [InlineData("1", 51, 1)]  // cópias demais
    public void InvalidChoices_AreRefused(string ranges, int copies, int position)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 3);
        Assert.ThrowsAny<Exception>(() => PdfOperations.CopyPages(input, ranges, copies, position, Path.Combine(dir, "b.pdf")));
    }

    private static string Content(PdfPage page)
    {
        var stream = new MemoryStream();
        for (int i = 0; i < page.Contents.Elements.Count; i++)
            stream.Write(page.Contents.Elements.GetDictionary(i)!.Stream.UnfilteredValue);
        return Convert.ToBase64String(stream.ToArray());
    }
}
