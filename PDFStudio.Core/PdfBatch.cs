namespace PDFStudio.Core;

/// <summary>Resultado de um arquivo do lote: deu certo (com o arquivo criado) ou o motivo do erro.</summary>
public sealed record BatchItemResult(string Input, string? Output, string? Error, string? Detail = null)
{
    public bool Ok => Error is null;
}

/// <summary>Andamento do lote: quantos já terminaram, o total e o arquivo da vez.</summary>
public readonly record struct BatchProgress(int Done, int Total, string CurrentFile);

/// <summary>
/// Etapa 8: PDFs em lote. Aplica a mesma operação (comprimir, marca d'água, fundo, cabeçalho/rodapé…) em vários
/// arquivos. Regras de segurança: os originais nunca são alterados, nenhum arquivo existente é sobrescrito
/// (o nome ganha " (2)", " (3)"…) e um arquivo com problema não interrompe os outros.
/// </summary>
public static partial class PdfBatch
{
    /// <summary>
    /// Executa <paramref name="operation"/> (entrada, saída) em cada arquivo. A saída vai para
    /// <paramref name="outputFolder"/> (null = a pasta de cada original) com o nome original + <paramref name="suffix"/>
    /// (ex.: "contrato-marca.pdf"). A operação pode devolver um texto curto sobre o resultado (ex.: "3 páginas").
    /// Se a operação não gravar nada, o arquivo foi pulado de propósito (ex.: OCR num PDF que já tem texto).
    /// </summary>
    public static List<BatchItemResult> Run(IReadOnlyList<string> inputs, string? outputFolder, string suffix,
        Func<string, string, string?> operation, IProgress<BatchProgress>? progress = null, CancellationToken cancel = default,
        string extension = ".pdf") =>
        RunAsync(inputs, outputFolder, suffix, (i, o, _) => Task.FromResult(operation(i, o)), progress, cancel, extension)
            .GetAwaiter().GetResult();

    /// <summary>
    /// Como <see cref="Run"/>, para operações assíncronas (OCR, desenho de páginas pelo Windows). A operação recebe o
    /// cancelamento para poder parar no meio de um arquivo grande; nesse caso nada daquele arquivo é gravado.
    /// <paramref name="extension"/>: tipo do arquivo criado (".pdf", ".docx"…).
    /// </summary>
    public static Task<List<BatchItemResult>> RunAsync(IReadOnlyList<string> inputs, string? outputFolder, string suffix,
        Func<string, string, CancellationToken, Task<string?>> operation, IProgress<BatchProgress>? progress = null,
        CancellationToken cancel = default, string extension = ".pdf") =>
        Loop(inputs, outputFolder, progress, cancel, (input, folder) => RunOneAsync(input, folder, suffix, extension, operation, cancel));

    /// <summary>
    /// Para operações que criam vários arquivos por entrada (ex.: uma imagem por página). A operação grava numa pasta
    /// temporária o que quiser, com os nomes finais; no fim cada arquivo vai para a pasta de saída sem sobrescrever
    /// nada. Com erro (ou Parar no meio), nada daquela entrada fica. <see cref="BatchItemResult.Output"/> = o 1º arquivo.
    /// </summary>
    public static Task<List<BatchItemResult>> RunToFolderAsync(IReadOnlyList<string> inputs, string? outputFolder,
        Func<string, string, CancellationToken, Task<string?>> operation, IProgress<BatchProgress>? progress = null,
        CancellationToken cancel = default) =>
        Loop(inputs, outputFolder, progress, cancel, (input, folder) => RunManyAsync(input, folder, operation, cancel));

    private static async Task<List<BatchItemResult>> Loop(IReadOnlyList<string> inputs, string? outputFolder,
        IProgress<BatchProgress>? progress, CancellationToken cancel, Func<string, string, Task<BatchItemResult>> one)
    {
        if (outputFolder is not null) Directory.CreateDirectory(outputFolder);
        var results = new List<BatchItemResult>();
        for (int i = 0; i < inputs.Count; i++)
        {
            string input = inputs[i];
            progress?.Report(new BatchProgress(i, inputs.Count, input));
            if (cancel.IsCancellationRequested) break; // "Parar": o arquivo da vez nem começa
            results.Add(await one(input, outputFolder ?? Path.GetDirectoryName(Path.GetFullPath(input))!));
        }
        progress?.Report(new BatchProgress(results.Count, inputs.Count, ""));
        return results;
    }

