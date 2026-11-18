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

    // ---------------- Inserir ----------------

    /// <summary>
    /// Insere <paramref name="count"/> páginas em branco. <paramref name="position"/> é quantas páginas
    /// ficam antes delas (0 = no início; total = no fim). Sem <paramref name="size"/>, as páginas novas
    /// copiam o tamanho e a orientação (como aparecem na tela) da página vizinha.
    /// Retorna o total de páginas do resultado.
    /// </summary>
    public static int InsertBlankPages(string input, int position, int count, string output,
        PdfSharp.PageSize? size = null, bool landscape = false)
    {
        if (count is < 1 or > 1000) throw new ArgumentOutOfRangeException(nameof(count), "Escolha de 1 a 1000 páginas.");

        using var source = OpenForImport(input);
        CheckPosition(position, source.PageCount);

        double width, height;
        if (size is { } chosen)
        {
            var s = PdfSharp.PageSizeConverter.ToSize(chosen);
            (width, height) = landscape ? (s.Height, s.Width) : (s.Width, s.Height);
        }
        else
        {
            var neighbor = source.Pages[Math.Max(0, position - 1)];
            (width, height) = VisualSize(neighbor);
        }

        return Assemble(source, position, output, result =>
        {
            for (int i = 0; i < count; i++)
                result.AddPage().MediaBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(0, 0), new PdfSharp.Drawing.XPoint(width, height));
        });
    }

    /// <summary>
    /// Insere páginas de outro PDF (todas, ou só as de <paramref name="otherRanges"/>) na posição indicada.
    /// O outro arquivo pode ser o próprio documento. Retorna o total de páginas do resultado.
    /// </summary>
    public static int InsertPagesFromPdf(string input, string otherPdf, string? otherRanges, int position, string output)
    {
        using var source = OpenForImport(input);
        CheckPosition(position, source.PageCount);
        using var other = OpenForImport(otherPdf);
        var pages = string.IsNullOrWhiteSpace(otherRanges)
            ? Enumerable.Range(0, other.PageCount).ToList()
            : PageRanges.Parse(otherRanges, other.PageCount).SelectMany(g => g).ToList();

        return Assemble(source, position, output, result =>
        {
            foreach (int i in pages) result.AddPage(other.Pages[i]);
        });
    }

    /// <summary>Copia as páginas de <paramref name="source"/> com um "recheio" inserido na posição dada.</summary>
    private static int Assemble(PdfDocument source, int position, string output, Action<PdfDocument> insert)
    {
        using var result = new PdfDocument();
        CopyInfo(source, result);
        for (int i = 0; i < position; i++) result.AddPage(source.Pages[i]);
        insert(result);
        for (int i = position; i < source.PageCount; i++) result.AddPage(source.Pages[i]);
        int total = result.PageCount;
        SaveSafely(result, output);
        return total;
    }

    private static void CheckPosition(int position, int pageCount)
    {
        if (position < 0 || position > pageCount)
            throw new ArgumentOutOfRangeException(nameof(position), $"Posição inválida: o documento tem {pageCount} páginas.");
    }

    // ---------------- Recortar ----------------

    /// <summary>Menor lado (em pontos, ~1,3 cm) que uma página pode ter depois do recorte.</summary>
    public const double MinCroppedSide = 36;

    /// <summary>
    /// Recorta as páginas (todas, ou só as de <paramref name="ranges"/>) tirando as margens indicadas,
    /// medidas como a página aparece na tela. Só a área visível (/CropBox) muda: o conteúdo cortado
    /// continua no arquivo e volta com <see cref="RemoveCrop"/>.
    /// </summary>
    public static void CropPages(string input, string? ranges, PageMargins margins, string output)
    {
        if (margins.Top < 0 || margins.Right < 0 || margins.Bottom < 0 || margins.Left < 0)
            throw new ArgumentException("As margens não podem ser negativas.", nameof(margins));

        EditPages(input, ranges, output, (page, number) =>
        {
            var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
            double x1 = Math.Min(box.X1, box.X2), x2 = Math.Max(box.X1, box.X2);
            double y1 = Math.Min(box.Y1, box.Y2), y2 = Math.Max(box.Y1, box.Y2);

            // Converte as margens "da tela" para as bordas da página sem rotação.
            var (top, right, bottom, left) = Normalize(page.Rotate) switch
            {
                90 => (margins.Right, margins.Bottom, margins.Left, margins.Top),
                180 => (margins.Bottom, margins.Left, margins.Top, margins.Right),
                270 => (margins.Left, margins.Top, margins.Right, margins.Bottom),
                _ => (margins.Top, margins.Right, margins.Bottom, margins.Left),
            };

            x1 += left; x2 -= right; y1 += bottom; y2 -= top;
            if (x2 - x1 < MinCroppedSide || y2 - y1 < MinCroppedSide)
                throw new InvalidOperationException($"As margens são grandes demais para a página {number}: sobraria quase nada dela.");

            page.CropBox = new PdfRectangle(new PdfSharp.Drawing.XPoint(x1, y1), new PdfSharp.Drawing.XPoint(x2, y2));
        });
    }

    /// <summary>Desfaz qualquer recorte: a página volta a mostrar toda a área original (/MediaBox).</summary>
    public static void RemoveCrop(string input, string? ranges, string output) =>
        EditPages(input, ranges, output, (page, _) =>
        {
            foreach (var key in new[] { "/CropBox", "/BleedBox", "/TrimBox", "/ArtBox" }) page.Elements.Remove(key);
        });

    /// <summary>
    /// Copia todas as páginas e aplica <paramref name="edit"/> nas escolhidas (todas quando
    /// <paramref name="ranges"/> está vazio). O segundo argumento de <paramref name="edit"/> é o número da página.
    /// </summary>
    private static void EditPages(string input, string? ranges, string output, Action<PdfPage, int> edit)
    {
        using var source = OpenForImport(input);
        var chosen = string.IsNullOrWhiteSpace(ranges)
            ? Enumerable.Range(0, source.PageCount).ToHashSet()
            : DistinctIndices(ranges, source.PageCount).ToHashSet();

        using var result = new PdfDocument();
        CopyInfo(source, result);
        for (int i = 0; i < source.PageCount; i++)
        {
            var page = result.AddPage(source.Pages[i]);
            if (chosen.Contains(i)) edit(page, i + 1);
        }
        SaveSafely(result, output);
    }

    // ---------------- Utilitários ----------------

    /// <summary>Largura e altura (em pontos) da página como ela aparece na tela, considerando a rotação.</summary>
    public static (double Width, double Height) VisualSize(PdfPage page)
    {
        var box = page.Elements.ContainsKey("/CropBox") ? page.CropBox : page.MediaBox;
        return Normalize(page.Rotate) is 90 or 270 ? (box.Height, box.Width) : (box.Width, box.Height);
    }

    /// <summary>Índices (0..n-1) citados nos intervalos, na ordem digitada e sem repetição.</summary>
    internal static List<int> DistinctIndices(string ranges, int pageCount) =>
        PageRanges.Parse(ranges, pageCount).SelectMany(g => g).Distinct().ToList();
}
