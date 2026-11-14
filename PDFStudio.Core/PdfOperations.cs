using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Operações de manipulação de PDF (PDFsharp 6.2.4, licença MIT).</summary>
public static class PdfOperations
{
    public static int GetPageCount(string path)
    {
        using var doc = OpenForImport(path);
        return doc.PageCount;
    }

    // ---------------- Juntar ----------------

    /// <summary>Junta os PDFs na ordem dada em um único arquivo.</summary>
    public static int Merge(IReadOnlyList<string> inputs, string output)
    {
        if (inputs.Count == 0) throw new ArgumentException("Selecione ao menos um PDF.", nameof(inputs));

        using var result = new PdfDocument();
        foreach (var input in inputs)
        {
            using var source = OpenForImport(input);
            foreach (var page in source.Pages) result.AddPage(page);
        }
        result.Info.Creator = "PDF Studio";
        int pageCount = result.PageCount; // após Save o documento não pode mais ser consultado
        SaveSafely(result, output);
        return pageCount;
    }

    // ---------------- Dividir ----------------

    /// <summary>Gera um arquivo por página. Retorna os caminhos criados.</summary>
    public static List<string> SplitEveryPage(string input, string outputFolder)
    {
        using var source = OpenForImport(input);
        var groups = Enumerable.Range(0, source.PageCount).Select(i => new[] { i }).ToList();
        int digits = source.PageCount.ToString().Length;
        return WriteGroups(source, input, outputFolder, groups, g => $"pagina_{(g[0] + 1).ToString().PadLeft(digits, '0')}");
    }

    /// <summary>Gera um arquivo por intervalo, ex.: "1-3, 5, 8-". Retorna os caminhos criados.</summary>
    public static List<string> SplitByRanges(string input, string ranges, string outputFolder)
    {
        using var source = OpenForImport(input);
        var groups = PageRanges.Parse(ranges, source.PageCount);
        return WriteGroups(source, input, outputFolder, groups,
            g => g.Length == 1 ? $"pagina_{g[0] + 1}" : $"paginas_{g[0] + 1}-{g[^1] + 1}");
    }

    private static List<string> WriteGroups(PdfDocument source, string input, string outputFolder,
        List<int[]> groups, Func<int[], string> suffix)
    {
        Directory.CreateDirectory(outputFolder);
        string baseName = Path.GetFileNameWithoutExtension(input);
        var created = new List<string>();
        foreach (var group in groups)
        {
            using var part = new PdfDocument();
            CopyInfo(source, part);
            foreach (int index in group) part.AddPage(source.Pages[index]);

            string path = UniquePath(Path.Combine(outputFolder, $"{baseName}_{suffix(group)}.pdf"));
            part.Save(path);
            created.Add(path);
        }
        return created;
    }

    // ---------------- Girar e reordenar ----------------

    /// <summary>
    /// Gera um novo PDF com as páginas na ordem de <paramref name="pages"/>, aplicando a rotação
    /// extra de cada uma (múltiplos de 90°, sentido horário). Pode sobrescrever o próprio arquivo de entrada.
    /// </summary>
    public static void Reorganize(string input, IReadOnlyList<PageEdit> pages, string output)
    {
        if (pages.Count == 0) throw new ArgumentException("O documento precisa ter ao menos uma página.", nameof(pages));
        foreach (var edit in pages)
            if (edit.Rotation % 90 != 0)
                throw new ArgumentException("A rotação deve ser múltipla de 90°.", nameof(pages));

        using var source = OpenForImport(input);
        using var result = new PdfDocument();
        CopyInfo(source, result);

        foreach (var edit in pages)
        {
            if (edit.SourceIndex < 0 || edit.SourceIndex >= source.PageCount)
                throw new ArgumentOutOfRangeException(nameof(pages), $"A página {edit.SourceIndex + 1} não existe.");

            var page = result.AddPage(source.Pages[edit.SourceIndex]);
            page.Rotate = Normalize(page.Rotate + edit.Rotation);
        }
        SaveSafely(result, output);
    }

    /// <summary>Normaliza um ângulo para 0, 90, 180 ou 270.</summary>
    public static int Normalize(int degrees) => ((degrees % 360) + 360) % 360;

    // ---------------- Utilitários ----------------

    internal static PdfDocument OpenForImport(string path)
    {
        try
        {
            return PdfReader.Open(path, PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(path)}\" está protegido por senha.", ex);
        }
    }

    private static void CopyInfo(PdfDocument from, PdfDocument to)
    {
        to.Info.Title = from.Info.Title;
        to.Info.Author = from.Info.Author;
        to.Info.Subject = from.Info.Subject;
        to.Info.Keywords = from.Info.Keywords;
        to.Info.Creator = "PDF Studio";
    }

    /// <summary>
    /// Salva em um arquivo temporário e só depois substitui o destino. Assim é possível
    /// gravar por cima de um dos arquivos de entrada sem corrompê-lo.
    /// </summary>
    internal static void SaveSafely(PdfDocument doc, string output)
    {
        string full = Path.GetFullPath(output);
        string temp = full + ".tmp";
        try
        {
            doc.Save(temp);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        string dir = Path.GetDirectoryName(path)!, name = Path.GetFileNameWithoutExtension(path), ext = Path.GetExtension(path);
        for (int i = 2; ; i++)
        {
            string candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate)) return candidate;
        }
    }
}
