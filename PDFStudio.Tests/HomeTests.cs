using PDFStudio.Core;

namespace PDFStudio.Tests;

public class RecentFilesTests
{
    private static (RecentFilesStore Store, string StorePath, string Dir) NewStore()
    {
        string dir = TestPdfs.TempDir();
        string storePath = Path.Combine(dir, "cfg", "recent.json");
        return (new RecentFilesStore(storePath), storePath, dir);
    }

    [Fact]
    public void Add_OrdersByMostRecent_AndPersists()
    {
        var (store, storePath, dir) = NewStore();
        string a = Path.Combine(dir, "a.pdf"), b = Path.Combine(dir, "b.pdf");
        store.Add(a, new DateTime(2026, 1, 1));
        store.Add(b, new DateTime(2026, 1, 2));
        Assert.Equal([b, a], store.Entries.Select(e => e.Path));

        store.Add(a, new DateTime(2026, 1, 3)); // abrir de novo sobe para o topo, sem duplicar
        var reloaded = new RecentFilesStore(storePath);
        Assert.Equal([a, b], reloaded.Entries.Select(e => e.Path));
    }

    [Fact]
    public void LastPage_IsRemembered_AndSurvivesReopen()
    {
        var (store, storePath, dir) = NewStore();
        string a = Path.Combine(dir, "a.pdf");
        Assert.Equal(1, store.LastPageOf(a)); // arquivo nunca aberto: página 1
        store.Add(a);
        Assert.Equal(1, store.LastPageOf(a));
        store.SetLastPage(a, 7);
        store.SetLastPage(a, 0); // inválida: ignorada
        store.Add(a); // abrir de novo não perde a página
        Assert.Equal(7, new RecentFilesStore(storePath).LastPageOf(a.ToUpperInvariant()));

        store.SetLastPage(Path.Combine(dir, "nao-aberto.pdf"), 3); // fora da lista: nada acontece
        Assert.Single(store.Entries);
    }

    [Fact]
    public void Paths_AreCaseInsensitive()
    {
        var (store, _, dir) = NewStore();
        store.Add(Path.Combine(dir, "Doc.pdf"));
        store.Add(Path.Combine(dir, "DOC.PDF"));
        Assert.Single(store.Entries);
    }

    [Fact]
    public void Star_SurvivesReopenAndClear()
    {
        var (store, storePath, dir) = NewStore();
        string a = Path.Combine(dir, "a.pdf"), b = Path.Combine(dir, "b.pdf");
        store.Add(a);
        store.Add(b);
        store.SetStarred(a, true);
        store.Add(a); // reabrir mantém a estrela
        Assert.True(store.Find(a)!.Starred);

        store.Clear(keepStarred: true);
        var reloaded = new RecentFilesStore(storePath);
        Assert.Equal([a], reloaded.Entries.Select(e => e.Path));
        Assert.Equal([a], reloaded.Starred.Select(e => e.Path));

        reloaded.Clear(keepStarred: false);
        Assert.Empty(reloaded.Entries);
    }

    [Fact]
    public void Trim_KeepsStarredFiles()
    {
        var (store, _, dir) = NewStore();
        string starred = Path.Combine(dir, "favorito.pdf");
        store.Add(starred, new DateTime(2020, 1, 1));
        store.SetStarred(starred, true);
        for (int i = 0; i < RecentFilesStore.MaxUnstarred + 20; i++)
            store.Add(Path.Combine(dir, $"f{i}.pdf"), new DateTime(2026, 1, 1).AddMinutes(i));

        Assert.Equal(RecentFilesStore.MaxUnstarred + 1, store.Entries.Count);
        Assert.NotNull(store.Find(starred));
        Assert.Null(store.Find(Path.Combine(dir, "f0.pdf"))); // os mais antigos sem estrela saem
    }

    [Fact]
    public void Folders_GroupFilesByDirectory()
    {
        var (store, _, dir) = NewStore();
        string sub = Path.Combine(dir, "sub");
        store.Add(Path.Combine(dir, "a.pdf"), new DateTime(2026, 1, 1));
        store.Add(Path.Combine(dir, "b.pdf"), new DateTime(2026, 1, 2));
        store.Add(Path.Combine(sub, "c.pdf"), new DateTime(2026, 1, 3));

        var folders = store.Folders;
        Assert.Equal(2, folders.Count);
        Assert.Equal(sub, folders[0].Path);
        Assert.Equal(1, folders[0].FileCount);
        Assert.Equal(2, folders[1].FileCount);
    }

    [Fact]
    public void RemoveMissing_DropsDeletedFiles()
    {
        var (store, _, dir) = NewStore();
        string exists = TestPdfs.Create(Path.Combine(dir, "existe.pdf"), 1);
        store.Add(exists);
        store.Add(Path.Combine(dir, "apagado.pdf"));
        Assert.Equal(1, store.RemoveMissing());
        Assert.Equal([exists], store.Entries.Select(e => e.Path));
    }

    [Fact]
    public void CorruptFile_StartsEmpty()
    {
        string dir = TestPdfs.TempDir();
        string storePath = Path.Combine(dir, "recent.json");
        File.WriteAllText(storePath, "{ isto não é json");
        var store = new RecentFilesStore(storePath);
        Assert.Empty(store.Entries);
        store.Add(Path.Combine(dir, "a.pdf")); // e volta a funcionar
        Assert.Single(new RecentFilesStore(storePath).Entries);
    }
}

public class BlankPdfTests
{
    [Theory]
    [InlineData(3, false, 595, 842)]
    [InlineData(1, true, 842, 595)]
    public void CreateBlank_MakesPagesOfTheChosenSize(int pages, bool landscape, double w, double h)
    {
        string dir = TestPdfs.TempDir();
        string output = Path.Combine(dir, "branco.pdf");
        PdfOperations.CreateBlank(output, pages, PdfSharp.PageSize.A4, landscape);

        var props = DocumentPropertiesService.Read(output);
        Assert.Equal(pages, props.PageCount);
        Assert.Equal(w, props.PageWidthPt, 0);
        Assert.Equal(h, props.PageHeightPt, 0);
    }

    [Fact]
    public void CreateBlank_RejectsInvalidPageCount()
    {
        string output = Path.Combine(TestPdfs.TempDir(), "x.pdf");
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfOperations.CreateBlank(output, 0, PdfSharp.PageSize.A4));
    }
}
