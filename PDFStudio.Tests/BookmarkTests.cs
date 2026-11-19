using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.2: marcadores.</summary>
public class BookmarkTests
{
    private static string Titles(IEnumerable<Bookmark> list) =>
        string.Join(",", list.Select(b => b.Children.Count == 0 ? b.Title : $"{b.Title}[{Titles(b.Children)}]"));

    private static int RootCount(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Internals.Catalog.Elements.GetDictionary("/Outlines")!.Elements.GetInteger("/Count");
    }

    [Fact]
    public void Add_CreatesTheTree_AndPointsToThePage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        Assert.Empty(PdfBookmarks.List(input));

        PdfBookmarks.Add(input, [], 0, "Introdução", 0, null, input);
        PdfBookmarks.Add(input, [], 1, "Capítulo 2", 1, 300, input);
        PdfBookmarks.Add(input, [1], 0, "Seção 2.1", 2, null, input);

        var list = PdfBookmarks.List(input);
        Assert.Equal("Introdução,Capítulo 2[Seção 2.1]", Titles(list));
        Assert.Equal(0, list[0].Page);
        Assert.Null(list[0].Top);
        Assert.Equal(1, list[1].Page);
        Assert.Equal(300, list[1].Top!.Value, 1);
        Assert.Equal(2, list[1].Children[0].Page);
        Assert.True(list[1].IsOpen); // recebeu um filho: fica aberto
        Assert.Equal(3, RootCount(input));
        Assert.Equal(["Página 1", "Página 2", "Página 3"], TestPdfs.Labels(input)); // páginas intactas
    }

    [Fact]
    public void Rename_Delete_AndRemoveTheTreeWhenEmpty()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        PdfBookmarks.Add(input, [], 0, "A", 0, null, input);
        PdfBookmarks.Add(input, [], 1, "B", 1, null, input);
        PdfBookmarks.Add(input, [1], 0, "B1", 1, null, input);

        PdfBookmarks.Rename(input, [0], "  Novo nome ", input);
        Assert.Equal("Novo nome,B[B1]", Titles(PdfBookmarks.List(input)));

        PdfBookmarks.Delete(input, [1], input); // sai com o filho
        Assert.Equal("Novo nome", Titles(PdfBookmarks.List(input)));
        Assert.Equal(1, RootCount(input));

        PdfBookmarks.Delete(input, [0], input);
        Assert.Empty(PdfBookmarks.List(input));
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        Assert.Null(doc.Internals.Catalog.Elements["/Outlines"]);
    }

    [Fact]
    public void Move_UpDown_IndentAndOutdent()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        foreach (var (t, i) in new[] { ("A", 0), ("B", 1), ("C", 2) }) PdfBookmarks.Add(input, [], i, t, 0, null, input);

        PdfBookmarks.Move(input, [2], [], 0, input);           // C sobe para o começo
        Assert.Equal("C,A,B", Titles(PdfBookmarks.List(input)));

        PdfBookmarks.Move(input, [2], [1], 0, input);          // B vai para dentro de A
        Assert.Equal("C,A[B]", Titles(PdfBookmarks.List(input)));

        PdfBookmarks.Move(input, [1, 0], [], 2, input);        // B volta para o nível de cima, depois de A
        Assert.Equal("C,A,B", Titles(PdfBookmarks.List(input)));

        Assert.Throws<ArgumentException>(() => PdfBookmarks.Move(input, [0], [0], 0, input)); // dentro dele mesmo
    }

    [Fact]
    public void ReadsBookmarksMadeByOtherPrograms_AndKeepsThemWhenEditing()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        // Marcadores feitos pela própria biblioteca (como outro programa faria), um com ação /GoTo.
        using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var first = doc.Outlines.Add("Primeiro", doc.Pages[2], true);
            first.Outlines.Add("Filho", doc.Pages[1]);
            doc.Save(input);
        }

        var list = PdfBookmarks.List(input);
        Assert.Equal("Primeiro[Filho]", Titles(list));
        Assert.Equal(2, list[0].Page);
        Assert.Equal(1, list[0].Children[0].Page);

        PdfBookmarks.Add(input, [], 1, "Meu", 0, null, input);
        Assert.Equal("Primeiro[Filho],Meu", Titles(PdfBookmarks.List(input)));
        Assert.Equal(2, PdfBookmarks.List(input)[0].Page);
    }

    [Fact]
    public void SetDestination_ChangesWhereItGoes()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        PdfBookmarks.Add(input, [], 0, "A", 0, null, input);

        PdfBookmarks.SetDestination(input, [0], 2, 100, input);

        var b = Assert.Single(PdfBookmarks.List(input));
        Assert.Equal(2, b.Page);
        Assert.Equal(100, b.Top!.Value, 1);
    }

    [Fact]
    public void EmptyTitle_IsRejected()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentException>(() => PdfBookmarks.Add(input, [], 0, "  ", 0, null, input));
    }
}
