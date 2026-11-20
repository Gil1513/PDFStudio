namespace PDFStudio.Core;

/// <summary>
/// Interpreta intervalos de páginas digitados pelo usuário, como "1-3, 5, 8-" (8 até o fim).
/// Os números são de 1 em diante; o resultado usa índices de 0 em diante.
/// </summary>
public static class PageRanges
{
    public static List<int[]> Parse(string text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Informe ao menos um intervalo, por exemplo: 1-3, 5, 8-10.");

        var result = new List<int[]>();
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int start, end;
            int dash = raw.IndexOf('-');
            if (dash < 0)
            {
                start = end = ParseNumber(raw, raw);
            }
            else
            {
                string a = raw[..dash].Trim(), b = raw[(dash + 1)..].Trim();
                start = a.Length == 0 ? 1 : ParseNumber(a, raw);
                end = b.Length == 0 ? pageCount : ParseNumber(b, raw);
            }

            if (start < 1 || end < 1 || start > pageCount || end > pageCount)
                throw new FormatException($"O intervalo \"{raw}\" está fora do documento (1 a {pageCount}).");
            if (start > end)
                throw new FormatException($"No intervalo \"{raw}\" o início é maior que o fim.");

            result.Add(Enumerable.Range(start - 1, end - start + 1).ToArray());
        }
        if (result.Count == 0)
            throw new FormatException("Informe ao menos um intervalo, por exemplo: 1-3, 5, 8-10.");
        return result;
    }

    /// <summary>
    /// Etapa 8.5 (lote): como <see cref="Parse"/>, mas para arquivos de tamanhos diferentes. Intervalos que passam do
    /// fim são cortados no fim ("3-10" num arquivo de 5 páginas = 3 a 5) e os que começam depois dele são ignorados.
    /// Erro só de escrita ("a-b", "0", "5-2"). Com <paramref name="pageCount"/> = 0 serve só para conferir a escrita.
    /// </summary>
    public static List<int[]> ParseClipped(string text, int pageCount)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new FormatException("Informe ao menos um intervalo, por exemplo: 1-3, 5, 8-10.");
        var result = new List<int[]>();
        int parts = 0;
        foreach (var raw in text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            parts++;
            int start, end;
            int dash = raw.IndexOf('-');
            if (dash < 0) start = end = ParseNumber(raw, raw);
            else
            {
                string a = raw[..dash].Trim(), b = raw[(dash + 1)..].Trim();
                start = a.Length == 0 ? 1 : ParseNumber(a, raw);
                end = b.Length == 0 ? int.MaxValue : ParseNumber(b, raw);
            }
            if (start < 1 || end < 1) throw new FormatException($"No intervalo \"{raw}\": as páginas começam em 1.");
            if (start > end) throw new FormatException($"No intervalo \"{raw}\" o início é maior que o fim.");
            if (start > pageCount) continue;
            end = Math.Min(end, pageCount);
            result.Add(Enumerable.Range(start - 1, end - start + 1).ToArray());
        }
        if (parts == 0) throw new FormatException("Informe ao menos um intervalo, por exemplo: 1-3, 5, 8-10.");
        return result;
    }

    /// <summary>Volta a escrever grupos de páginas (índices de 0) como texto: "1-3, 5".</summary>
    public static string Format(IEnumerable<int[]> groups) =>
        string.Join(", ", groups.Where(g => g.Length > 0).Select(g => g.Length == 1 ? $"{g[0] + 1}" : $"{g[0] + 1}-{g[^1] + 1}"));

    private static int ParseNumber(string s, string range) =>
        int.TryParse(s, out int n) ? n : throw new FormatException($"\"{range}\" não é um intervalo válido.");
}
