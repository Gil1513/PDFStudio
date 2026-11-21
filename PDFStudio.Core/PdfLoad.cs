using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>
/// Abre PDFs com o PDFsharp e, se a leitura falhar num arquivo com várias gravações no fim (ex.: PDF protegido
/// que recebeu uma assinatura depois), tenta de novo com um índice de objetos único montado na memória.
/// O arquivo em disco nunca é alterado.
/// </summary>
public static class PdfLoad
{
    public static PdfDocument Open(string path, PdfDocumentOpenMode mode) => Open(File.ReadAllBytes(path), null, mode);

    public static PdfDocument Open(string path, string? password, PdfDocumentOpenMode mode) =>
        Open(File.ReadAllBytes(path), password, mode);

    public static PdfDocument Open(Stream stream, PdfDocumentOpenMode mode) => Open(ReadAll(stream), null, mode);

    public static PdfDocument Open(Stream stream, string? password, PdfDocumentOpenMode mode) => Open(ReadAll(stream), password, mode);

    public static PdfDocument Open(byte[] bytes, string? password, PdfDocumentOpenMode mode)
    {
        try
        {
            return Raw(bytes, password, mode);
        }
        catch (Exception ex) when (!IsPasswordProblem(ex) && WithSingleIndex(bytes) is { } repaired)
        {
            try { return Raw(repaired, password, mode); }
            catch (Exception) { ExceptionDispatchInfo.Capture(ex).Throw(); throw; } // o erro que vale é o primeiro
        }
    }

    private static PdfDocument Raw(byte[] bytes, string? password, PdfDocumentOpenMode mode) =>
        string.IsNullOrEmpty(password)
            ? PdfReader.Open(new MemoryStream(bytes), mode)
            : PdfReader.Open(new MemoryStream(bytes), password, mode);

