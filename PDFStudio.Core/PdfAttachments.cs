using System.IO.Compression;
using System.Security.Cryptography;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>
/// Um arquivo anexado ao PDF. <see cref="PageIndex"/> é null para anexos do documento (painel "Anexos")
/// e o número da página para anexos presos à página (ícone de clipe, feitos por outros programas).
/// </summary>
public sealed record AttachmentInfo(string Name, string Description, long Size, DateTime? Modified, int? PageIndex, int Number);

/// <summary>Etapa 5.4: anexos (arquivos guardados dentro do PDF).</summary>
public static class PdfAttachments
{
    /// <summary>Tamanho máximo de um anexo (o PDF inteiro fica na memória ao editar).</summary>
    public const long MaxSize = 200L * 1024 * 1024;

    /// <summary>Extensões de programas e scripts: o app só deixa salvar, nunca abrir direto.</summary>
    public static readonly string[] RiskyExtensions =
    [
        ".exe", ".com", ".bat", ".cmd", ".msi", ".msix", ".appx", ".scr", ".pif", ".cpl", ".dll", ".sys",
        ".js", ".jse", ".vbs", ".vbe", ".wsf", ".wsh", ".ps1", ".psm1", ".hta", ".lnk", ".url", ".reg",
        ".jar", ".application", ".gadget", ".msc", ".inf", ".iso", ".img", ".vhd", ".vhdx", ".chm", ".docm", ".xlsm", ".pptm",
    ];

