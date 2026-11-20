namespace PDFStudio.Core;

/// <summary>Onde a página fica no papel: retângulo (mesma unidade da área) e se foi girada 90°.</summary>
public readonly record struct PrintPlacement(double X, double Y, double Width, double Height, bool Rotated);

/// <summary>Etapa 7.3: contas da impressão (posição da página no papel e resolução).</summary>
public static class PrintLayout
{
    /// <summary>
    /// Põe a página (<paramref name="pageWidth"/> × <paramref name="pageHeight"/>) na área imprimível do papel,
    /// centralizada. <paramref name="fit"/>: aumenta ou diminui para caber inteira; senão, tamanho real.
    /// <paramref name="autoRotate"/>: página deitada em papel em pé (ou o contrário) é girada 90° para aproveitar o papel.
    /// </summary>
    public static PrintPlacement Place(double pageWidth, double pageHeight, double areaX, double areaY, double areaWidth, double areaHeight,
        bool fit, bool autoRotate = true)
    {
        if (pageWidth <= 0 || pageHeight <= 0 || areaWidth <= 0 || areaHeight <= 0)
            throw new ArgumentException("Tamanhos de página e papel precisam ser positivos.");
        bool rotate = autoRotate && pageWidth != pageHeight && areaWidth != areaHeight
                      && (pageWidth > pageHeight) != (areaWidth > areaHeight);
        double w = rotate ? pageHeight : pageWidth, h = rotate ? pageWidth : pageHeight; // como fica no papel
        double scale = fit ? Math.Min(areaWidth / w, areaHeight / h) : 1;
        w *= scale;
        h *= scale;
        return new PrintPlacement(areaX + (areaWidth - w) / 2, areaY + (areaHeight - h) / 2, w, h, rotate);
    }

    /// <summary>
    /// Resolução da imagem de cada página (pontos por polegada): menor em documentos grandes, para a fila da
    /// impressora não ficar enorme (cada folha vai como uma imagem).
    /// </summary>
    public static int Dpi(int pages) => pages <= 30 ? 200 : pages <= 100 ? 150 : 110;

    /// <summary>
    /// Páginas escolhidas (começando em 0), na ordem e sem repetir, a partir dos intervalos 1… da janela do Windows.
    /// Sem intervalos = todas. Números fora do documento são ignorados.
    /// </summary>
    public static List<int> Pages(int pageCount, IEnumerable<(int First, int Last)>? ranges)
    {
        var list = ranges?.ToList();
        if (list is null || list.Count == 0) return Enumerable.Range(0, pageCount).ToList();
        var result = new List<int>();
        var seen = new HashSet<int>();
        foreach (var (first, last) in list)
        {
            int a = Math.Min(first, last), b = Math.Max(first, last);
            for (int p = Math.Max(1, a); p <= Math.Min(pageCount, b); p++)
                if (seen.Add(p - 1)) result.Add(p - 1);
        }
        return result;
    }

    /// <summary>Só as páginas de número ímpar (1, 3, 5…) ou par, mantendo a ordem. Índices começam em 0.</summary>
    public static List<int> Parity(IEnumerable<int> pages, PageParity parity) => parity switch
    {
        PageParity.Odd => pages.Where(p => p % 2 == 0).ToList(),
        PageParity.Even => pages.Where(p => p % 2 == 1).ToList(),
        _ => pages.ToList(),
    };

    /// <summary>
    /// A ordem das folhas a imprimir (índices das páginas; null = lado em branco). As cópias são feitas por nós,
    /// não pela impressora (nem todo driver faz cópias).
    /// <paramref name="collate"/> (agrupar): 1,2,3, 1,2,3… Sem agrupar: 1,1, 2,2, 3,3…
    /// <paramref name="duplex"/>: cada cópia agrupada começa numa folha nova (lado em branco depois de um número
    /// ímpar de páginas); sem agrupar, cada folha (frente e verso) é repetida, e não cada página.
    /// </summary>
    public static List<int?> Sequence(IReadOnlyList<int> pages, int copies, bool collate, bool duplex)
    {
        var result = new List<int?>();
        if (pages.Count == 0 || copies < 1) return result;
        if (collate || copies == 1)
        {
            for (int c = 0; c < copies; c++)
            {
                result.AddRange(pages.Select(p => (int?)p));
                if (duplex && pages.Count % 2 == 1 && c < copies - 1) result.Add(null);
            }
            return result;
        }
        if (!duplex)
        {
            foreach (int p in pages)
                for (int c = 0; c < copies; c++) result.Add(p);
            return result;
        }
        for (int i = 0; i < pages.Count; i += 2)
        {
            bool last = i + 2 >= pages.Count;
            for (int c = 0; c < copies; c++)
            {
                result.Add(pages[i]);
                if (i + 1 < pages.Count) result.Add(pages[i + 1]);
                else if (!(last && c == copies - 1)) result.Add(null); // o verso fica em branco
            }
        }
        return result;
    }

    /// <summary>
    /// Onde a página fica na folha da impressora, em pixels da impressora, contados a partir do canto da área
    /// imprimível (o zero do desenho). <paramref name="fit"/>: ajusta à área imprimível; senão, tamanho real
    /// centralizado no papel inteiro (pode cortar nas margens que a impressora não alcança).
    /// </summary>
    public static PrintPlacement PlaceOnSheet(SheetMetrics sheet, double pageWidthInches, double pageHeightInches, bool fit)
    {
        double dx = sheet.DpiX, dy = sheet.DpiY;
        var p = fit
            ? Place(pageWidthInches, pageHeightInches, 0, 0, sheet.PrintableWidth / dx, sheet.PrintableHeight / dy, true)
            : Place(pageWidthInches, pageHeightInches, -sheet.OffsetX / dx, -sheet.OffsetY / dy,
                sheet.PhysicalWidth / dx, sheet.PhysicalHeight / dy, false);
        return new PrintPlacement(p.X * dx, p.Y * dy, p.Width * dx, p.Height * dy, p.Rotated);
    }
}

public enum PageParity { All, Odd, Even }

/// <summary>
/// Medidas da folha numa impressora (pixels da impressora): resolução, papel inteiro, onde começa a área
/// imprimível (margem que a impressora não alcança) e o tamanho dessa área.
/// </summary>
public readonly record struct SheetMetrics(int DpiX, int DpiY, int PhysicalWidth, int PhysicalHeight,
    int OffsetX, int OffsetY, int PrintableWidth, int PrintableHeight);
