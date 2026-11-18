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
    bool CoversWholeFile);

public enum PageSizeUnit { Centimeters, Millimeters, Inches, Points }

/// <summary>Leitura e gravação das propriedades do documento (PDFsharp 6.2.4, MIT).</summary>
public static class DocumentPropertiesService
{
    public static DocumentProperties Read(string path)
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
            ReadSignatures(doc, path));
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
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        return fields is not null && Flatten(fields).Any(f =>
            f.Elements.GetName("/FT") == "/Sig" && f.Elements.GetDictionary("/V") is not null);
    }

    private static List<SignatureDetails> ReadSignatures(PdfDocument doc, string path)
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
            var (intact, whole, cert) = Verify(bytes, v.Elements.GetArray("/ByteRange"));

            result.Add(new SignatureDetails(
                field.Elements.GetString("/T"),
                PageOf(doc, field),
                cert?.GetNameInfo(X509NameType.SimpleName, false) ?? NullIfEmpty(v.Elements.GetString("/Name")),
                cert?.GetNameInfo(X509NameType.SimpleName, true),
                NullIfEmpty(v.Elements.GetString("/Reason")),
                NullIfEmpty(v.Elements.GetString("/Location")),
                NullIfEmpty(v.Elements.GetString("/ContactInfo")),
                ParsePdfDate(v.Elements.GetString("/M")),
                intact, whole));
            cert?.Dispose();
        }
        return result;
    }

    private static IEnumerable<PdfDictionary> Flatten(PdfArray fields)
    {
        for (int i = 0; i < fields.Elements.Count; i++)
        {
            var f = fields.Elements.GetDictionary(i);
            if (f is null) continue;
            yield return f;
            if (f.Elements.GetArray("/Kids") is { } kids)
                foreach (var k in Flatten(kids)) yield return k;
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

    /// <summary>Confere o CMS contra os bytes do /ByteRange, como um leitor de PDF faria.</summary>
    private static (bool? Intact, bool Whole, X509Certificate2? Cert) Verify(byte[] bytes, PdfArray? byteRange)
    {
        if (byteRange is null || byteRange.Elements.Count != 4) return (null, false, null);
        try
        {
            long a = byteRange.Elements.GetInteger(0), b = byteRange.Elements.GetInteger(1),
                 c = byteRange.Elements.GetInteger(2), d = byteRange.Elements.GetInteger(3);
            if (a != 0 || b <= 0 || c <= b || c + d > bytes.Length) return (null, false, null);
            bool whole = c + d == bytes.Length;

            // O trecho entre b e c é o valor hexadecimal de /Contents: <....>
            string hex = Encoding.ASCII.GetString(bytes, (int)b, (int)(c - b)).Trim();
            if (!hex.StartsWith('<') || !hex.EndsWith('>')) return (null, whole, null);
            byte[] cms = TrimDer(System.Convert.FromHexString(hex[1..^1]));

            byte[] signedData = [.. bytes.AsSpan(0, (int)b), .. bytes.AsSpan((int)c, (int)d)];
            var signed = new SignedCms(new ContentInfo(signedData), detached: true);
            signed.Decode(cms);
            var cert = signed.SignerInfos.Count > 0 && signed.SignerInfos[0].Certificate is { } sc
                ? X509CertificateLoader.LoadCertificate(sc.RawData) : null;
            try
            {
                signed.CheckSignature(verifySignatureOnly: true);
                return (true, whole, cert);
            }
            catch (CryptographicException)
            {
                return (false, whole, cert);
            }
        }
        catch
        {
            return (null, false, null);
        }
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