    public static bool IsRisky(string name) => RiskyExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>Anexos do documento (em ordem de nome) e depois os presos às páginas.</summary>
    public static List<AttachmentInfo> List(string path)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        var result = new List<AttachmentInfo>();
        int n = 0;
        foreach (var (key, spec) in DocumentEntries(doc))
            result.Add(Describe(spec, key, null, n++));
        n = 0;
        foreach (var (page, spec) in PageEntries(doc))
            result.Add(Describe(spec, null, page, n++));
        return result;
    }

    /// <summary>Anexa o arquivo ao documento. Se já existir um anexo com o mesmo nome, ganha " (2)", " (3)"…</summary>
    public static string Add(string input, string file, string description, string output)
    {
        var info = new FileInfo(file);
        if (!info.Exists) throw new FileNotFoundException("O arquivo não foi encontrado.", file);
        if (info.Length > MaxSize) throw new InvalidOperationException($"O arquivo é grande demais para anexar (máximo {MaxSize / 1024 / 1024} MB).");
        byte[] data = File.ReadAllBytes(file);
        string name = info.Name;

        PdfAnnotations.Edit(input, output, doc =>
        {
            var entries = DocumentEntries(doc);
            name = UniqueName(name, entries.Select(e => e.Key).ToHashSet(StringComparer.OrdinalIgnoreCase));
            entries.Add((name, CreateFileSpec(doc, name, description, data, info.LastWriteTime)));
            WriteTree(doc, entries);
        });
        return name;
    }

    /// <summary>Remove o anexo do documento número <paramref name="number"/> (ordem de <see cref="List"/>).</summary>
    public static void Remove(string input, int number, string output) =>
        PdfAnnotations.Edit(input, output, doc =>
        {
            var entries = DocumentEntries(doc);
            if (number < 0 || number >= entries.Count) throw new ArgumentOutOfRangeException(nameof(number), "O anexo não foi encontrado.");
            entries.RemoveAt(number);
            WriteTree(doc, entries);
        });

    /// <summary>Bytes do anexo (já descompactados).</summary>
    public static byte[] Read(string path, AttachmentInfo attachment)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        var spec = attachment.PageIndex is null
            ? DocumentEntries(doc).ElementAtOrDefault(attachment.Number).Spec
            : PageEntries(doc).ElementAtOrDefault(attachment.Number).Spec;
        if (spec is null || EmbeddedStream(spec) is not { } stream)
            throw new InvalidOperationException("O conteúdo do anexo não foi encontrado no PDF.");
        return stream.Stream.UnfilteredValue;
    }

    /// <summary>Grava o anexo em <paramref name="destination"/>.</summary>
    public static void SaveTo(string path, AttachmentInfo attachment, string destination) =>
        File.WriteAllBytes(destination, Read(path, attachment));

    // ---------------- Leitura ----------------

    private static PdfDocument Open(string path, PdfDocumentOpenMode mode) =>
        PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), mode);

    /// <summary>Entradas da árvore /Names /EmbeddedFiles (com /Kids, se houver), em ordem.</summary>
    private static List<(string Key, PdfDictionary Spec)> DocumentEntries(PdfDocument doc)
    {
        var result = new List<(string, PdfDictionary)>();
        var tree = doc.Internals.Catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/EmbeddedFiles");
        if (tree is not null) Collect(tree, result, 0);
        return result;
    }

    private static void Collect(PdfDictionary node, List<(string, PdfDictionary)> result, int depth)
    {
        if (depth > 20) return;
        if (node.Elements.GetArray("/Names") is { } names)
            for (int i = 0; i + 1 < names.Elements.Count; i += 2)
                if (names.Elements[i] is PdfString key && names.Elements.GetDictionary(i + 1) is { } spec)
                    result.Add((key.Value, spec));
        if (node.Elements.GetArray("/Kids") is { } kids)
            for (int i = 0; i < kids.Elements.Count; i++)
                if (kids.Elements.GetDictionary(i) is { } kid) Collect(kid, result, depth + 1);
    }

    private static List<(int Page, PdfDictionary Spec)> PageEntries(PdfDocument doc)
    {
        var result = new List<(int, PdfDictionary)>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            if (doc.Pages[p].Elements.GetArray("/Annots") is not { } annots) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
                if (annots.Elements.GetDictionary(i) is { } a && a.Elements.GetName("/Subtype") == "/FileAttachment"
                    && a.Elements.GetDictionary("/FS") is { } spec)
                    result.Add((p, spec));
        }
        return result;
    }

    private static PdfDictionary? EmbeddedStream(PdfDictionary spec)
    {
        var ef = spec.Elements.GetDictionary("/EF");
        var stream = ef?.Elements.GetDictionary("/UF") ?? ef?.Elements.GetDictionary("/F");
        return stream?.Stream is null ? null : stream;
    }

    private static AttachmentInfo Describe(PdfDictionary spec, string? key, int? page, int number)
    {
        string name = FirstNonEmpty(spec.Elements.GetString("/UF"), spec.Elements.GetString("/F"), key, "anexo");
        // Só o nome do arquivo (nunca uma pasta) e sem caracteres que o Windows não aceita.
        name = name.Replace('\\', '/').Split('/').Last();
        name = new string(name.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray()).Trim(' ', '.');
        if (name.Length == 0) name = "anexo";
        var stream = EmbeddedStream(spec);
        var parameters = stream?.Elements.GetDictionary("/Params");
        long size = parameters?.Elements.GetInteger("/Size") ?? 0;
        if (size <= 0) size = stream?.Stream.Length ?? 0;
        var modified = DocumentPropertiesService.ParsePdfDate(parameters?.Elements.GetString("/ModDate"));
        return new AttachmentInfo(name, spec.Elements.GetString("/Desc"), size, modified, page, number);
    }

    private static string FirstNonEmpty(params string?[] values) => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v)) ?? "";

    // ---------------- Escrita ----------------

    private static PdfDictionary CreateFileSpec(PdfDocument doc, string name, string description, byte[] data, DateTime modified)
    {
        var file = new PdfDictionary(doc);
        file.Elements["/Type"] = new PdfName("/EmbeddedFile");
        var parameters = new PdfDictionary(doc);
        parameters.Elements["/Size"] = new PdfInteger(data.Length);
        parameters.Elements["/ModDate"] = new PdfString(PdfDate(modified));
        parameters.Elements["/CheckSum"] = new PdfString(System.Text.Encoding.Latin1.GetString(MD5.HashData(data)), PdfStringEncoding.RawEncoding);
        file.Elements["/Params"] = parameters;
        using (var compressed = new MemoryStream())
        {
            using (var z = new ZLibStream(compressed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(data);
            file.CreateStream(compressed.ToArray());
        }
        file.Elements["/Filter"] = new PdfName("/FlateDecode");
        doc.Internals.AddObject(file);

        var spec = new PdfDictionary(doc);
        spec.Elements["/Type"] = new PdfName("/Filespec");
        spec.Elements["/F"] = new PdfString(AsciiName(name));
        spec.Elements["/UF"] = new PdfString(name, PdfStringEncoding.Unicode);
        if (!string.IsNullOrWhiteSpace(description)) spec.Elements["/Desc"] = new PdfString(description.Trim(), PdfStringEncoding.Unicode);
        var ef = new PdfDictionary(doc);
        ef.Elements["/F"] = file.Reference!;
        ef.Elements["/UF"] = file.Reference!;
        spec.Elements["/EF"] = ef;
        doc.Internals.AddObject(spec);
        return spec;
    }

    /// <summary>Regrava a árvore de anexos como uma lista única, em ordem de nome (como a especificação pede).</summary>
    private static void WriteTree(PdfDocument doc, List<(string Key, PdfDictionary Spec)> entries)
    {
        var catalog = doc.Internals.Catalog;
        var names = catalog.Elements.GetDictionary("/Names");
        if (entries.Count == 0)
        {
            names?.Elements.Remove("/EmbeddedFiles");
            if (names is not null && names.Elements.Count == 0) catalog.Elements.Remove("/Names");
            return;
        }
        if (names is null)
        {
            names = new PdfDictionary(doc);
            catalog.Elements["/Names"] = names;
        }
        var array = new PdfArray(doc);
        foreach (var (key, spec) in entries.OrderBy(e => e.Key, StringComparer.Ordinal))
        {
            array.Elements.Add(new PdfString(key, PdfStringEncoding.Unicode));
            array.Elements.Add(spec.Reference is { } r ? r : spec);
        }
        var tree = new PdfDictionary(doc);
        tree.Elements["/Names"] = array;
        names.Elements["/EmbeddedFiles"] = tree;
    }

    private static string UniqueName(string name, HashSet<string> taken)
    {
        if (!taken.Contains(name)) return name;
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int i = 2; ; i++)
            if (!taken.Contains($"{stem} ({i}){ext}")) return $"{stem} ({i}){ext}";
    }

    private static string AsciiName(string name) =>
        new(name.Normalize(System.Text.NormalizationForm.FormD).Where(c => c < 128 && c >= 32).ToArray());

    private static string PdfDate(DateTime d)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(d);
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        return $"D:{d:yyyyMMddHHmmss}{sign}{Math.Abs(offset.Hours):00}'{Math.Abs(offset.Minutes):00}'";
    }
}
