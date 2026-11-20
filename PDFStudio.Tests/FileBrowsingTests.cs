using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Tela própria de abrir/salvar: listar, filtrar, conferir nomes, nova pasta, voltar/avançar.</summary>
public class FileBrowsingTests
{
    private static string Folder()
    {
        string dir = TestPdfs.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "Zeta"));
        Directory.CreateDirectory(Path.Combine(dir, "alfa"));
        File.WriteAllText(Path.Combine(dir, "pagina10.pdf"), "x");
        File.WriteAllText(Path.Combine(dir, "pagina2.PDF"), "xx");
        File.WriteAllText(Path.Combine(dir, "Relatório.pdf"), "x");
        File.WriteAllText(Path.Combine(dir, "nota.txt"), "x");
        string hidden = Path.Combine(dir, "oculto.pdf");
        File.WriteAllText(hidden, "x");
        File.SetAttributes(hidden, FileAttributes.Hidden);
        return dir;
    }

    [Fact]
    public void List_FoldersFirst_NaturalOrder_FilteredByExtension()
    {
        string dir = Folder();
        var names = FileBrowsing.List(dir, [".pdf"]).Select(e => e.Name).ToList();
        Assert.Equal(["alfa", "Zeta", "pagina2.PDF", "pagina10.pdf", "Relatório.pdf"], names);

        var all = FileBrowsing.List(dir, []).Select(e => e.Name).ToList();
        Assert.Contains("nota.txt", all);
        Assert.DoesNotContain("oculto.pdf", all); // oculto fica de fora

        var onlyFolders = FileBrowsing.List(dir, [], includeFiles: false);
        Assert.All(onlyFolders, e => Assert.True(e.IsFolder));
        Assert.Equal(2, onlyFolders.Count);
    }

    [Fact]
    public void List_Search_IgnoresCaseAndAccents()
    {
        string dir = Folder();
        var names = FileBrowsing.List(dir, [".pdf"], search: "relatorio").Select(e => e.Name).ToList();
        Assert.Equal(["Relatório.pdf"], names);
        Assert.Equal(2, FileBrowsing.List(dir, ["pdf"], search: "PAGINA").Count);
    }

    [Fact]
    public void NameProblem_DetectsBadNames()
    {
        Assert.Null(FileBrowsing.NameProblem("contrato final.pdf"));
        Assert.NotNull(FileBrowsing.NameProblem(""));
        Assert.NotNull(FileBrowsing.NameProblem("a?b.pdf"));
        Assert.NotNull(FileBrowsing.NameProblem("nome."));
        Assert.NotNull(FileBrowsing.NameProblem("CON.pdf"));
        Assert.NotNull(FileBrowsing.NameProblem("com1"));
    }

    [Fact]
    public void SavePath_AddsExtensionAndChecks()
    {
        string dir = Folder();
        Assert.Equal(Path.Combine(dir, "novo.pdf"), FileBrowsing.SavePath(dir, "novo", ".pdf"));
        Assert.Equal(Path.Combine(dir, "novo.PDF"), FileBrowsing.SavePath(dir, " novo.PDF ", ".pdf"));
        Assert.Equal(Path.Combine(dir, "alfa", "x.pdf"), FileBrowsing.SavePath(dir, @"alfa\x", "pdf"));
        string other = TestPdfs.TempDir();
        Assert.Equal(Path.Combine(other, "y.pdf"), FileBrowsing.SavePath(dir, Path.Combine(other, "y.pdf"), ".pdf"));
        Assert.Equal(Path.Combine(dir, "a.b.docx"), FileBrowsing.SavePath(dir, "a.b", ".docx"));

        Assert.Throws<ArgumentException>(() => FileBrowsing.SavePath(dir, "  ", ".pdf"));
        Assert.Throws<ArgumentException>(() => FileBrowsing.SavePath(dir, "a*b", ".pdf"));
        Assert.Throws<ArgumentException>(() => FileBrowsing.SavePath(dir, @"naoexiste\x", ".pdf"));
        Assert.Throws<ArgumentException>(() => FileBrowsing.SavePath(dir, "alfa", "")); // é uma pasta
    }

    [Fact]
    public void CreateFolder_AvoidsExistingNames()
    {
        string dir = Folder();
        string a = FileBrowsing.CreateFolder(dir, "Nova pasta");
        string b = FileBrowsing.CreateFolder(dir, "Nova pasta");
        Assert.Equal(Path.Combine(dir, "Nova pasta"), a);
        Assert.Equal(Path.Combine(dir, "Nova pasta (2)"), b);
        Assert.True(Directory.Exists(b));
        Assert.Throws<ArgumentException>(() => FileBrowsing.CreateFolder(dir, "a/b"));
    }

    [Fact]
    public void Parent_StopsAtTheDrive()
    {
        Assert.Equal(@"C:\Users", FileBrowsing.Parent(@"C:\Users\Ana"));
        Assert.Equal(@"C:\Users", FileBrowsing.Parent(@"C:\Users\Ana\"));
        Assert.Equal(@"C:\", FileBrowsing.Parent(@"C:\Users"));
        Assert.Null(FileBrowsing.Parent(@"C:\"));
    }

    [Fact]
    public void FormatSize_IsReadable()
    {
        Assert.Equal("1 byte", FileBrowsing.FormatSize(1));
        Assert.Equal("830 bytes", FileBrowsing.FormatSize(830));
        Assert.Equal("2 KB", FileBrowsing.FormatSize(1500));
        Assert.Equal("1,5 MB", FileBrowsing.FormatSize(1536 * 1024));
        Assert.Equal("20 MB", FileBrowsing.FormatSize(20L * 1024 * 1024));
    }

    [Fact]
    public void Places_IncludesDocumentsAndADrive()
    {
        var places = FileBrowsing.Places();
        Assert.Contains(places, p => p.Kind == FilePlaceKind.Drive);
        Assert.All(places, p => Assert.True(Directory.Exists(p.Path)));
    }

    [Fact]
    public void History_BackAndForward()
    {
        var h = new FolderHistory();
        Assert.False(h.CanGoBack);
        h.Visit(@"C:\a");
        h.Visit(@"C:\b");
        h.Visit(@"C:\B"); // a mesma pasta não repete
        h.Visit(@"C:\c");
        Assert.Equal(@"C:\b", h.Back());
        Assert.Equal(@"C:\a", h.Back());
        Assert.Null(h.Back());
        Assert.Equal(@"C:\b", h.Forward());
        h.Visit(@"C:\d"); // o "avançar" (c) se perde
        Assert.False(h.CanGoForward);
        Assert.Equal(@"C:\b", h.Back());
    }
}
