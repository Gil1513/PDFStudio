using PdfSharp.Pdf;

namespace PDFStudio.Core;

// Etapa 3: ferramentas de página (excluir, extrair, inserir, recortar e redimensionar).
public static partial class PdfOperations
{
    // ---------------- Excluir ----------------

    /// <summary>
    /// Remove as páginas indicadas em <paramref name="ranges"/> (ex.: "2, 5-7") e grava o resultado.
    /// Pode sobrescrever o próprio arquivo de entrada. Retorna quantas páginas sobraram.
    /// </summary>
    public static int DeletePages(string input, string ranges, string output)
    {
        int total = GetPageCount(input);
        var remove = PageRanges.Parse(ranges, total).SelectMany(g => g).ToHashSet();
        if (remove.Count >= total)
            throw new InvalidOperationException("Não é possível excluir todas as páginas: o PDF precisa ter ao menos uma.");

        var keep = Enumerable.Range(0, total).Where(i => !remove.Contains(i)).Select(i => new PageEdit(i)).ToList();
        Reorganize(input, keep, output);
        return keep.Count;
    }

    // ---------------- Extrair ----------------

    /// <summary>
    /// Copia as páginas indicadas (na ordem digitada, sem repetir) para um novo PDF.
    /// O arquivo de entrada não é alterado. Retorna quantas páginas o novo arquivo tem.
    /// </summary>
    public static int ExtractPages(string input, string ranges, string output)
    {
        var pages = DistinctIndices(ranges, GetPageCount(input)).Select(i => new PageEdit(i)).ToList();
        Reorganize(input, pages, output);
        return pages.Count;
    }

    /// <summary>Grava cada página indicada como um PDF separado na pasta. Retorna os arquivos criados.</summary>
    public static List<string> ExtractPagesSeparately(string input, string ranges, string outputFolder)
    {
        using var source = OpenForImport(input);
        var groups = DistinctIndices(ranges, source.PageCount).Select(i => new[] { i }).ToList();
        int digits = source.PageCount.ToString().Length;
        return WriteGroups(source, input, outputFolder, groups, g => $"pagina_{(g[0] + 1).ToString().PadLeft(digits, '0')}");
    }

    // ---------------- Utilitários ----------------

    /// <summary>Índices (0..n-1) citados nos intervalos, na ordem digitada e sem repetição.</summary>
    internal static List<int> DistinctIndices(string ranges, int pageCount) =>
        PageRanges.Parse(ranges, pageCount).SelectMany(g => g).Distinct().ToList();
}
