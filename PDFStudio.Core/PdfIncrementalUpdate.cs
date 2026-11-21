using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PDFStudio.Core;

/// <summary>
/// Gravação "no fim do arquivo" (atualização incremental): os bytes originais ficam exatamente como estavam e
/// só os objetos novos ou alterados são acrescentados depois deles, com uma tabela nova que aponta para a antiga.
/// É assim que se comenta um PDF assinado sem invalidar a assinatura (ela cobre só os bytes originais).
/// O PDFsharp sempre regrava o arquivo inteiro, então esta parte é feita aqui.
/// </summary>
internal static class PdfIncrementalUpdate
{
    /// <summary>"Retrato" de cada objeto do documento (número → resumo do conteúdo), tirado antes de editar.</summary>
    public static Dictionary<int, byte[]> Snapshot(PdfDocument doc)
    {
        var result = new Dictionary<int, byte[]>();
        foreach (var obj in doc.Internals.GetAllObjects())
            if (!IsXRef(obj)) result[PdfSyntax.Num(obj)] = SHA256.HashData(PdfSyntax.Object(obj));
        return result;
    }

    /// <summary>Tabelas em formato de fluxo são estrutura do arquivo, não conteúdo: não entram na comparação.</summary>
    private static bool IsXRef(PdfObject obj) => obj is PdfDictionary d && d.Elements.GetName("/Type") == "/XRef";

