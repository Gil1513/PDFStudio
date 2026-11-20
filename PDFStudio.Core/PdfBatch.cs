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
public static class PdfBatch
{
    /// <summary>
    /// Executa <paramref name="operation"/> (entrada, saída) em cada arquivo. A saída vai para
    /// <paramref name="outputFolder"/> (null = a pasta de cada original) com o nome original + <paramref name="suffix"/>
    /// (ex.: "contrato-marca.pdf"). A operação pode devolver um texto curto sobre o resultado (ex.: "3 páginas").
    /// </summary>
    public static List<BatchItemResult> Run(IReadOnlyList<string> inputs, string? outputFolder, string suffix,
        Func<string, string, string?> operation, IProgress<BatchProgress>? progress = null, CancellationToken cancel = default)
    {
        if (outputFolder is not null) Directory.CreateDirectory(outputFolder);
        var results = new List<BatchItemResult>();
        for (int i = 0; i < inputs.Count; i++)
        {
            string input = inputs[i];
            progress?.Report(new BatchProgress(i, inputs.Count, input));
            if (cancel.IsCancellationRequested) break; // "Parar": o arquivo da vez nem começa
            results.Add(RunOne(input, outputFolder ?? Path.GetDirectoryName(Path.GetFullPath(input))!, suffix, operation));
        }
        progress?.Report(new BatchProgress(results.Count, inputs.Count, ""));
        return results;
    }

    private static BatchItemResult RunOne(string input, string outputFolder, string suffix, Func<string, string, string?> operation)
    {
        if (!File.Exists(input)) return new BatchItemResult(input, null, "O arquivo não existe mais.");
        // Grava primeiro num arquivo temporário na mesma pasta; só no fim ganha o nome definitivo.
        string temp = Path.Combine(outputFolder, $".pdfstudio-{Guid.NewGuid():N}.tmp");
        try
        {
            string? detail = operation(input, temp);
            if (!File.Exists(temp)) return new BatchItemResult(input, null, "A operação não gerou um arquivo.");
            string output = PageImages.FreePath(outputFolder, OutputName(input, suffix));
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

    /// <summary>"contrato.pdf" + "-marca" → "contrato-marca.pdf".</summary>
    public static string OutputName(string input, string suffix)
    {
        string clean = string.Concat((suffix ?? "").Where(c => Array.IndexOf(Path.GetInvalidFileNameChars(), c) < 0));
        return Path.GetFileNameWithoutExtension(input) + clean + ".pdf";
    }

    /// <summary>
    /// A pasta de saída não pode ser a mesma dos arquivos se o sufixo estiver vazio: os resultados ficariam com o
    /// mesmo nome dos originais (eles não seriam apagados, mas viraria "nome (2).pdf", o que confunde).
    /// </summary>
    public static bool NeedsSuffix(IEnumerable<string> inputs, string? outputFolder, string suffix) =>
        string.IsNullOrWhiteSpace(suffix) && (outputFolder is null || inputs.Any(i =>
            string.Equals(Path.GetFullPath(Path.GetDirectoryName(i) ?? ""), Path.GetFullPath(outputFolder).TrimEnd('\\', '/'),
                StringComparison.OrdinalIgnoreCase)));

    private static string FriendlyError(Exception ex)
    {
        string m = ex.Message;
        if (ex is PdfPasswordException) return m; // já vem explicada em português
        if (m.Contains("password", StringComparison.OrdinalIgnoreCase) || m.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
            return "O PDF tem senha ou restrições de edição.";
        if (ex is FormatException) return m; // ex.: páginas pedidas que o arquivo não tem
        if (ex is IOException) return "Não foi possível ler ou gravar o arquivo (está aberto em outro programa?).";
        return m;
    }
}