    private static bool IsPasswordProblem(Exception ex) =>
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);

    private static byte[] ReadAll(Stream stream)
    {
        if (stream is MemoryStream ms && ms.Position == 0 && ms.TryGetBuffer(out var buffer) && buffer.Offset == 0 && buffer.Count == ms.Length)
            return buffer.Count == buffer.Array!.Length ? buffer.Array : buffer.ToArray();
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }

    private static readonly Regex ObjectStart = new(@"(?:^|[\r\n])(\d{1,10})[ \t\r\n]+(\d{1,5})[ \t\r\n]+obj\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// O mesmo arquivo com uma tabela de objetos nova no fim, completa e sem apontar para as antigas (cada objeto
    /// na última versão gravada). null se não der para montar com segurança (uma gravação só, objetos comprimidos
    /// em grupo, fim do arquivo que não reconhecemos).
    /// </summary>
    internal static byte[]? WithSingleIndex(byte[] bytes)
    {
        string text = Encoding.Latin1.GetString(bytes);
        if (CountOf(text, "startxref") < 2 || text.Contains("/ObjStm", StringComparison.Ordinal)) return null;

        // Onde cada objeto foi gravado por último (a gravação mais nova vale).
        var objects = new SortedDictionary<int, (long Offset, int Gen)>();
        foreach (Match m in ObjectStart.Matches(text))
        {
            var number = m.Groups[1];
            if (!int.TryParse(number.Value, out int n) || !int.TryParse(m.Groups[2].Value, out int gen) || n <= 0 || n > 8_000_000) continue;
            int end = Math.Min(text.Length, m.Index + m.Length + 300);
            int stream = text.IndexOf("stream", m.Index + m.Length, end - m.Index - m.Length, StringComparison.Ordinal);
            string head = text.Substring(m.Index + m.Length, (stream < 0 ? end : stream) - m.Index - m.Length);
            if (Regex.IsMatch(head, @"/Type\s*/XRef\b")) continue; // as tabelas antigas não entram
            objects[n] = (number.Index, gen);
        }
        if (objects.Count == 0) return null;

        string? trailer = NewestTrailer(text);
        if (trailer is null) return null;
        string? root = Entry(trailer, "Root");
        if (root is null) return null;

        var sb = new StringBuilder();
        int size = objects.Keys.Max() + 1;
        sb.Append('\n');
        long xrefAt = bytes.Length + 1;
        sb.Append("xref\n0 ").Append(size).Append('\n');
        for (int i = 0; i < size; i++)
        {
            if (i == 0) sb.Append("0000000000 65535 f\r\n");
            else if (objects.TryGetValue(i, out var o)) sb.Append(o.Offset.ToString("D10")).Append(' ').Append(o.Gen.ToString("D5")).Append(" n\r\n");
            else sb.Append("0000000000 00001 f\r\n");
        }
        sb.Append("trailer\n<< /Size ").Append(size).Append(' ').Append(root);
        foreach (var key in new[] { "Info", "Encrypt", "ID" })
            if (Entry(trailer, key) is { } value) sb.Append(' ').Append(value);
        sb.Append(" >>\nstartxref\n").Append(xrefAt).Append("\n%%EOF\n");

        var result = new byte[bytes.Length + Encoding.Latin1.GetByteCount(sb.ToString())];
        bytes.CopyTo(result, 0);
        Encoding.Latin1.GetBytes(sb.ToString(), 0, sb.Length, result, bytes.Length);
        return result;
    }

    private static int CountOf(string text, string word)
    {
        int count = 0;
        for (int i = text.IndexOf(word, StringComparison.Ordinal); i >= 0; i = text.IndexOf(word, i + word.Length, StringComparison.Ordinal)) count++;
        return count;
    }

    /// <summary>O dicionário do trailer mais novo (tabela normal ou tabela em fluxo).</summary>
    private static string? NewestTrailer(string text)
    {
        var m = Regex.Match(text, @"startxref\s+(\d+)", RegexOptions.RightToLeft);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out long at) || at < 0 || at >= text.Length) return null;
        int start;
        if (string.CompareOrdinal(text, (int)at, "xref", 0, 4) == 0)
        {
            int t = text.IndexOf("trailer", (int)at, StringComparison.Ordinal);
            if (t < 0) return null;
            start = text.IndexOf("<<", t, StringComparison.Ordinal);
        }
        else start = text.IndexOf("<<", (int)at, StringComparison.Ordinal);
        if (start < 0) return null;
        int end = DictionaryEnd(text, start);
        return end < 0 ? null : text[start..end];
    }

    /// <summary>Fim (exclusivo) do dicionário que começa em <paramref name="start"/> ("&lt;&lt;").</summary>
    private static int DictionaryEnd(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length - 1 && i < start + 100_000; i++)
        {
            char c = text[i];
            if (c == '(') { i = StringEnd(text, i); if (i < 0) return -1; continue; }
            if (c == '<' && text[i + 1] == '<') { depth++; i++; continue; }
            if (c == '>' && text[i + 1] == '>') { depth--; i++; if (depth == 0) return i + 1; continue; }
            if (c == '<') { int close = text.IndexOf('>', i); if (close < 0) return -1; i = close; }
        }
        return -1;
    }

    /// <summary>Posição do ")" que fecha a string literal que começa em <paramref name="start"/>.</summary>
    private static int StringEnd(string text, int start)
    {
        int depth = 0;
        for (int i = start; i < text.Length; i++)
        {
            char c = text[i];
            if (c == '\\') { i++; continue; }
            if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>"/Chave valor" copiado como está no trailer (referência, dicionário ou array).</summary>
    private static string? Entry(string trailer, string key)
    {
        var m = Regex.Match(trailer, @"/" + key + @"(?![A-Za-z0-9])\s*");
        if (!m.Success) return null;
        int i = m.Index + m.Length;
        var reference = Regex.Match(trailer[i..], @"^\d+\s+\d+\s+R");
        if (reference.Success) return "/" + key + " " + reference.Value;
        if (trailer.AsSpan(i).StartsWith("<<"))
        {
            int end = DictionaryEnd(trailer, i);
            return end < 0 ? null : "/" + key + " " + trailer[i..end];
        }
        if (i < trailer.Length && trailer[i] == '[')
        {
            for (int j = i + 1; j < trailer.Length; j++)
            {
                if (trailer[j] == '(') { j = StringEnd(trailer, j); if (j < 0) return null; continue; }
                if (trailer[j] == '<') { int close = trailer.IndexOf('>', j); if (close < 0) return null; j = close; continue; }
                if (trailer[j] == ']') return "/" + key + " " + trailer[i..(j + 1)];
            }
        }
        return null;
    }
}
