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

    /// <summary>
    /// Tipos que o app abre direto no programa padrão: documentos sem macro, imagens, texto, áudio e vídeo.
    /// Todo o resto (programas, scripts, páginas da web, atalhos, compactados, tipos desconhecidos…) só pode
    /// ser salvo. Uma lista do que é permitido não esquece nenhum tipo perigoso novo.
    /// </summary>
    public static readonly string[] SafeToOpenExtensions =
    [
        ".pdf", ".txt", ".csv", ".md",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".tif", ".tiff", ".heic",
        ".docx", ".xlsx", ".pptx", ".doc", ".xls", ".ppt", ".rtf", ".odt", ".ods", ".odp",
        ".mp3", ".wav", ".m4a", ".ogg", ".flac", ".mp4", ".mov", ".avi", ".mkv", ".wmv",
    ];

    /// <summary>true = o app não abre este anexo direto (só deixa salvar).</summary>
    public static bool IsRisky(string name) => !SafeToOpenExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    /// <summary>
    /// Nome de arquivo seguro para gravar no Windows: só o nome (sem pasta), sem caracteres proibidos, sem
    /// caracteres invisíveis que invertem o texto (um "foto[inverte]gpj.exe" aparece como "fotoexe.jpg") e
    /// sem nomes reservados (CON, NUL…).
    /// </summary>
    public static string SafeFileName(string? name)
    {
        name = (name ?? "").Replace('\\', '/').Split('/').Last();
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name
            .Where(c => System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c) is not
                (System.Globalization.UnicodeCategory.Format or System.Globalization.UnicodeCategory.Control))
            .Select(c => invalid.Contains(c) ? '_' : c);
        name = new string(chars.ToArray()).Trim(' ', '.');
        if (name.Length > 150) name = name[..(150 - Path.GetExtension(name).Length)] + Path.GetExtension(name);
        if (name.Length == 0) return "anexo";
        if (ReservedNames.Contains(Path.GetFileNameWithoutExtension(name))) name = "_" + name;
        return name;
    }

    /// <summary>
    /// Marca o arquivo como "veio de outro lugar" (como o navegador faz com downloads). Assim o Windows e o
    /// Office avisam antes de rodar algo dele (Modo de Exibição Protegido, SmartScreen).
    /// </summary>
    public static void MarkAsFromElsewhere(string file)
    {
        try
        {
            File.WriteAllText(file + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Pen drive em FAT32 e afins não guardam essa marca: o arquivo é gravado mesmo assim.
        }
    }

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

    /// <summary>Bytes do anexo (já descompactados), no máximo <paramref name="maxSize"/> bytes.</summary>
    public static byte[] Read(string path, AttachmentInfo attachment, long maxSize = MaxSize)
    {
        using var doc = Open(path, PdfDocumentOpenMode.Import);
        var spec = attachment.PageIndex is null
            ? DocumentEntries(doc).ElementAtOrDefault(attachment.Number).Spec
            : PageEntries(doc).ElementAtOrDefault(attachment.Number).Spec;
        if (spec is null || EmbeddedStream(spec) is not { } stream)
            throw new InvalidOperationException("O conteúdo do anexo não foi encontrado no PDF.");
        return Unpack(stream, maxSize);
    }

    /// <summary>
    /// Descompacta com limite: um anexo pequeno no PDF pode virar gigabytes ao descompactar ("bomba") e
    /// travar o computador. Passou do limite, para e avisa.
    /// </summary>
    private static byte[] Unpack(PdfDictionary stream, long maxSize)
    {
        string tooBig = $"O anexo é grande demais para abrir (máximo {Math.Max(1, maxSize / 1024 / 1024)} MB).";
        var filter = stream.Elements["/Filter"];
        if (filter is PdfArray { Elements.Count: 1 } single) filter = single.Elements[0];
        byte[] raw = stream.Stream.Value;
        if (filter is null)
            return raw.LongLength <= maxSize ? raw : throw new InvalidOperationException(tooBig);
        if (filter is PdfName { Value: "/FlateDecode" } && stream.Elements["/DecodeParms"] is null)
        {
            try
            {
                using var z = new ZLibStream(new MemoryStream(raw), CompressionMode.Decompress);
                using var output = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = z.Read(buffer)) > 0)
                {
                    if (output.Length + read > maxSize) throw new InvalidOperationException(tooBig);
                    output.Write(buffer, 0, read);
                }
                return output.ToArray();
            }
            catch (InvalidDataException)
            {
                // Compactação fora do padrão: tenta pelo leitor da biblioteca, mais tolerante (abaixo).
            }
        }
        // Outros filtros (raros em anexos): só confia se o arquivo compactado já for pequeno.
        if (raw.LongLength > maxSize / 4) throw new InvalidOperationException(tooBig);
        var value = stream.Stream.UnfilteredValue;
        return value.LongLength <= maxSize ? value : throw new InvalidOperationException(tooBig);
    }

    /// <summary>Grava o anexo em <paramref name="destination"/>, marcado como vindo de outro lugar.</summary>
    public static void SaveTo(string path, AttachmentInfo attachment, string destination)
    {
        File.WriteAllBytes(destination, Read(path, attachment));
        MarkAsFromElsewhere(destination);
    }

    /// <summary>Pasta das cópias de anexos abertos (cada abertura numa subpasta).</summary>
    public static string OpenedCopiesRoot => Path.Combine(Path.GetTempPath(), "PDFStudio", "anexos");

    /// <summary>Apaga cópias de anexos abertos há mais de um dia (as que ainda estão abertas ficam).</summary>
    public static void CleanupOpenedCopies(string? root = null)
    {
        root ??= OpenedCopiesRoot;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.EnumerateDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Ainda aberto em outro programa: fica para a próxima.
            }
        }
    }

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
        string name = SafeFileName(FirstNonEmpty(spec.Elements.GetString("/UF"), spec.Elements.GetString("/F"), key, "anexo"));
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
