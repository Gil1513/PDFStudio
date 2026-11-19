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
    /// Resolução da imagem de cada página (pontos por polegada): menor em documentos grandes, porque a janela de
    /// impressão do Windows guarda todas as páginas na memória até terminar.
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
}
