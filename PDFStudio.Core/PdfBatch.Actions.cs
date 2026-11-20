using PdfSharp.Pdf;

namespace PDFStudio.Core;

/// <summary>Em qual página fica a assinatura visível de cada arquivo do lote (os arquivos têm tamanhos diferentes).</summary>
public enum BatchSignaturePage { First, Last }

/// <summary>Etapa 8.2: senha, assinatura e impressão em lote.</summary>
public static partial class PdfBatch
{
    // ---------------- Senha ----------------

    /// <summary>
    /// Operação "colocar senha". <paramref name="currentPassword"/>: senha de permissões dos arquivos que já têm
    /// proteção (os outros não precisam dela).
    /// </summary>
    public static Func<string, string, string?> Protect(PasswordOptions options, string? currentPassword) => (input, output) =>
    {
        bool had = PdfPasswords.Inspect(input) != PasswordState.None;
        PdfPasswords.Protect(input, options, output, string.IsNullOrEmpty(currentPassword) ? null : currentPassword);
        return had ? "proteção trocada" : "protegido";
    };

    /// <summary>Operação "remover senha": a mesma senha é tentada em todos os arquivos.</summary>
    public static Func<string, string, string?> RemovePassword(string password) => (input, output) =>
    {
        if (PdfPasswords.Inspect(input) == PasswordState.None)
        {
            File.Copy(input, output, overwrite: true);
            return "já estava sem senha (cópia)";
        }
        PdfPasswords.Remove(input, password, output);
        return "senha removida";
    };

    // ---------------- Assinar ----------------

    /// <summary>
    /// Operação "assinar" com o mesmo certificado em todos os arquivos. Do <paramref name="template"/> valem o
    /// certificado, se é visível, o canto, o carimbo e os dados (motivo, local…); a página é a primeira ou a última de
    /// cada arquivo. Arquivo que já tem assinatura é recusado: gravá-lo de novo invalidaria a assinatura anterior.
    /// </summary>
    public static Func<string, string, string?> Sign(SignatureRequest template, BatchSignaturePage page) => (input, output) =>
    {
        if (PdfSigning.HasSignatures(input))
            throw new InvalidOperationException("Já tem assinatura: assinar de novo invalidaria a anterior (use a tela Assinar, um arquivo por vez).");
        int pageNumber = page == BatchSignaturePage.Last ? PdfOperations.GetPageCount(input) : 1;
        var request = new SignatureRequest
        {
            InputPath = input,
            OutputPath = output,
            PfxPath = template.PfxPath,
            PfxPassword = template.PfxPassword,
            Visible = template.Visible,
            PageNumber = pageNumber,
            Position = template.Position,
            Reason = template.Reason,
            Location = template.Location,
            ContactInfo = template.ContactInfo,
            StampSignerName = template.StampSignerName,
            StampTitle = template.StampTitle,
            StampShowDate = template.StampShowDate,
            StampShowDetails = template.StampShowDetails,
            TimestampUrl = template.TimestampUrl,
        };
        PdfSigning.SignAsync(request).GetAwaiter().GetResult();
        return template.Visible ? $"assinado (página {pageNumber})" : "assinado";
    };

    // ---------------- Converter e criar (Etapa 8.3) ----------------

    /// <summary>Operação "PDF para Word" (use com extension ".docx").</summary>
    public static Func<string, string, string?> ToWord() => (input, output) =>
    {
        var r = PdfToWord.Convert(input, output);
        return $"{r.Pages} {(r.Pages == 1 ? "página" : "páginas")}" + (r.Images > 0 ? $", {r.Images} {(r.Images == 1 ? "imagem" : "imagens")}" : "");
    };

    /// <summary>Operação "imagem para PDF": cada imagem vira um PDF de uma página.</summary>
    public static Func<string, string, string?> ImageToPdf(ImagePageSize size, ImageMargin margin) => (input, output) =>
    {
        ImagesToPdf.Create([input], size, margin, output);
        return null;
    };

    /// <summary>A entrada é uma imagem que dá para pôr num PDF (pela extensão)?</summary>
    public static bool IsImage(string path) =>
        ImagesToPdf.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ---------------- Etapa 8.4: Bates, páginas em branco, formulários, renomear ----------------

    /// <summary>
    /// Operação "numeração Bates": a numeração continua de um arquivo para o outro, na ordem da lista (arquivo com erro
    /// não gasta números). Devolve "ABC000001 a ABC000004".
    /// </summary>
    public static Func<string, string, string?> Bates(BatesOptions options, long first)
    {
        long next = first;
        return (input, output) =>
        {
            long start = next;
            int pages = PdfBates.Add(input, options, start, output);
            next = start + pages;
            return pages == 1 ? PdfBates.Label(options, start) : $"{PdfBates.Label(options, start)} a {PdfBates.Label(options, start + pages - 1)}";
        };
    }

    /// <summary>
    /// Operação "excluir páginas em branco". <paramref name="inkOfPages"/> dá a fração de tinta de cada página (o app
    /// desenha as páginas com o motor do Windows). PDF sem página em branco é pulado; se todas forem brancas, erro.
    /// </summary>
    public static Func<string, string, CancellationToken, Task<string?>> RemoveBlankPages(
        Func<string, CancellationToken, Task<IReadOnlyList<double>>> inkOfPages, double tolerancePercent) => async (input, output, cancel) =>
    {
        var ink = await inkOfPages(input, cancel);
        var blank = Enumerable.Range(0, ink.Count).Where(i => BlankPages.IsBlank(ink[i], tolerancePercent)).ToList();
        if (blank.Count == 0) return "nenhuma página em branco";
        if (blank.Count == ink.Count) throw new InvalidOperationException("Todas as páginas parecem em branco: nada foi excluído (diminua a tolerância?).");
        PdfOperations.DeletePages(input, BlankPages.Ranges(blank), output);
        return blank.Count == 1 ? $"página {blank[0] + 1} excluída" : $"{blank.Count} páginas excluídas ({BlankPages.Ranges(blank)})";
    };

