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

    // ---------------- Etapa 8.2: senha, assinar, imprimir ----------------

    [Fact]
    public void Password_IsPut_AndRemoved_InEveryFile()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1), b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 2);
        string locked = Path.Combine(dir, "protegidos"), opened = Path.Combine(dir, "abertos");

        var put = PdfBatch.Run([a, b], locked, "", PdfBatch.Protect(new PasswordOptions("lote123"), null));
        Assert.All(put, r => Assert.True(r.Ok, r.Error));
        Assert.All(put, r => Assert.Equal(PasswordState.OpenPassword, PdfPasswords.Inspect(r.Output!)));
        Assert.Equal(PasswordState.None, PdfPasswords.Inspect(a)); // original sem senha

        string plain = TestPdfs.Create(Path.Combine(dir, "sem.pdf"), 1);
        var removed = PdfBatch.Run([.. put.Select(r => r.Output!), plain], opened, "", PdfBatch.RemovePassword("lote123"));
        Assert.All(removed, r => Assert.True(r.Ok, r.Error));
        Assert.All(removed, r => Assert.Equal(PasswordState.None, PdfPasswords.Inspect(r.Output!)));
        Assert.Equal(["senha removida", "senha removida", "já estava sem senha (cópia)"], removed.Select(r => r.Detail));
        Assert.Equal(TestPdfs.Labels(b), TestPdfs.Labels(removed[1].Output!));
    }

    [Fact]
    public void Password_WrongOrMissing_IsReportedPerFile()
    {
        string dir = TestPdfs.TempDir();
        string other = Path.Combine(dir, "outra.pdf");
        PdfPasswords.Protect(TestPdfs.Create(Path.Combine(dir, "x.pdf"), 1), new PasswordOptions("outra"), other);
        string good = Path.Combine(dir, "certa.pdf");
        PdfPasswords.Protect(TestPdfs.Create(Path.Combine(dir, "y.pdf"), 1), new PasswordOptions("certa"), good);

        var removed = PdfBatch.Run([other, good], Path.Combine(dir, "s1"), "", PdfBatch.RemovePassword("certa"));
        Assert.Equal([false, true], removed.Select(r => r.Ok));
        Assert.Equal("Senha incorreta.", removed[0].Error);

        // Trocar a senha de um arquivo que já tem uma: sem a senha atual, erro explicado.
        var put = PdfBatch.Run([good], Path.Combine(dir, "s2"), "", PdfBatch.Protect(new PasswordOptions("nova"), null));
        Assert.Contains("senha", Assert.Single(put).Error);
        var swapped = PdfBatch.Run([good], Path.Combine(dir, "s3"), "", PdfBatch.Protect(new PasswordOptions("nova"), "certa"));
        Assert.Equal("proteção trocada", Assert.Single(swapped).Detail);
    }

    [Fact]
    public void Sign_EveryFile_OnTheFirstOrLastPage_SkippingSignedOnes()
    {
        string dir = TestPdfs.TempDir();
        string pfx = SigningTests.CreatePfx(dir);
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1), b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 3);
        var template = new SignatureRequest { InputPath = "", OutputPath = "", PfxPath = pfx, PfxPassword = "senha123", Visible = true, Reason = "Lote" };

        var results = PdfBatch.Run([a, b], Path.Combine(dir, "assinados"), "-assinado", PdfBatch.Sign(template, BatchSignaturePage.Last));

        Assert.All(results, r => Assert.True(r.Ok, r.Error));
        Assert.Equal(["assinado (página 1)", "assinado (página 3)"], results.Select(r => r.Detail));
        Assert.All(results, r => SigningTests.VerifySignature(r.Output!));
        Assert.False(PdfSigning.HasSignatures(a)); // original sem assinatura

        // Assinar de novo o que já está assinado: recusado (estragaria a assinatura).
        var again = Assert.Single(PdfBatch.Run([results[0].Output!], Path.Combine(dir, "de-novo"), "", PdfBatch.Sign(template, BatchSignaturePage.First)));
        Assert.Contains("Já tem assinatura", again.Error);
    }

    [Fact]
    public void Print_ChecksEachFile_AndMergesWithBlankPagesForDuplex()
    {
        string dir = TestPdfs.TempDir();
        string one = TestPdfs.Create(Path.Combine(dir, "um.pdf"), 1), three = TestPdfs.Create(Path.Combine(dir, "tres.pdf"), 3);
        string two = TestPdfs.Create(Path.Combine(dir, "dois.pdf"), 2);
        string locked = Path.Combine(dir, "senha.pdf");
        PdfPasswords.Protect(TestPdfs.Create(Path.Combine(dir, "x.pdf"), 1), new PasswordOptions("abc"), locked);
        string noPrint = Path.Combine(dir, "sem-imprimir.pdf");
        PdfPasswords.Protect(TestPdfs.Create(Path.Combine(dir, "y.pdf"), 1), new PasswordOptions("", "dono", AllowPrint: false), noPrint);

        Assert.Null(PdfBatch.CannotPrint(one));
        Assert.Contains("senha para abrir", PdfBatch.CannotPrint(locked));
        Assert.Contains("não permite imprimir", PdfBatch.CannotPrint(noPrint));
        Assert.Equal("O arquivo não existe mais.", PdfBatch.CannotPrint(Path.Combine(dir, "sumiu.pdf")));

        string merged = Path.Combine(dir, "junto.pdf"), padded = Path.Combine(dir, "folhas.pdf");
        Assert.Equal(6, PdfBatch.MergeForPrint([one, three, two], merged, eachOnNewSheet: false));
        // um (1) + branca, tres (3) + branca, dois (2): cada arquivo começa numa folha nova.
        Assert.Equal(8, PdfBatch.MergeForPrint([one, three, two], padded, eachOnNewSheet: true));
        var labels = TestPdfs.Labels(padded);
        Assert.Equal(TestPdfs.Labels(one)[0], labels[0]);
        Assert.Equal(TestPdfs.Labels(three)[0], labels[2]);
        Assert.Equal(TestPdfs.Labels(two)[0], labels[6]);
    }

    /// <summary>IProgress que chama na hora (o Progress padrão chama depois, em outra thread).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