    private static async Task<BatchItemResult> RunOneAsync(string input, string outputFolder, string suffix, string extension,
        Func<string, string, CancellationToken, Task<string?>> operation, CancellationToken cancel)
    {
        if (!File.Exists(input)) return new BatchItemResult(input, null, "O arquivo não existe mais.");
        // Grava primeiro num arquivo temporário na mesma pasta; só no fim ganha o nome definitivo.
        string temp = Path.Combine(outputFolder, $".pdfstudio-{Guid.NewGuid():N}.tmp");
        try
        {
            string? detail = await operation(input, temp, cancel);
            if (!File.Exists(temp)) return new BatchItemResult(input, null, null, detail ?? "nada a fazer"); // pulado
            string output = PageImages.FreePath(outputFolder, OutputName(input, suffix, extension));
            File.Move(temp, output);
            return new BatchItemResult(input, output, null, detail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new BatchItemResult(input, null, FriendlyError(ex));
        }
        finally
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch (IOException) { }
        }
    }

    private static async Task<BatchItemResult> RunManyAsync(string input, string outputFolder,
        Func<string, string, CancellationToken, Task<string?>> operation, CancellationToken cancel)
    {
        if (!File.Exists(input)) return new BatchItemResult(input, null, "O arquivo não existe mais.");
        string temp = Path.Combine(outputFolder, $".pdfstudio-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(temp);
            string? detail = await operation(input, temp, cancel);
            var made = Directory.GetFiles(temp).Order(StringComparer.OrdinalIgnoreCase).ToList();
            if (made.Count == 0) return new BatchItemResult(input, null, null, detail ?? "nada a fazer");
            string? first = null;
            foreach (var file in made)
            {
                string target = PageImages.FreePath(outputFolder, Path.GetFileName(file));
                File.Move(file, target);
                first ??= target;
            }
            return new BatchItemResult(input, first, null, detail);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return new BatchItemResult(input, null, FriendlyError(ex));
        }
        finally
        {
            try { if (Directory.Exists(temp)) Directory.Delete(temp, recursive: true); } catch (IOException) { }
        }
    }

    /// <summary>"contrato.pdf" + "-marca" → "contrato-marca.pdf".</summary>
    public static string OutputName(string input, string suffix, string extension = ".pdf")
    {
        string clean = string.Concat((suffix ?? "").Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0));
        return Path.GetFileNameWithoutExtension(input) + clean + extension;
    }

    /// <summary>
    /// A pasta de saída não pode ser a mesma dos arquivos se o sufixo estiver vazio: os resultados ficariam com o
    /// mesmo nome dos originais (eles não seriam apagados, mas viraria "nome (2).pdf", o que confunde).
    /// </summary>
    public static bool NeedsSuffix(IEnumerable<string> inputs, string? outputFolder, string suffix, string extension = ".pdf") =>
        string.IsNullOrWhiteSpace(suffix) && inputs.Any(i => Path.GetExtension(i).Equals(extension, StringComparison.OrdinalIgnoreCase)) &&
        (outputFolder is null || inputs.Any(i =>
            string.Equals(Path.GetFullPath(Path.GetDirectoryName(i) ?? ""), Path.GetFullPath(outputFolder).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase)));

    private static string FriendlyError(Exception ex)
    {
        string m = ex.Message;
        if (ex is PdfPasswordException) return m; // já vem explicada em português
        if (m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
            return "O PDF tem senha ou restrições de edição.";
        if (ex is FormatException) return m; // ex.: páginas pedidas que o arquivo não tem
        if (ex is OperationCanceledException) return "Parado no meio: nada deste arquivo foi gravado.";
        if (ex is IOException) return "Não foi possível ler ou gravar o arquivo (está aberto em outro programa?).";
        return m;
    }
}