    /// <summary>
    /// Junta os dados de formulário de todos os PDFs numa planilha CSV (uma linha por arquivo), gravada em
    /// <paramref name="outputFolder"/> (null = pasta do primeiro arquivo) sem sobrescrever nada. PDF sem formulário fica
    /// de fora da planilha ("pulado"). Devolve o resultado de cada arquivo e o caminho do CSV (null = nenhum dado).
    /// </summary>
    public static (List<BatchItemResult> Results, string? Csv) ExportFormsCsv(IReadOnlyList<string> inputs, string? outputFolder,
        IProgress<BatchProgress>? progress = null, CancellationToken cancel = default, string fileName = "dados-formularios.csv")
    {
        var rows = new List<(string File, IReadOnlyList<FormFieldValue> Fields)>();
        var results = new List<BatchItemResult>();
        for (int i = 0; i < inputs.Count; i++)
        {
            progress?.Report(new BatchProgress(i, inputs.Count, inputs[i]));
            if (cancel.IsCancellationRequested) break;
            try
            {
                if (!File.Exists(inputs[i])) { results.Add(new BatchItemResult(inputs[i], null, "O arquivo não existe mais.")); continue; }
                var fields = PdfFormData.Read(inputs[i]);
                if (fields.Count == 0) { results.Add(new BatchItemResult(inputs[i], null, null, "sem formulário")); continue; }
                rows.Add((Path.GetFileName(inputs[i]), fields));
                results.Add(new BatchItemResult(inputs[i], null, null, $"{fields.Count} {(fields.Count == 1 ? "campo" : "campos")}"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                results.Add(new BatchItemResult(inputs[i], null, FriendlyError(ex)));
            }
        }
        progress?.Report(new BatchProgress(results.Count, inputs.Count, ""));
        if (rows.Count == 0) return (results, null);

        string folder = outputFolder ?? Path.GetDirectoryName(Path.GetFullPath(inputs[0]))!;
        Directory.CreateDirectory(folder);
        string temp = Path.Combine(folder, $".pdfstudio-{Guid.NewGuid():N}.tmp");
        try
        {
            File.WriteAllText(temp, PdfFormData.ToCsv(rows), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
            string csv = PageImages.FreePath(folder, fileName);
            File.Move(temp, csv);
            return (results, csv);
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    /// <summary>
    /// "Renomear": cria uma cópia de cada PDF com o nome feito pelo <paramref name="template"/> (veja <see cref="PdfRename"/>).
    /// O original fica como está; nome repetido ganha " (2)".
    /// </summary>
    public static Task<List<BatchItemResult>> CopyRenamed(IReadOnlyList<string> inputs, string? outputFolder, string template,
        IProgress<BatchProgress>? progress = null, CancellationToken cancel = default)
    {
        int position = 0;
        return Loop(inputs, outputFolder, progress, cancel, (input, folder) =>
        {
            position++;
            try
            {
                if (!File.Exists(input)) return Task.FromResult(new BatchItemResult(input, null, "O arquivo não existe mais."));
                string name = PdfRename.NameFor(template, PdfRename.Info(input), position, inputs.Count);
                string target = PageImages.FreePath(folder, name + ".pdf");
                File.Copy(input, target, overwrite: false);
                return Task.FromResult(new BatchItemResult(input, target, null, $"→ {Path.GetFileName(target)}"));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return Task.FromResult(new BatchItemResult(input, null, FriendlyError(ex)));
            }
        });
    }

    // ---------------- Imprimir ----------------

    /// <summary>Por que o arquivo não pode ir para a impressão (null = pode).</summary>
    public static string? CannotPrint(string path)
    {
        if (!File.Exists(path)) return "O arquivo não existe mais.";
        try
        {
            return PdfPasswords.Inspect(path) switch
            {
                PasswordState.OpenPassword => "Tem senha para abrir: remova a senha antes.",
                _ when !PdfPasswords.AllowsPrinting(path) => "Quem criou o PDF não permite imprimir.",
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FriendlyError(ex);
        }
    }

    /// <summary>
    /// Junta os arquivos num só, para uma única janela de impressão. <paramref name="eachOnNewSheet"/>: depois de um
    /// arquivo com número ímpar de páginas entra uma página em branco, assim, em frente e verso, o próximo arquivo
    /// começa numa folha nova. Devolve o total de páginas.
    /// </summary>
    public static int MergeForPrint(IReadOnlyList<string> inputs, string output, bool eachOnNewSheet)
    {
        if (inputs.Count == 0) throw new ArgumentException("Nenhum arquivo para imprimir.", nameof(inputs));
        using var result = new PdfDocument();
        for (int i = 0; i < inputs.Count; i++)
        {
            using var source = PdfOperations.OpenForImport(inputs[i]);
            PdfPage? last = null;
            foreach (var page in source.Pages) last = result.AddPage(page);
            if (eachOnNewSheet && i < inputs.Count - 1 && source.PageCount % 2 == 1 && last is not null)
            {
                var blank = result.AddPage();
                blank.Width = last.Width;
                blank.Height = last.Height;
            }
        }
        result.Info.Creator = "PDF Studio";
        int count = result.PageCount;
        PdfOperations.SaveSafely(result, output);
        return count;
    }
}
