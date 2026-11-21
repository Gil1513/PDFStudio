using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Campos editáveis das propriedades do documento.</summary>
public sealed record DocumentMetadata(string Title, string Author, string Subject, string Keywords);

/// <summary>Tudo o que a janela "Propriedades do documento" mostra.</summary>
public sealed record DocumentProperties(
    string FilePath,
    long FileSize,
    int PageCount,
    double PageWidthPt,
    double PageHeightPt,
    string PdfVersion,
    DocumentMetadata Metadata,
    string Creator,
    string Producer,
    DateTime? Created,
    DateTime? Modified,
    bool IsEncrypted,
    IReadOnlyList<SignatureDetails> Signatures);

/// <summary>Dados de uma assinatura que já existe no arquivo (somente leitura).</summary>
public sealed record SignatureDetails(
    string FieldName,
    int? PageNumber,
    string? Signer,
    string? Issuer,
    string? Reason,
    string? Location,
    string? ContactInfo,
    DateTime? SignedAt,
    /// <summary>true = o documento não mudou desde a assinatura; false = foi alterado; null = não foi possível verificar.</summary>
    bool? IsIntact,
    /// <summary>A assinatura cobre o arquivo inteiro (não houve gravações depois dela).</summary>
    bool CoversWholeFile,
    /// <summary>Certificado de quem assinou (null se não veio na assinatura).</summary>
    SignerCertificate? Certificate = null,
    /// <summary>Hora do carimbo de tempo de um servidor (null = só o relógio de quem assinou).</summary>
    DateTime? TimestampAt = null,
    /// <summary>Assinatura de certificação (DocMDP): 1 = nada pode mudar, 2 = formulários e assinar, 3 = também comentar.</summary>
    int? CertificationLevel = null,
    /// <summary>O que o documento recebeu depois desta assinatura (nada, comentários, outra assinatura, algo proibido…).</summary>
    SignatureChanges ChangesAfter = SignatureChanges.None,
    /// <summary>Se o carimbo de tempo (TimestampAt) foi conferido e de quem é.</summary>
    TimestampCheck TimestampState = TimestampCheck.None,
    /// <summary>A assinatura está montada de um jeito irregular (o trecho assinado não bate com o lugar dela no arquivo).</summary>
    bool BadLayout = false);

/// <summary>Situação do carimbo de tempo de uma assinatura.</summary>
public enum TimestampCheck
{
    /// <summary>Sem carimbo de tempo.</summary>
    None,
    /// <summary>Carimbo conferido, de um servidor que o Windows reconhece: o horário é confiável.</summary>
    Trusted,
    /// <summary>Carimbo íntegro, mas de um servidor que o Windows não reconhece.</summary>
    UnknownServer,
    /// <summary>O carimbo não confere com a assinatura.</summary>
    Broken,
}

public enum PageSizeUnit { Centimeters, Millimeters, Inches, Points }

/// <summary>Leitura e gravação das propriedades do documento (PDFsharp 6.2.4, MIT).</summary>
public static class DocumentPropertiesService
{
    /// <param name="checkRevocationOnline">Também pergunta pela internet se o certificado foi cancelado (revogado).
    /// Sem isso, só usa o que o Windows já tem guardado, e abrir o PDF não acessa a internet.</param>
    public static DocumentProperties Read(string path, bool checkRevocationOnline = false)
    {
        var file = new FileInfo(path);
        using var doc = PdfOperations.OpenForImport(path);
        var info = doc.Info;

        double w = 0, h = 0;
        if (doc.PageCount > 0)
        {
            var page = doc.Pages[0];
            w = page.Width.Point;
            h = page.Height.Point;
            if (PdfOperations.Normalize(page.Rotate) is 90 or 270) (w, h) = (h, w);
        }

        int v = doc.Version;
        return new DocumentProperties(
            file.FullName,
            file.Length,
            doc.PageCount,
            w, h,
            v > 0 ? $"PDF-{v / 10}.{v % 10}" : "desconhecida",
            new DocumentMetadata(info.Title ?? "", info.Author ?? "", info.Subject ?? "", info.Keywords ?? ""),
            info.Creator ?? "",
            info.Producer ?? "",
            ValidDate(info.CreationDate),
            ValidDate(info.ModificationDate),
            IsEncrypted(path),
            ReadSignatures(doc, path, checkRevocationOnline));
    }

