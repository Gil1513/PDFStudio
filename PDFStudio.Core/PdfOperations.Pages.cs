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

    // ---------------- Utilitários ----------------

    /// <summary>Índices (0..n-1) citados nos intervalos, na ordem digitada e sem repetição.</summary>
    internal static List<int> DistinctIndices(string ranges, int pageCount) =>
        PageRanges.Parse(ranges, pageCount).SelectMany(g => g).Distinct().ToList();
}
