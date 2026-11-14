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

    private static int ParseNumber(string s, string range) =>
        int.TryParse(s, out int n) ? n : throw new FormatException($"\"{range}\" não é um intervalo válido.");
}
