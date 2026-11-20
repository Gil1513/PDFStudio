namespace PDFStudio.Core;

/// <summary>
/// Um trecho para ler em voz alta (uma frase, um título): o texto e onde ele está na página
/// (um retângulo por linha, em pontos, para destacar o que está sendo lido).
/// </summary>
public sealed record ReadingChunk(int Page, string Text, IReadOnlyList<(double X, double Y, double Width, double Height)> Boxes);

/// <summary>
/// Etapa 7.9: ler em voz alta. A voz é do Windows (no app); aqui fica o que dá para testar: o texto das páginas
/// dividido em frases, na ordem de leitura, cada uma com a posição para o destaque.
/// </summary>
public static class ReadAloud
{
    /// <summary>Frase muito longa (sem ponto) é cortada nesse número de palavras, para o destaque acompanhar a leitura.</summary>
    public const int MaxWords = 45;

    /// <summary>Os trechos de todas as páginas a partir de <paramref name="firstPage"/>.</summary>
    public static List<ReadingChunk> Chunks(IReadOnlyList<IReadOnlyList<TextWord>> pages, int firstPage = 0)
    {
        var result = new List<ReadingChunk>();
        for (int p = Math.Max(0, firstPage); p < pages.Count; p++) result.AddRange(Chunks(pages[p], p));
        return result;
    }

    /// <summary>
    /// As frases de uma página. Uma frase termina em ponto final, interrogação, exclamação ou reticências; também
    /// termina quando há um espaço maior entre as linhas (título, novo parágrafo) ou quando fica longa demais.
    /// </summary>
    public static List<ReadingChunk> Chunks(IReadOnlyList<TextWord> words, int page)
    {
        var result = new List<ReadingChunk>();
        var current = new List<TextWord>();

        void Close()
        {
            if (current.Count == 0) return;
            string text = Join(current);
            if (text.Any(char.IsLetterOrDigit))
                result.Add(new ReadingChunk(page, text, PdfTextLayout.LineRects(current)));
            current = [];
        }

        for (int i = 0; i < words.Count; i++)
        {
            var w = words[i];
            if (current.Count > 0 && w.Line != current[^1].Line && BigGap(current[^1], w)) Close();
            current.Add(w);
            if (EndsSentence(w.Text) || current.Count >= MaxWords) Close();
        }
        Close();
        return result;
    }

    /// <summary>A linha nova começa bem mais abaixo do que a altura da letra (linha em branco, título, outro bloco).</summary>
    private static bool BigGap(TextWord previous, TextWord next)
    {
        double gap = next.Y - previous.Bottom;
        double size = Math.Max(previous.Height, next.Height);
        return gap > size * 0.6 || Math.Abs(previous.Height - next.Height) > size * 0.3 || next.Y < previous.Y - size;
    }

    /// <summary>"fim." / "certo?" / "(nota.)" / "dito…" encerram a frase; "Sr." e "1.5" não (abreviação comum e número).</summary>
    public static bool EndsSentence(string word)
    {
        string w = word.TrimEnd('"', '\'', ')', ']', '»', '”', '’');
        if (w.Length == 0) return false;
        char last = w[^1];
        if (last is '!' or '?' or '…') return true;
        if (last != '.') return false;
        string body = w.TrimEnd('.');
        if (body.Length == 0) return w.Length >= 3; // "..."
        string lower = body.ToLowerInvariant();
        return lower is not ("sr" or "sra" or "dr" or "dra" or "prof" or "profa" or "art" or "etc" or "ex" or "pág" or "n" or "nº" or "av" or "fig");
    }

    /// <summary>Junta as palavras com espaço; palavra partida com hífen no fim da linha volta a ficar inteira.</summary>
    private static string Join(List<TextWord> words)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < words.Count; i++)
        {
            string t = words[i].Text;
            if (sb.Length > 0)
            {
                bool split = i > 0 && words[i].Line != words[i - 1].Line && sb[^1] == '-' && sb.Length > 1
                             && char.IsLetter(sb[^2]) && t.Length > 0 && char.IsLower(t[0]);
                if (split) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(t);
        }
        return sb.ToString();
    }
}
