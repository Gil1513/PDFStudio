using System.Globalization;
using System.Text;

namespace PDFStudio.Core;

/// <summary>Um retângulo na tela (pontos, canto superior esquerdo da página visível).</summary>
public readonly record struct SearchBox(double X, double Y, double Width, double Height);

/// <summary>
/// Um resultado da pesquisa: página (começa em 0), o trecho em volta (para a lista) e os retângulos
/// que cobrem o texto achado (um por linha).
/// </summary>
public sealed record SearchHit(int Page, string Before, string Match, string After, IReadOnlyList<SearchBox> Boxes);

/// <summary>Opções da pesquisa. Por padrão ignora maiúsculas e acentos ("acao" acha "Ação").</summary>
public sealed record SearchOptions(bool MatchCase = false, bool WholeWord = false, bool MatchAccents = false);

/// <summary>
/// Etapa 7.1: pesquisar no documento. Junta as palavras de cada página (em ordem de leitura, separadas por
/// um espaço, inclusive de uma linha para a outra) e procura o texto nelas. Assim uma frase que quebra de linha
/// também é achada. PDF escaneado (só imagem) não tem texto: precisa de OCR (Etapa 7.7).
/// </summary>
public static class PdfSearch
{
    /// <summary>Limite de resultados (documentos enormes com uma letra só pesquisada).</summary>
    public const int MaxHits = 5000;

    private const int ContextLength = 40;

    public static List<SearchHit> Find(string path, string query, SearchOptions? options = null, CancellationToken cancel = default) =>
        Find(PdfTextLayout.AllWords(path, cancel), query, options, cancel);

    public static List<SearchHit> Find(IReadOnlyList<IReadOnlyList<TextWord>> pages, string query, SearchOptions? options = null,
        CancellationToken cancel = default)
    {
        options ??= new SearchOptions();
        var hits = new List<SearchHit>();
        string wanted = Fold(string.Join(' ', query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)), options);
        if (wanted.Length == 0) return hits;

        for (int p = 0; p < pages.Count && hits.Count < MaxHits; p++)
        {
            cancel.ThrowIfCancellationRequested();
            var words = pages[p];
            if (words.Count == 0) continue;

            // Texto da página e, para cada letra, de que palavra ela é (-1 = espaço entre palavras).
            var text = new StringBuilder();
            var owner = new List<int>();
            var offset = new List<int>();
            for (int w = 0; w < words.Count; w++)
            {
                if (w > 0) { text.Append(' '); owner.Add(-1); offset.Add(0); }
                for (int c = 0; c < words[w].Text.Length; c++) { owner.Add(w); offset.Add(c); }
                text.Append(words[w].Text);
            }
            string original = text.ToString();
            string folded = Fold(original, options);

            for (int start = folded.IndexOf(wanted, StringComparison.Ordinal); start >= 0 && hits.Count < MaxHits;
                 start = folded.IndexOf(wanted, start + 1, StringComparison.Ordinal))
            {
                int end = start + wanted.Length; // exclusivo
                if (options.WholeWord && (IsWordChar(folded, start - 1) || IsWordChar(folded, end))) continue;
                hits.Add(new SearchHit(p,
                    Context(original, Math.Max(0, start - ContextLength), start, before: true),
                    original[start..end],
                    Context(original, end, Math.Min(original.Length, end + ContextLength), before: false),
                    Boxes(words, owner, offset, start, end)));
            }
        }
        return hits;
    }

    /// <summary>Retângulos do trecho [start, end): parte de cada palavra, juntando as da mesma linha.</summary>
    private static List<SearchBox> Boxes(IReadOnlyList<TextWord> words, List<int> owner, List<int> offset, int start, int end)
    {
        var boxes = new List<(int Line, double X1, double Y1, double X2, double Y2)>();
        int i = start;
        while (i < end)
        {
            int w = owner[i];
            if (w < 0) { i++; continue; }
            int from = offset[i];
            while (i < end && owner[i] == w) i++;
            int to = offset[i - 1] + 1;
            var word = words[w];
            // A largura de cada letra não é guardada: divide a palavra em partes iguais (boa aproximação).
            double letter = word.Width / Math.Max(1, word.Text.Length);
            double x1 = word.X + from * letter, x2 = word.X + to * letter;
            if (boxes.Count > 0 && boxes[^1].Line == word.Line)
            {
                var last = boxes[^1];
                boxes[^1] = (last.Line, Math.Min(last.X1, x1), Math.Min(last.Y1, word.Y), Math.Max(last.X2, x2), Math.Max(last.Y2, word.Bottom));
            }
            else boxes.Add((word.Line, x1, word.Y, x2, word.Bottom));
        }
        return boxes.Select(b => new SearchBox(b.X1, b.Y1, b.X2 - b.X1, b.Y2 - b.Y1)).ToList();
    }

    private static string Context(string text, int from, int to, bool before)
    {
        string part = text[from..to];
        if (before && from > 0) part = "…" + part;
        if (!before && to < text.Length) part += "…";
        return part;
    }

    private static bool IsWordChar(string text, int index) =>
        index >= 0 && index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_');

    /// <summary>
    /// Deixa o texto comparável sem mudar o tamanho (uma letra vira uma letra): tira acentos e passa para
    /// minúsculas, conforme as opções. Assim as posições achadas valem para o texto original.
    /// </summary>
    internal static string Fold(string text, SearchOptions options)
    {
        var result = new char[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (!options.MatchAccents)
            {
                string d = c.ToString().Normalize(NormalizationForm.FormD);
                if (d.Length > 0 && CharUnicodeInfo.GetUnicodeCategory(d[0]) != UnicodeCategory.NonSpacingMark) c = d[0];
            }
            if (!options.MatchCase) c = char.ToLowerInvariant(c);
            result[i] = c;
        }
        return new string(result);
    }
}
