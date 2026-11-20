using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 8.1: PDFs em lote (a mesma operação em vários arquivos, sem mexer nos originais).</summary>
public class BatchTests
{
    private static readonly WatermarkOptions Mark = new("CONFIDENCIAL", "Arial", new AnnotationColor(200, 0, 0));

    private static string? AddMark(string input, string output) => $"{PdfWatermark.Add(input, Mark, null, output)} páginas";

    [Fact]
    public void EveryFile_GetsTheAction_AndOriginalsStayTheSame()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 2), b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 3);
        byte[] before = File.ReadAllBytes(a);
        string outDir = Path.Combine(dir, "saida");

        var results = PdfBatch.Run([a, b], outDir, "-marca", AddMark);

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal([Path.Combine(outDir, "a-marca.pdf"), Path.Combine(outDir, "b-marca.pdf")], results.Select(r => r.Output));
        Assert.Equal(["2 páginas", "3 páginas"], results.Select(r => r.Detail));
        Assert.Equal(2, PdfWatermark.Count(results[0].Output!));
        Assert.Equal(3, PdfWatermark.Count(results[1].Output!));
        Assert.Equal(before, File.ReadAllBytes(a)); // o original não muda
        Assert.Empty(Directory.GetFiles(outDir, "*.tmp", SearchOption.AllDirectories)); // nada de temporário sobrando
    }

    [Fact]
    public void ExistingFile_IsNeverOverwritten()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1);
        string outDir = Path.Combine(dir, "saida");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "a-marca.pdf"), "arquivo do usuário");

        var result = Assert.Single(PdfBatch.Run([a], outDir, "-marca", AddMark));

        Assert.Equal(Path.Combine(outDir, "a-marca (2).pdf"), result.Output);
        Assert.Equal("arquivo do usuário", File.ReadAllText(Path.Combine(outDir, "a-marca.pdf")));
    }

    [Fact]
    public void BadFiles_AreReported_AndTheOthersContinue()
    {
        string dir = TestPdfs.TempDir();
        string good = TestPdfs.Create(Path.Combine(dir, "bom.pdf"), 1);
        string locked = Path.Combine(dir, "senha.pdf");
        PdfPasswords.Protect(TestPdfs.Create(Path.Combine(dir, "x.pdf"), 1), new PasswordOptions("abc123"), locked);
        string broken = Path.Combine(dir, "quebrado.pdf");
        File.WriteAllText(broken, "isto não é um PDF");
        string missing = Path.Combine(dir, "sumiu.pdf");
        string outDir = Path.Combine(dir, "saida");

        var results = PdfBatch.Run([locked, broken, missing, good], outDir, "-marca", AddMark);

        Assert.Equal([false, false, false, true], results.Select(r => r.Ok));
        Assert.Contains("senha", results[0].Error);
        Assert.Equal("O arquivo não existe mais.", results[2].Error);
        Assert.All(results.Take(3), r => Assert.Null(r.Output));
        Assert.Single(Directory.GetFiles(outDir)); // só o arquivo que deu certo
    }

    [Fact]
    public void Cancel_StopsBeforeTheNextFile_AndProgressIsReported()
    {
        string dir = TestPdfs.TempDir();
        var inputs = Enumerable.Range(1, 4).Select(i => TestPdfs.Create(Path.Combine(dir, $"{i}.pdf"), 1)).ToList();
        using var cancel = new CancellationTokenSource();
        var seen = new List<BatchProgress>();
        var progress = new SyncProgress<BatchProgress>(p => { seen.Add(p); if (p.Done == 2) cancel.Cancel(); });

        var results = PdfBatch.Run(inputs, Path.Combine(dir, "saida"), "-x", AddMark, progress, cancel.Token);

        Assert.Equal(2, results.Count);
        Assert.Equal([0, 1, 2], seen.Select(p => p.Done).Distinct());
        Assert.All(seen, p => Assert.Equal(4, p.Total));
    }

    [Fact]
    public void Names_AndSameFolderCheck()
    {
        Assert.Equal("contrato-marca.pdf", PdfBatch.OutputName(@"C:\docs\contrato.pdf", "-marca"));
        Assert.Equal("contrato_ok.pdf", PdfBatch.OutputName(@"C:\docs\contrato.pdf", "_o:k?")); // tira o que não vale em nome de arquivo
        Assert.True(PdfBatch.NeedsSuffix([@"C:\docs\a.pdf"], @"C:\docs\", ""));
        Assert.False(PdfBatch.NeedsSuffix([@"C:\docs\a.pdf"], @"C:\docs", "-novo"));
        Assert.False(PdfBatch.NeedsSuffix([@"C:\docs\a.pdf"], @"C:\saida", ""));
        Assert.True(PdfBatch.NeedsSuffix([@"C:\docs\a.pdf"], null, " ")); // "a mesma pasta de cada arquivo" exige o acréscimo
    }

    [Fact]
    public void WithoutFolder_EachResultGoesNextToItsOriginal()
    {
        string dir = TestPdfs.TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "um"));
        Directory.CreateDirectory(Path.Combine(dir, "dois"));
        string a = TestPdfs.Create(Path.Combine(dir, "um", "a.pdf"), 1), b = TestPdfs.Create(Path.Combine(dir, "dois", "b.pdf"), 1);

        var results = PdfBatch.Run([a, b], null, "-marca", AddMark);

        Assert.Equal([Path.Combine(dir, "um", "a-marca.pdf"), Path.Combine(dir, "dois", "b-marca.pdf")], results.Select(r => r.Output));
    }

    /// <summary>IProgress que chama na hora (o Progress padrão chama depois, em outra thread).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