    /// <summary>
    /// Grava título, autor, assunto e palavras-chave. O PDF é regravado por inteiro,
    /// então assinaturas digitais existentes deixam de ser válidas.
    /// </summary>
    public static void Save(string inputPath, DocumentMetadata metadata, string outputPath)
    {
        PdfDocument doc;
        try
        {
            doc = PdfReader.Open(inputPath, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("O PDF está protegido e não permite alterar as propriedades.", ex);
        }

        using (doc)
        {
            doc.Info.Title = metadata.Title.Trim();
            doc.Info.Author = metadata.Author.Trim();
            doc.Info.Subject = metadata.Subject.Trim();
            doc.Info.Keywords = metadata.Keywords.Trim();
            doc.Info.ModificationDate = DateTime.Now;
            PdfOperations.SaveSafely(doc, outputPath);
        }
    }

    /// <summary>Converte o tamanho da página de pontos para a unidade escolhida.</summary>
    public static double Convert(double points, PageSizeUnit unit) => unit switch
    {
        PageSizeUnit.Centimeters => points * 2.54 / 72,
        PageSizeUnit.Millimeters => points * 25.4 / 72,
        PageSizeUnit.Inches => points / 72,
        _ => points,
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.0} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.00} GB",
    };

    /// <summary>Procura o dicionário /Encrypt no arquivo (criptografia, com ou sem senha de abertura).</summary>
    private static bool IsEncrypted(string path)
    {
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return text.Contains("/Encrypt", StringComparison.Ordinal);
    }

    private static DateTime? ValidDate(DateTime d) => d == DateTime.MinValue || d.Year < 1980 ? null : d;

    // ---------------- Assinaturas existentes ----------------

    /// <summary>
    /// Verificação rápida: o PDF tem alguma assinatura preenchida? Usada para avisar o usuário
    /// antes de uma alteração que vai invalidá-la (não confere o certificado).
    /// </summary>
    public static bool HasSignatures(string path)
    {
        using var doc = PdfOperations.OpenForImport(path);
        return HasSignatures(doc);
    }

    /// <summary>
    /// <paramref name="current"/> é o <paramref name="original"/> com coisas só acrescentadas no fim (os bytes do
    /// original estão intactos no começo) — ou seja, as assinaturas do original continuam valendo para a parte delas.
    /// </summary>
    public static bool OnlyAppended(string original, string current)
    {
        using var a = File.OpenRead(original);
        using var b = File.OpenRead(current);
        if (b.Length < a.Length) return false;
        var bufA = new byte[81920];
        var bufB = new byte[81920];
        int read;
        while ((read = a.Read(bufA, 0, bufA.Length)) > 0)
        {
            b.ReadExactly(bufB, 0, read);
            if (!bufA.AsSpan(0, read).SequenceEqual(bufB.AsSpan(0, read))) return false;
        }
        return true;
    }

    internal static bool HasSignatures(PdfDocument doc)
    {
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        return fields is not null && Flatten(fields).Any(f =>
            f.Elements.GetName("/FT") == "/Sig" && f.Elements.GetDictionary("/V") is not null);
    }

    private static List<SignatureDetails> ReadSignatures(PdfDocument doc, string path, bool online)
    {
        var result = new List<SignatureDetails>();
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        if (fields is null) return result;

        byte[]? bytes = null;
        foreach (var field in Flatten(fields))
        {
            if (field.Elements.GetName("/FT") != "/Sig") continue;
            var v = field.Elements.GetDictionary("/V");
            if (v is null) continue; // campo de assinatura vazio

            bytes ??= File.ReadAllBytes(path);
            var check = Verify(bytes, v.Elements.GetArray("/ByteRange"), online);
            int? level = CertificationLevelOf(v);
            var changes = check.Whole || check.Intact != true ? SignatureChanges.None
                : SignatureRevisions.After(bytes, check.SignedEnd, doc, level);

            result.Add(new SignatureDetails(
                field.Elements.GetString("/T"),
                PageOf(doc, field),
                check.Cert?.GetNameInfo(X509NameType.SimpleName, false) ?? NullIfEmpty(v.Elements.GetString("/Name")),
                check.Cert?.GetNameInfo(X509NameType.SimpleName, true),
                NullIfEmpty(v.Elements.GetString("/Reason")),
                NullIfEmpty(v.Elements.GetString("/Location")),
                NullIfEmpty(v.Elements.GetString("/ContactInfo")),
                // A hora gravada dentro da assinatura vale mais que o /M (que fica fora dela e qualquer um muda).
                check.SigningTime ?? ParsePdfDate(v.Elements.GetString("/M")),
                check.Intact, check.Whole, check.Info, check.TimestampAt, level, changes, check.TimestampState, check.BadLayout));
            check.Cert?.Dispose();
        }
        return result;
    }

    /// <summary>Nível de certificação (DocMDP, /Reference → /TransformParams /P) da assinatura, se for de certificação.</summary>
    private static int? CertificationLevelOf(PdfDictionary signature)
    {
        if (signature.Elements.GetArray("/Reference") is not { } refs) return null;
        for (int i = 0; i < refs.Elements.Count; i++)
        {
            var r = refs.Elements.GetDictionary(i);
            if (r?.Elements.GetName("/TransformMethod") != "/DocMDP") continue;
            var p = r.Elements.GetDictionary("/TransformParams");
            return p is not null && p.Elements.ContainsKey("/P") ? p.Elements.GetInteger("/P") : 2; // sem /P = 2 (norma)
        }
        return null;
    }

    /// <summary>Todos os campos, descendo nos /Kids (com limite e sem repetir: um arquivo malfeito pode ter laços).</summary>
    private static IEnumerable<PdfDictionary> Flatten(PdfArray fields)
    {
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        var stack = new Stack<(PdfArray Array, int Depth)>();
        stack.Push((fields, 0));
        while (stack.Count > 0)
        {
            var (array, depth) = stack.Pop();
            for (int i = 0; i < array.Elements.Count; i++)
            {
                var f = array.Elements.GetDictionary(i);
                if (f is null || !seen.Add(f) || seen.Count > 100_000) continue;
                yield return f;
                if (depth < 32 && f.Elements.GetArray("/Kids") is { } kids) stack.Push((kids, depth + 1));
            }
        }
    }

    private static int? PageOf(PdfDocument doc, PdfDictionary field)
    {
        for (int p = 0; p < doc.PageCount; p++)
        {
            var annots = doc.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var a = annots.Elements.GetDictionary(i);
                if (ReferenceEquals(a, field) || ReferenceEquals(a?.Elements.GetDictionary("/Parent"), field))
                    return p + 1;
            }
        }
        return null;
    }

    private sealed record Checked(bool? Intact, bool Whole, long SignedEnd, X509Certificate2? Cert, SignerCertificate? Info,
        DateTime? TimestampAt, TimestampCheck TimestampState, DateTime? SigningTime, bool BadLayout)
    {
        public static readonly Checked Nothing = new(null, false, 0, null, null, null, TimestampCheck.None, null, false);
    }

    /// <summary>Confere o CMS contra os bytes do /ByteRange, como um leitor de PDF faria.</summary>
    private static Checked Verify(byte[] bytes, PdfArray? byteRange, bool online)
    {
        if (byteRange is null || byteRange.Elements.Count != 4) return Checked.Nothing;
        try
        {
            long a = byteRange.Elements.GetInteger(0), b = byteRange.Elements.GetInteger(1),
                 c = byteRange.Elements.GetInteger(2), d = byteRange.Elements.GetInteger(3);
            if (a != 0 || b <= 0 || c <= b || d < 0 || c + d > bytes.Length) return Checked.Nothing;
            bool whole = c + d == bytes.Length;

            // O trecho entre b e c tem de ser exatamente o valor hexadecimal de /Contents: <....>, logo depois de "/Contents".
            string hex = Encoding.ASCII.GetString(bytes, (int)b, (int)(c - b));
            bool layoutOk = hex.Length >= 2 && hex[0] == '<' && hex[^1] == '>' && ContentsKeyBefore(bytes, (int)b)
                            && ByteRangeInsideSignedPart(bytes, a, b, c, d);
            if (!layoutOk)
                return Checked.Nothing with { Intact = false, Whole = whole, SignedEnd = c + d, BadLayout = true };
            byte[] cms = TrimDer(System.Convert.FromHexString(hex[1..^1]));

            byte[] signedData = [.. bytes.AsSpan(0, (int)b), .. bytes.AsSpan((int)c, (int)d)];
            var signed = new SignedCms(new ContentInfo(signedData), detached: true);
            signed.Decode(cms);
            var signer = signed.SignerInfos.Count > 0 ? signed.SignerInfos[0] : null;
            var cert = signer?.Certificate is { } sc ? X509CertificateLoader.LoadCertificate(sc.RawData) : null;

            DateTime? timestamp = null, signingTime = null;
            var tsState = TimestampCheck.None;
            SignerCertificate? info = null;
            try
            {
                if (signer is not null)
                {
                    (timestamp, tsState) = SignerCertificate.CheckTimestamp(signer, signed.Certificates);
                    signingTime = SignerCertificate.SigningTimeOf(signer);
                }
                if (cert is not null)
                    info = SignerCertificate.From(cert, signed.Certificates,
                        tsState == TimestampCheck.Trusted ? timestamp : signingTime, online);
            }
            catch (CryptographicException) { } // detalhes do certificado são extras: a verificação continua

            bool intact;
            try
            {
                signed.CheckSignature(verifySignatureOnly: true);
                intact = true;
            }
            catch (CryptographicException)
            {
                intact = false;
            }
            return new Checked(intact, whole, c + d, cert, info, timestamp, tsState, signingTime, false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Checked.Nothing;
        }
    }

    /// <summary>Logo antes do valor assinado tem de vir a chave /Contents (o trecho pulado é a própria assinatura).</summary>
    private static bool ContentsKeyBefore(byte[] bytes, int at)
    {
        int i = at - 1;
        while (i >= 0 && bytes[i] is (byte)' ' or (byte)'\n' or (byte)'\r' or (byte)'\t') i--;
        const string key = "/Contents";
        return i + 1 >= key.Length && Encoding.ASCII.GetString(bytes, i + 1 - key.Length, key.Length) == key;
    }

    /// <summary>O próprio /ByteRange tem de estar dentro da parte assinada, perto da assinatura (senão alguém o trocou).</summary>
    private static bool ByteRangeInsideSignedPart(byte[] bytes, long a, long b, long c, long d)
    {
        const int window = 64 * 1024;
        var pattern = new System.Text.RegularExpressions.Regex($@"/ByteRange\s*\[\s*{a}\s+{b}\s+{c}\s+{d}\s*\]");
        int from = (int)Math.Max(0, b - window);
        if (pattern.IsMatch(Encoding.Latin1.GetString(bytes, from, (int)(b - from)))) return true;
        int length = (int)Math.Min(window, d);
        return pattern.IsMatch(Encoding.Latin1.GetString(bytes, (int)c, length));
    }

    /// <summary>Remove os zeros de preenchimento depois da estrutura DER.</summary>
    private static byte[] TrimDer(byte[] data)
    {
        if (data.Length < 2 || data[0] != 0x30) return data;
        int lenByte = data[1], header = 2, length = lenByte;
        if (lenByte > 0x80)
        {
            int n = lenByte & 0x7F;
            length = 0;
            for (int i = 0; i < n; i++) length = (length << 8) | data[2 + i];
            header = 2 + n;
        }
        return header + length <= data.Length ? data[..(header + length)] : data;
    }

    /// <summary>Lê datas no formato do PDF: D:AAAAMMDDHHmmSS+HH'mm'.</summary>
    public static DateTime? ParsePdfDate(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        s = s.Trim();
        if (s.StartsWith("D:")) s = s[2..];
        string digits = new(s.TakeWhile(char.IsDigit).ToArray());
        if (digits.Length < 4) return null;
        digits = digits.PadRight(14, '0');
        if (digits[4..6] == "00") digits = digits[..4] + "01" + digits[6..];
        if (digits[6..8] == "00") digits = digits[..6] + "01" + digits[8..];
        if (!DateTime.TryParseExact(digits[..14], "yyyyMMddHHmmss", null,
                System.Globalization.DateTimeStyles.None, out var local))
            return null;

        string rest = s[Math.Min(s.Length, new string(s.TakeWhile(char.IsDigit).ToArray()).Length)..];
        if (rest.Length == 0) return local;
        if (rest[0] == 'Z') return DateTime.SpecifyKind(local, DateTimeKind.Utc).ToLocalTime();
        if (rest[0] is '+' or '-')
        {
            var parts = rest[1..].Replace("'", " ").Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int hh = parts.Length > 0 && int.TryParse(parts[0], out var x) ? x : 0;
            int mm = parts.Length > 1 && int.TryParse(parts[1], out var y) ? y : 0;
            var offset = new TimeSpan(hh, mm, 0) * (rest[0] == '-' ? -1 : 1);
            return new DateTimeOffset(local, offset).LocalDateTime;
        }
        return local;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}
