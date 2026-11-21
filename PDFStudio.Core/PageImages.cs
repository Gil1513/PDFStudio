namespace PDFStudio.Core;

/// <summary>Formato das imagens geradas a partir das páginas.</summary>
public enum PageImageFormat
{
    /// <summary>PNG: sem perda, bom para texto e desenhos.</summary>
    Png,
    /// <summary>JPG: arquivo menor, bom para fotos.</summary>
    Jpeg,
}

/// <summary>
/// Etapa 7.4 (PDF→imagem): as contas e os nomes. O desenho da página em si é feito no app, pelo motor de PDF
/// do Windows; aqui fica o que dá para testar: quais páginas, tamanho em pixels e nome de cada arquivo.
/// </summary>
public static class PageImages
{
    /// <summary>Lado máximo da imagem, em pixels (acima disso a memória do Windows pode não dar conta).</summary>
    public const int MaxSide = 10000;

    /// <summary>Resoluções oferecidas: tela, padrão e impressão (pontos por polegada).</summary>
    public static readonly int[] Resolutions = [96, 150, 300];

    public static string Extension(PageImageFormat format) => format == PageImageFormat.Png ? ".png" : ".jpg";

    /// <summary>
    /// Páginas escolhidas (índices de 0 em diante, em ordem e sem repetir). Texto vazio = todas.
    /// Lança <see cref="FormatException"/> com a explicação se o texto estiver errado.
    /// </summary>
    public static List<int> Pages(string? ranges, int pageCount) =>
        string.IsNullOrWhiteSpace(ranges)
            ? Enumerable.Range(0, pageCount).ToList()
            : PageRanges.Parse(ranges, pageCount).SelectMany(r => r).Distinct().Order().ToList();

    /// <summary>
    /// Tamanho em pixels de uma página de <paramref name="widthDips"/> × <paramref name="heightDips"/> DIPs
    /// (1/96 pol., a medida do motor do Windows) na resolução <paramref name="dpi"/>. Página enorme é reduzida
    /// (mantendo a proporção) para nenhum lado passar de <see cref="MaxSide"/>.
    /// </summary>
    public static (uint Width, uint Height) PixelSize(double widthDips, double heightDips, int dpi)
    {
        double scale = dpi / 96.0;
        double w = widthDips * scale, h = heightDips * scale;
        double biggest = Math.Max(w, h);
        if (biggest > MaxSide) { w *= MaxSide / biggest; h *= MaxSide / biggest; }
        return ((uint)Math.Max(1, Math.Round(w)), (uint)Math.Max(1, Math.Round(h)));
    }

    /// <summary>Teto de pixels de uma página desenhada na tela (~4096 × 4096): uma página gigante não esgota a memória.</summary>
    public const long MaxScreenPixels = 16_777_216;

    /// <summary>
    /// Tamanho em pixels de uma página desenhada na tela com <paramref name="scale"/> (2 = nítido com zoom). Se
    /// passar de <see cref="MaxScreenPixels"/> ou de <see cref="MaxSide"/> num lado, diminui mantendo a proporção.
    /// </summary>
    public static (uint Width, uint Height) ScreenSize(double widthDips, double heightDips, double scale)
    {
        double w = Math.Max(1, widthDips * scale), h = Math.Max(1, heightDips * scale);
        if (!double.IsFinite(w) || !double.IsFinite(h)) return (1, 1);
        double shrink = Math.Min(1, Math.Sqrt(MaxScreenPixels / (w * h)));
        shrink = Math.Min(shrink, MaxSide / Math.Max(w, h));
        return ((uint)Math.Max(1, Math.Round(w * shrink)), (uint)Math.Max(1, Math.Round(h * shrink)));
    }

    /// <summary>
    /// Nome do arquivo de uma página: "contrato-pagina-03.png". O número tem tantos dígitos quanto o total
    /// de páginas (assim a lista de arquivos fica em ordem). Caracteres proibidos no nome viram "_".
    /// </summary>
    public static string FileName(string documentName, int pageNumber, int pageCount, PageImageFormat format)
    {
        string baseName = Path.GetFileNameWithoutExtension(documentName);
        foreach (char c in Path.GetInvalidFileNameChars()) baseName = baseName.Replace(c, '_');
        if (baseName.Trim().Length == 0) baseName = "documento";
        int digits = Math.Max(2, pageCount.ToString().Length);
        return $"{baseName}-pagina-{pageNumber.ToString().PadLeft(digits, '0')}{Extension(format)}";
    }

    /// <summary>Caminho na pasta que não apaga nada: se o nome já existe, vira "nome (2).png", "nome (3).png"…</summary>
    public static string FreePath(string folder, string fileName)
    {
        string path = Path.Combine(folder, fileName);
        string stem = Path.GetFileNameWithoutExtension(fileName), ext = Path.GetExtension(fileName);
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{stem} ({n}){ext}");
        return path;
    }
}