    /// <summary>
    /// Grava em <paramref name="output"/> os bytes <paramref name="original"/> seguidos só do que mudou no documento
    /// desde o <paramref name="before"/>. Lança <see cref="NotSupportedException"/> se este arquivo não permitir.
    /// </summary>
    /// <param name="xrefStream">Só para testes: força o formato da tabela nova (null = o mesmo do arquivo).</param>
    public static void Write(byte[] original, PdfDocument doc, Dictionary<int, byte[]> before, string output, bool? xrefStream = null)
    {
        var changed = new List<PdfObject>();
        var expected = new Dictionary<int, byte[]>(before);
        int maxNumber = 0;
        foreach (var obj in doc.Internals.GetAllObjects())
        {
            int n = PdfSyntax.Num(obj);
            maxNumber = Math.Max(maxNumber, n);
            if (IsXRef(obj)) continue;
            byte[] now = SHA256.HashData(PdfSyntax.Object(obj));
            if (!before.TryGetValue(n, out var hash) || !hash.AsSpan().SequenceEqual(now))
            {
                changed.Add(obj);
                expected[n] = now;
            }
        }

        var (prevXref, isStream, trailer) = LastTrailer(original);
        isStream = xrefStream ?? isStream;
        int ownXref = -1;
        if (Regex.IsMatch(trailer, @"/Encrypt\b")) throw new NotSupportedException("PDF com senha.");
        int prevSize = Regex.Match(trailer, @"/Size\s+(\d+)") is { Success: true } m ? int.Parse(m.Groups[1].Value) : 0;
        // Um objeto novo não pode reaproveitar um número que já existe no arquivo.
        if (changed.Any(o => !before.ContainsKey(PdfSyntax.Num(o)) && PdfSyntax.Num(o) < prevSize))
            throw new NotSupportedException("Número de objeto já usado.");
        int size = Math.Max(maxNumber + 1, prevSize);
        string info = Regex.Match(trailer, @"/Info\s+\d+\s+\d+\s+R").Value;
        string id = Regex.Match(trailer, @"/ID\s*\[[^\]]*\]").Value;
        var root = doc.Internals.Catalog;

        using var file = new MemoryStream(original.Length + 4096);
        file.Write(original);
        if (changed.Count > 0)
        {
            if (original.Length > 0 && original[^1] is not ((byte)'\n' or (byte)'\r')) file.WriteByte((byte)'\n');
            var offsets = new SortedDictionary<int, (long Offset, int Generation)>();
            foreach (var obj in changed.OrderBy(o => PdfSyntax.Num(o)))
            {
                offsets[PdfSyntax.Num(obj)] = (file.Position, PdfSyntax.Gen(obj));
                file.Write(PdfSyntax.Object(obj));
            }

            string common = $"/Root {PdfSyntax.Num(root)} {PdfSyntax.Gen(root)} R" + (info.Length > 0 ? " " + info : "") +
                            (id.Length > 0 ? " " + id : "") + $" /Prev {prevXref}";
            long xrefAt = file.Position;
            if (isStream)
            {
                // Tabela em formato de "fluxo" (PDF 1.5+), igual à do arquivo original.
                int own = size;
                ownXref = own;
                offsets[own] = (xrefAt, 0);
                var data = new MemoryStream();
                foreach (var (_, (offset, generation)) in offsets)
                {
                    data.WriteByte(1);
                    for (int s = 24; s >= 0; s -= 8) data.WriteByte((byte)(offset >> s));
                    data.WriteByte((byte)(generation >> 8));
                    data.WriteByte((byte)generation);
                }
                byte[] entries = data.ToArray();
                Ascii(file, $"{own} 0 obj\n<< /Type /XRef /Size {own + 1} /W [1 4 2] /Index [{Index(offsets.Keys)}] {common} /Length {entries.Length} >>\nstream\n");
                file.Write(entries);
                Ascii(file, "\nendstream\nendobj\n");
            }
            else
            {
                var text = new StringBuilder("xref\n");
                foreach (var (start, count) in Runs(offsets.Keys))
                {
                    text.Append(CultureInfo.InvariantCulture, $"{start} {count}\n");
                    for (int i = start; i < start + count; i++)
                        text.Append(CultureInfo.InvariantCulture, $"{offsets[i].Offset:D10} {offsets[i].Generation:D5} n\r\n");
                }
                text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {size} {common} >>\n");
                Ascii(file, text.ToString());
            }
            Ascii(file, $"startxref\n{xrefAt}\n%%EOF\n");
        }

        byte[] result = file.ToArray();
        // Confere: lido de novo, o arquivo tem de ter exatamente os objetos esperados (os antigos intactos e os novos).
        try
        {
            using var check = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(result), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            var got = Snapshot(check);
            got.Remove(ownXref); // a própria tabela nova, em formato de fluxo, também é um objeto
            if (got.Count != expected.Count || expected.Any(kv => !got.TryGetValue(kv.Key, out var h) || !h.AsSpan().SequenceEqual(kv.Value)))
                throw new NotSupportedException("A gravação no fim do arquivo não conferiu.");
        }
        catch (PdfSharp.Pdf.IO.PdfReaderException ex)
        {
            throw new NotSupportedException("A gravação no fim do arquivo não pôde ser lida de volta.", ex);
        }

        string full = Path.GetFullPath(output), temp = full + ".tmp";
        try
        {
            File.WriteAllBytes(temp, result);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }

    /// <summary>A tabela mais recente do arquivo: onde ela fica, se é "fluxo" e o texto do trailer (ou do dicionário dela).</summary>
    private static (long Offset, bool IsStream, string Trailer) LastTrailer(byte[] bytes)
    {
        int tail = Math.Max(0, bytes.Length - 2048);
        string end = Encoding.Latin1.GetString(bytes, tail, bytes.Length - tail);
        var m = Regex.Match(end, @"startxref\s+(\d+)\s*%%EOF\s*$", RegexOptions.RightToLeft);
        if (!m.Success) m = Regex.Match(end, @"startxref\s+(\d+)", RegexOptions.RightToLeft);
        if (!m.Success || !long.TryParse(m.Groups[1].Value, out long at) || at < 0 || at >= bytes.Length)
            throw new NotSupportedException("Não achei a tabela do arquivo.");
        int length = (int)Math.Min(bytes.Length - at, 64 * 1024);
        string text = Encoding.Latin1.GetString(bytes, (int)at, length);
        if (text.StartsWith("xref", StringComparison.Ordinal))
        {
            int t = text.IndexOf("trailer", StringComparison.Ordinal);
            if (t < 0) throw new NotSupportedException("Tabela sem trailer.");
            return (at, false, text[t..]);
        }
        if (!Regex.IsMatch(text, @"^\d+\s+\d+\s+obj") || !text.Contains("/XRef", StringComparison.Ordinal))
            throw new NotSupportedException("Tabela em formato desconhecido.");
        int s = text.IndexOf("stream", StringComparison.Ordinal);
        return (at, true, s > 0 ? text[..s] : text);
    }

    private static IEnumerable<(int Start, int Count)> Runs(IEnumerable<int> sorted)
    {
        int start = -1, count = 0;
        foreach (int n in sorted)
        {
            if (count > 0 && n == start + count) { count++; continue; }
            if (count > 0) yield return (start, count);
            start = n;
            count = 1;
        }
        if (count > 0) yield return (start, count);
    }

    private static string Index(IEnumerable<int> sorted) => string.Join(" ", Runs(sorted).Select(r => $"{r.Start} {r.Count}"));

    private static void Ascii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
}

/// <summary>Escreve objetos do PDFsharp na sintaxe do PDF (usado pela gravação no fim do arquivo e para comparar versões).</summary>
internal static class PdfSyntax
{
    private const int MaxDepth = 100;

    public static int Num(PdfObject o) => o.Reference?.ObjectNumber ?? throw new NotSupportedException("Objeto sem número.");
    public static int Gen(PdfObject o) => o.Reference?.GenerationNumber ?? 0;

    /// <summary>"N G obj ... endobj" de um objeto indireto, com o fluxo (stream) se houver.</summary>
    public static byte[] Object(PdfObject obj)
    {
        var s = new MemoryStream();
        Ascii(s, $"{PdfSyntax.Num(obj)} {PdfSyntax.Gen(obj)} obj\n");
        if (obj is PdfDictionary { Stream: { } stream } dict)
        {
            byte[] data = stream.Value ?? [];
            Dictionary(s, dict, 0, overrideLength: data.Length);
            Ascii(s, "\nstream\n");
            s.Write(data);
            Ascii(s, "\nendstream");
        }
        else Item(s, obj, 0);
        Ascii(s, "\nendobj\n");
        return s.ToArray();
    }

