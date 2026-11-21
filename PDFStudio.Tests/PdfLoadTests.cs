using System.IO.Compression;
using System.Text;
using PDFStudio.Core;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Tests;

/// <summary>Abrir PDFs que o PDFsharp não lê sozinho (protegidos e com gravações no fim, ex.: assinados depois).</summary>
public class PdfLoadTests
{
    /// <summary>
    /// PDF protegido (só senha de permissões) que recebeu uma gravação no fim com tabela de objetos "em fluxo"
    /// que não lista o objeto da proteção — como documentos oficiais assinados depois de protegidos.
    /// </summary>
    private static string ProtectedWithAppendedRevision(string dir)
    {
        string input = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 3, prefix: "P");
        string locked = Path.Combine(dir, "protegido.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("", "dono", AllowEdit: false), locked);

        byte[] bytes = File.ReadAllBytes(locked);
        string text = Encoding.Latin1.GetString(bytes);
        int t = text.LastIndexOf("trailer", StringComparison.Ordinal);
        int s = text.LastIndexOf("startxref", StringComparison.Ordinal);
        string trailer = text[t..s];
        string prev = System.Text.RegularExpressions.Regex.Match(text[s..], @"\d+").Value;
        int size = int.Parse(System.Text.RegularExpressions.Regex.Match(trailer, @"/Size\s+(\d+)").Groups[1].Value);
        string Keep(string key) => System.Text.RegularExpressions.Regex.Match(trailer,
            "/" + key + @"\s*(\d+\s+\d+\s+R|\[[^\]]*\])").Value;

        // Primeira tabela "em fluxo", completa (como no arquivo original do emissor)...
        var offsets = new Dictionary<int, long>();
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(text, @"(?<=\n)(\d+) 0 obj"))
            offsets[int.Parse(m.Groups[1].Value)] = m.Index;
        var rows = new MemoryStream();
        for (int i = 0; i < size; i++)
        {
            long off = offsets.GetValueOrDefault(i);
            rows.Write([(byte)(offsets.ContainsKey(i) ? 1 : 0), (byte)(off >> 16), (byte)(off >> 8), (byte)off]);
        }
        var all = new List<byte>(bytes);
        long first = all.Count;
        Append(all, size, $"/Size {size + 1} /Index [0 {size}]", rows.ToArray(), Keep);
        // ...e uma gravação depois, com tabela que só lista o objeto 0 e aponta para a primeira.
        long second = all.Count;
        Append(all, size + 1, $"/Size {size + 2} /Index [0 1] /Prev {first}", [0, 0, 0, 0], Keep);
        all.AddRange(Encoding.Latin1.GetBytes($"startxref\n{second}\n%%EOF\n"));
        _ = prev;
        string output = Path.Combine(dir, "protegido-assinado.pdf");
        File.WriteAllBytes(output, all.ToArray());
        return output;
    }

    private static void Append(List<byte> file, int number, string keys, byte[] rows, Func<string, string> keep)
    {
        using var packed = new MemoryStream();
        using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(rows);
        string head = $"{number} 0 obj\n<< /Type /XRef {keys} /W [1 3 0] /Filter /FlateDecode /Length {packed.Length} " +
                      $"{keep("Root")} {keep("Info")} {keep("Encrypt")} {keep("ID")} >>\nstream\n";
        file.AddRange(Encoding.Latin1.GetBytes(head));
        file.AddRange(packed.ToArray());
        file.AddRange(Encoding.Latin1.GetBytes("\nendstream\nendobj\n"));
    }

    [Fact]
    public void ProtectedPdfWithAppendedRevision_OpensAndPagesCanBeCopied()
    {
        string dir = TestPdfs.TempDir();
        string tricky = ProtectedWithAppendedRevision(dir);

        Assert.Equal(3, PdfOperations.GetPageCount(tricky));
        string output = Path.Combine(dir, "copia.pdf");
        Assert.Equal(4, PdfOperations.CopyPages(tricky, "2", 1, 3, output)); // página 2 → no fim
        Assert.Equal(["P 1", "P 2", "P 3", "P 2"], TestPdfs.Labels(output));
    }

    [Fact]
    public void SingleIndex_IsNotBuilt_ForSimpleFiles()
    {
        string dir = TestPdfs.TempDir();
        string simple = TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1);
        Assert.Null(PdfLoad.WithSingleIndex(File.ReadAllBytes(simple))); // uma gravação só: nada a montar
    }

    [Fact]
    public void WrongPassword_StillFailsAsBefore()
    {
        string dir = TestPdfs.TempDir();
        string locked = Path.Combine(dir, "fechado.pdf");
        PdfPasswords.Protect(TestPdfs.Create(Path.Combine(dir, "a.pdf"), 1), new PasswordOptions("abrir"), locked);
        Assert.ThrowsAny<Exception>(() => PdfLoad.Open(locked, "errada", PdfDocumentOpenMode.Import));
        using var ok = PdfLoad.Open(locked, "abrir", PdfDocumentOpenMode.Import);
        Assert.Equal(1, ok.PageCount);
    }
}
