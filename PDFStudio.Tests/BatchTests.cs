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

    // ---------------- Etapa 8.3: converter, criar, OCR ----------------

    [Fact]
    public void ToWord_MakesADocxPerPdf_WithoutNeedingASuffix()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 2);

        Assert.False(PdfBatch.NeedsSuffix([a], null, "", ".docx")); // a.docx não tem o nome do original
        var r = Assert.Single(PdfBatch.Run([a], null, "", PdfBatch.ToWord(), extension: ".docx"));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Path.Combine(dir, "a.docx"), r.Output);
        Assert.Equal("2 páginas", r.Detail);
        using var zip = System.IO.Compression.ZipFile.OpenRead(r.Output!);
        Assert.NotNull(zip.GetEntry("word/document.xml"));
    }

    [Fact]
    public void Images_BecomeOnePdfEach()
    {
        string dir = TestPdfs.TempDir();
        string png = TestImages.Png(Path.Combine(dir, "foto.png"), 300, 200);
        Assert.True(PdfBatch.IsImage(png));
        Assert.False(PdfBatch.IsImage(Path.Combine(dir, "x.pdf")));

        var r = Assert.Single(PdfBatch.Run([png], null, "", PdfBatch.ImageToPdf(ImagePageSize.A4, ImageMargin.None)));

        Assert.True(r.Ok, r.Error);
        Assert.Equal(Path.Combine(dir, "foto.pdf"), r.Output);
        Assert.Equal(1, PdfOperations.GetPageCount(r.Output!));
    }

    [Fact]
    public void Skipped_WhenTheOperationWritesNothing()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1);
        var r = Assert.Single(PdfBatch.Run([a], Path.Combine(dir, "s"), "-x", (_, _) => "já tem texto"));
        Assert.True(r.Ok);
        Assert.Null(r.Output);
        Assert.Equal("já tem texto", r.Detail);
    }

    [Fact]
    public async Task ManyFilesPerInput_GoToTheFolder_WithoutOverwriting_AndNothingStaysOnError()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1), b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 1);
        string outDir = Path.Combine(dir, "imagens");
        Directory.CreateDirectory(outDir);
        File.WriteAllText(Path.Combine(outDir, "a-pagina-01.png"), "do usuário");

        var results = await PdfBatch.RunToFolderAsync([a, b], outDir, async (input, folder, _) =>
        {
            string name = Path.GetFileNameWithoutExtension(input);
            await File.WriteAllTextAsync(Path.Combine(folder, $"{name}-pagina-01.png"), "1");
            await File.WriteAllTextAsync(Path.Combine(folder, $"{name}-pagina-02.png"), "2");
            if (name == "b") throw new IOException("disco cheio");
            return "2 imagens";
        });

        Assert.True(results[0].Ok);
        Assert.Equal(Path.Combine(outDir, "a-pagina-01 (2).png"), results[0].Output);
        Assert.Equal("do usuário", File.ReadAllText(Path.Combine(outDir, "a-pagina-01.png")));
        Assert.False(results[1].Ok);
        Assert.Equal(["a-pagina-01 (2).png", "a-pagina-01.png", "a-pagina-02.png"],
            Directory.GetFileSystemEntries(outDir).Select(Path.GetFileName).Order()); // nada de "b", nem pasta temporária
    }

    [Fact]
    public async Task StopInTheMiddleOfAFile_SavesNothingOfIt()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1);
        using var cancel = new CancellationTokenSource();
        var r = Assert.Single(await PdfBatch.RunAsync([a], Path.Combine(dir, "s"), "-x", (input, output, token) =>
        {
            File.Copy(input, output);
            cancel.Cancel();
            token.ThrowIfCancellationRequested();
            return Task.FromResult<string?>(null);
        }, cancel: cancel.Token));
        Assert.Contains("Parado no meio", r.Error);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "s")));
    }

    // ---------------- Etapa 8.4: Bates, páginas em branco, formulários, renomear ----------------

    [Fact]
    public void Bates_ContinuesFromOneFileToTheNext()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 2), b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 3);
        var options = new BatesOptions("ABC", 6);
        Assert.Equal("ABC000001", PdfBates.Label(options, 1));
        Assert.Equal("X-0042/24", PdfBates.Label(new BatesOptions("X-", 4, "/24"), 42));

        var results = PdfBatch.Run([a, b], Path.Combine(dir, "s"), "-bates", PdfBatch.Bates(options, 1));

        Assert.Equal(["ABC000001 a ABC000002", "ABC000003 a ABC000005"], results.Select(r => r.Detail));
        Assert.Equal(3, PdfBates.Count(results[1].Output!));
        Assert.Contains(PdfTextLayout.Words(results[1].Output!, 0), w => w.Text == "ABC000003");
        Assert.Contains(PdfTextLayout.Words(results[1].Output!, 2), w => w.Text == "ABC000005");
        Assert.Equal(TestPdfs.Labels(b), TestPdfs.Labels(results[1].Output!)); // o conteúdo continua o mesmo

        // Numerar de novo troca o número (não acumula).
        string again = Path.Combine(dir, "de-novo.pdf");
        PdfBates.Add(results[0].Output!, options, 100, again);
        var words = PdfTextLayout.Words(again, 0).Select(w => w.Text).ToList();
        Assert.Contains("ABC000100", words);
        Assert.DoesNotContain("ABC000001", words);
    }

    private static byte[] Page(int width, int height, Action<Action<int, int>>? ink = null)
    {
        var data = new byte[width * height * 4];
        Array.Fill(data, (byte)255);
        ink?.Invoke((x, y) => { int i = (y * width + x) * 4; data[i] = data[i + 1] = data[i + 2] = 0; });
        return data;
    }

    [Fact]
    public void BlankPage_IgnoresDirtAndScannerBorders_ButNotText()
    {
        const int W = 200, H = 300;
        Assert.Equal(0, BlankPages.InkRatio(Page(W, H), W, H));
        // Borda preta do scanner (fora da área que conta) e 5 pontinhos de sujeira.
        var dirty = Page(W, H, dot =>
        {
            for (int y = 0; y < H; y++) { dot(0, y); dot(1, y); dot(W - 1, y); }
            for (int i = 0; i < 5; i++) dot(50 + i * 20, 150);
        });
        double dirt = BlankPages.InkRatio(dirty, W, H);
        Assert.True(BlankPages.IsBlank(dirt, BlankPages.DefaultTolerancePercent), $"{dirt:P3}");
        // Uma linha de texto (bloco de 120 × 8 pontos com metade pintada).
        var text = Page(W, H, dot => { for (int y = 40; y < 48; y++) for (int x = 30; x < 150; x += 2) dot(x, y); });
        double t = BlankPages.InkRatio(text, W, H);
        Assert.False(BlankPages.IsBlank(t, BlankPages.DefaultTolerancePercent), $"{t:P3}");
        Assert.True(BlankPages.IsBlank(t, 2)); // com tolerância bem alta passa
        Assert.Equal("2, 5", BlankPages.Ranges([4, 1]));
    }

    [Fact]
    public void BlankPages_AreRemoved_SkippingFilesWithoutThem()
    {
        string dir = TestPdfs.TempDir();
        string a = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 4), b = TestPdfs.Create(Path.Combine(dir, "b.pdf"), 2);
        string c = TestPdfs.Create(Path.Combine(dir, "c.pdf"), 2);
        // Tinta fingida por página: em "a" as páginas 2 e 4 estão em branco; "b" não tem; "c" é toda branca.
        var ink = new Dictionary<string, double[]> { ["a"] = [0.05, 0, 0.03, 0.0002], ["b"] = [0.02, 0.04], ["c"] = [0, 0] };
        var op = PdfBatch.RemoveBlankPages((input, _) =>
            Task.FromResult<IReadOnlyList<double>>(ink[Path.GetFileNameWithoutExtension(input)]), BlankPages.DefaultTolerancePercent);

        var results = PdfBatch.RunAsync([a, b, c], Path.Combine(dir, "s"), "-sem-brancas", op).GetAwaiter().GetResult();

        Assert.Equal("2 páginas excluídas (2, 4)", results[0].Detail);
        Assert.Equal([TestPdfs.Labels(a)[0], TestPdfs.Labels(a)[2]], TestPdfs.Labels(results[0].Output!));
        Assert.True(results[1].Ok);
        Assert.Null(results[1].Output); // nada a fazer
        Assert.Contains("Todas as páginas", results[2].Error);
    }

    /// <summary>Formulário de teste: campos soltos, um grupo com filhos, caixa marcada/desmarcada e assinatura.</summary>
    private static string FormPdf(string path, string name, string city, bool accepted)
    {
        TestPdfs.Create(path, 1);
        using (var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Modify))
        {
            PdfSharp.Pdf.PdfDictionary Field(string t, string ft)
            {
                var f = new PdfSharp.Pdf.PdfDictionary(doc);
                f.Elements["/T"] = new PdfSharp.Pdf.PdfString(t);
                if (ft.Length > 0) f.Elements.SetName("/FT", ft);
                doc.Internals.AddObject(f);
                return f;
            }
            var nameField = Field("nome", "/Tx");
            nameField.Elements["/V"] = new PdfSharp.Pdf.PdfString(name);
            var address = Field("endereco", "/Tx");
            var cityField = Field("cidade", "");
            cityField.Elements["/V"] = new PdfSharp.Pdf.PdfString(city);
            cityField.Elements["/Parent"] = address.Reference;
            address.Elements["/Kids"] = new PdfSharp.Pdf.PdfArray(doc, cityField.Reference!);
            var check = Field("aceito", "/Btn");
            check.Elements.SetName("/V", accepted ? "/Yes" : "/Off");
            var sig = Field("assinatura", "/Sig");
            var form = new PdfSharp.Pdf.PdfDictionary(doc);
            form.Elements["/Fields"] = new PdfSharp.Pdf.PdfArray(doc, nameField.Reference!, address.Reference!, check.Reference!, sig.Reference!);
            doc.Internals.Catalog.Elements["/AcroForm"] = form;
            doc.Save(path);
        }
        return path;
    }

    [Fact]
    public void FormData_GoesToOneCsvRowPerFile()
    {
        string dir = TestPdfs.TempDir();
        string a = FormPdf(Path.Combine(dir, "a.pdf"), "Ana; Maria", "São Paulo", true);
        string b = FormPdf(Path.Combine(dir, "b.pdf"), "=SOMA(1)", "Recife \"centro\"", false);
        string plain = TestPdfs.Create(Path.Combine(dir, "sem.pdf"), 1);

        Assert.Equal([new FormFieldValue("nome", "Ana; Maria"), new FormFieldValue("endereco.cidade", "São Paulo"), new FormFieldValue("aceito", "Yes")],
            PdfFormData.Read(a));

        var (results, csv) = PdfBatch.ExportFormsCsv([a, plain, b], null);

        Assert.Equal(["3 campos", "sem formulário", "3 campos"], results.Select(r => r.Detail));
        Assert.Equal(Path.Combine(dir, "dados-formularios.csv"), csv);
        byte[] bytes = File.ReadAllBytes(csv!);
        Assert.Equal([0xEF, 0xBB, 0xBF], bytes[..3]); // BOM: o Excel acerta os acentos
        Assert.Equal(
            "Arquivo;nome;endereco.cidade;aceito\r\n" +
            "a.pdf;\"Ana; Maria\";São Paulo;Yes\r\n" +
            "b.pdf;'=SOMA(1);\"Recife \"\"centro\"\"\";\r\n",
            File.ReadAllText(csv!));

        // De novo: não sobrescreve a planilha anterior.
        Assert.Equal(Path.Combine(dir, "dados-formularios (2).csv"), PdfBatch.ExportFormsCsv([a], null).Csv);
        Assert.Null(PdfBatch.ExportFormsCsv([plain], null).Csv);
    }

    [Fact]
    public void Rename_UsesTheContent_AndKeepsTheOriginals()
    {
        var info = new RenameInfo("scan001", "Contrato: Aluguel", "Ana", new DateTime(2026, 3, 5), "PROPOSTA COMERCIAL");
        Assert.Equal("2026-03-05 Contrato Aluguel", PdfRename.NameFor("{data} {titulo}", info, 1, 5));
        Assert.Equal("002 - PROPOSTA COMERCIAL", PdfRename.NameFor("{n} - {linha}", info, 2, 5));
        var empty = info with { Title = "", Author = "" };
        Assert.Equal("Relatório", PdfRename.NameFor("{autor} - Relatório", empty, 1, 1)); // sobra de separador sai
        Assert.Equal("scan001", PdfRename.NameFor("{titulo}", empty, 1, 1)); // sem título: fica o nome atual
        Assert.Equal("007 - scan001", PdfRename.NameFor("{n} - {titulo} - {nome}", empty, 7, 9)); // sem "- -" no meio
        Assert.Equal(PdfRename.MaxLength, PdfRename.NameFor(new string('x', 300), info, 1, 1).Length);

        string dir = TestPdfs.TempDir();
        string a = Path.Combine(dir, "a.pdf"); // sem título: fica o nome atual
        DocumentPropertiesService.Save(TestPdfs.Create(Path.Combine(dir, "w.pdf"), 1), new DocumentMetadata("", "", "", ""), a);
        string titled = Path.Combine(dir, "b.pdf");
        DocumentPropertiesService.Save(TestPdfs.Create(Path.Combine(dir, "x.pdf"), 1), new DocumentMetadata("Relatório anual", "", "", ""), titled);
        string c = TestPdfs.Create(Path.Combine(dir, "c.pdf"), 1);
        DocumentPropertiesService.Save(TestPdfs.Create(Path.Combine(dir, "y.pdf"), 1), new DocumentMetadata("Relatório anual", "", "", ""), c);

        var results = PdfBatch.CopyRenamed([a, titled, c], Path.Combine(dir, "s"), "{titulo}").GetAwaiter().GetResult();

        Assert.Equal(["a.pdf", "Relatório anual.pdf", "Relatório anual (2).pdf"], results.Select(r => Path.GetFileName(r.Output!)));
        Assert.True(File.Exists(a) && File.Exists(titled) && File.Exists(c)); // os originais ficam
        Assert.Equal("topo", PdfRename.Info(a).FirstLine); // a linha mais alta da página de teste
    }

    /// <summary>IProgress que chama na hora (o Progress padrão chama depois, em outra thread).</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