    /// <summary>Um valor solto (direto) na sintaxe do PDF; usado para comparar duas versões de uma chave.</summary>
    public static byte[] Value(PdfItem? item)
    {
        var s = new MemoryStream();
        if (item is PdfDictionary { Stream: { } stream } d)
        {
            Dictionary(s, d, 0, overrideLength: stream.Value?.Length ?? 0);
            s.Write(SHA256.HashData(stream.Value ?? []));
        }
        else Item(s, item, 0);
        return s.ToArray();
    }

    private static void Item(Stream s, PdfItem? item, int depth)
    {
        if (depth > MaxDepth) throw new NotSupportedException("Objeto aninhado demais.");
        switch (item)
        {
            case null or PdfNull or PdfNullObject: Ascii(s, "null"); break;
            case PdfReference r: Ascii(s, $"{r.ObjectNumber} {r.GenerationNumber} R"); break;
            case PdfName n: Name(s, n.Value); break;
            case PdfNameObject n: Name(s, n.Value); break;
            case PdfString str: String(s, str.Value, str.Encoding == PdfStringEncoding.Unicode); break;
            case PdfStringObject str: String(s, str.Value, str.Encoding == PdfStringEncoding.Unicode); break;
            case PdfBoolean b: Ascii(s, b.Value ? "true" : "false"); break;
            case PdfBooleanObject b: Ascii(s, b.Value ? "true" : "false"); break;
            case PdfInteger i: Ascii(s, i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfIntegerObject i: Ascii(s, i.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfLongInteger l: Ascii(s, l.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfLongIntegerObject l: Ascii(s, l.Value.ToString(CultureInfo.InvariantCulture)); break;
            case PdfReal d: Ascii(s, Real(d.Value)); break;
            case PdfRealObject d: Ascii(s, Real(d.Value)); break;
            case PdfRectangle rc: Ascii(s, $"[{Real(rc.X1)} {Real(rc.Y1)} {Real(rc.X2)} {Real(rc.Y2)}]"); break;
            case PdfLiteral lit: Ascii(s, lit.Value); break;
            case PdfDate date: String(s, PdfDateText(date.Value), false); break;
            case PdfArray a:
                Ascii(s, "[");
                for (int i = 0; i < a.Elements.Count; i++)
                {
                    if (i > 0) Ascii(s, " ");
                    Item(s, a.Elements[i], depth + 1);
                }
                Ascii(s, "]");
                break;
            case PdfDictionary d:
                if (d.Stream is not null && d.Reference is null) throw new NotSupportedException("Fluxo direto.");
                Dictionary(s, d, depth, overrideLength: null);
                break;
            default: throw new NotSupportedException($"Tipo de objeto não suportado: {item.GetType().Name}.");
        }
    }

    private static void Dictionary(Stream s, PdfDictionary d, int depth, int? overrideLength)
    {
        Ascii(s, "<<");
        foreach (string key in d.Elements.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            if (overrideLength is not null && key == "/Length") continue;
            Ascii(s, " ");
            Name(s, key);
            Ascii(s, " ");
            Item(s, d.Elements[key], depth + 1);
        }
        if (overrideLength is { } length) Ascii(s, $" /Length {length}");
        Ascii(s, " >>");
    }

    /// <summary>Nome do PDF (/Abc), com #xx nos caracteres que não podem aparecer soltos.</summary>
    private static void Name(Stream s, string value)
    {
        s.WriteByte((byte)'/');
        foreach (byte b in Encoding.UTF8.GetBytes(value.StartsWith('/') ? value[1..] : value))
        {
            if (b is < 0x21 or > 0x7E or (byte)'#' or (byte)'/' or (byte)'(' or (byte)')' or (byte)'<' or (byte)'>'
                or (byte)'[' or (byte)']' or (byte)'{' or (byte)'}' or (byte)'%')
                Ascii(s, "#" + b.ToString("X2"));
            else s.WriteByte(b);
        }
    }

    /// <summary>Texto em hexadecimal: bytes 0–255 como estão; com acentos fora disso, UTF-16 com a marca FE FF.</summary>
    private static void String(Stream s, string value, bool unicode)
    {
        byte[] bytes = unicode || value.Any(c => c > 255)
            ? [0xFE, 0xFF, .. Encoding.BigEndianUnicode.GetBytes(value)]
            : Encoding.Latin1.GetBytes(value);
        Ascii(s, "<" + Convert.ToHexString(bytes) + ">");
    }

    /// <summary>Data no formato do PDF: D:AAAAMMDDHHmmSS-03'00'.</summary>
    private static string PdfDateText(DateTime value)
    {
        var offset = value.Kind == DateTimeKind.Utc ? TimeSpan.Zero : TimeZoneInfo.Local.GetUtcOffset(value);
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return "D:" + value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + $"{sign}{offset.Hours:D2}'{offset.Minutes:D2}'";
    }

    private static string Real(double v) =>
        double.IsFinite(v) ? v.ToString("0.##########", CultureInfo.InvariantCulture) : "0";

    private static void Ascii(Stream s, string text) => s.Write(Encoding.ASCII.GetBytes(text));
}
