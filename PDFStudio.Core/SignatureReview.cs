using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;

namespace PDFStudio.Core;

/// <summary>O certificado de quem assinou, já lido para mostrar na tela "Propriedades da assinatura".</summary>
public sealed record SignerCertificate(
    string Name,
    string? Email,
    string? Organization,
    string? OrganizationalUnit,
    string? City,
    string? State,
    string? Country,
    string Subject,
    string IssuerName,
    string Issuer,
    string SerialNumber,
    DateTime NotBefore,
    DateTime NotAfter,
    /// <summary>Impressão digital SHA-256 (hexadecimal), usada para "confiar neste certificado".</summary>
    string Sha256,
    string Sha1,
    bool IsSelfSigned,
    /// <summary>A cadeia chega a uma autoridade certificadora confiável do Windows (ex.: ICP-Brasil instalada).</summary>
    bool IsTrustedBySystem,
    string KeyDescription,
    string SignatureAlgorithm,
    IReadOnlyList<string> Usages)
{
    /// <summary>Lê o certificado; <paramref name="extra"/> = outros certificados que vieram na assinatura (cadeia).</summary>
    public static SignerCertificate From(X509Certificate2 cert, X509Certificate2Collection? extra = null)
    {
        string? Part(string oid) => cert.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(r => r.GetSingleElementType().Value == oid).Select(r => r.GetSingleElementValue()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        bool trusted;
        try
        {
            using var chain = new X509Chain();
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid; // a data é conferida à parte
            if (extra is not null) chain.ChainPolicy.ExtraStore.AddRange(extra);
            trusted = chain.Build(cert);
        }
        catch (CryptographicException)
        {
            trusted = false;
        }

        string key = cert.GetRSAPublicKey() is { } rsa ? $"RSA {rsa.KeySize} bits"
            : cert.GetECDsaPublicKey() is { } ec ? $"ECDSA {ec.KeySize} bits"
            : cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "desconhecida";

        var usages = new List<string>();
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509KeyUsageExtension ku)
            {
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)) usages.Add("Assinatura digital");
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation)) usages.Add("Não repúdio");
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment)) usages.Add("Cifrar chaves");
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign)) usages.Add("Emitir certificados");
            }
            else if (ext is X509EnhancedKeyUsageExtension eku)
            {
                foreach (var oid in eku.EnhancedKeyUsages)
                    usages.Add(oid.Value switch
                    {
                        "1.3.6.1.5.5.7.3.4" => "Proteção de e-mail",
                        "1.3.6.1.4.1.311.10.3.12" => "Assinatura de documentos",
                        "1.2.840.113583.1.1.5" => "Documentos autênticos (PDF)",
                        "1.3.6.1.5.5.7.3.2" => "Autenticação de cliente",
                        _ => oid.FriendlyName ?? oid.Value ?? "",
                    });
            }
        }

        return new SignerCertificate(
            cert.GetNameInfo(X509NameType.SimpleName, false),
            Part("1.2.840.113549.1.9.1") ?? NullIfEmpty(cert.GetNameInfo(X509NameType.EmailName, false)),
            Part("2.5.4.10"), Part("2.5.4.11"), Part("2.5.4.7"), Part("2.5.4.8"), Part("2.5.4.6"),
            cert.Subject,
            cert.GetNameInfo(X509NameType.SimpleName, true),
            cert.Issuer,
            cert.SerialNumber,
            cert.NotBefore, cert.NotAfter,
            Convert.ToHexString(cert.GetCertHash(HashAlgorithmName.SHA256)),
            cert.Thumbprint,
            cert.SubjectName.RawData.AsSpan().SequenceEqual(cert.IssuerName.RawData),
            trusted,
            key,
            cert.SignatureAlgorithm.FriendlyName ?? cert.SignatureAlgorithm.Value ?? "",
            usages.Where(u => u.Length > 0).Distinct().ToList());
    }

    /// <summary>Hora de um carimbo de tempo (RFC 3161) que veio dentro da assinatura, se houver.</summary>
    internal static DateTime? TimestampOf(SignerInfo signer)
    {
        foreach (var attr in signer.UnsignedAttributes)
        {
            if (attr.Oid.Value != "1.2.840.113549.1.9.16.2.14") continue;
            foreach (var value in attr.Values)
                if (Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _))
                    return token.TokenInfo.Timestamp.LocalDateTime;
        }
        return null;
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

public enum SignatureStatus { Valid, Unknown, Invalid }

/// <summary>Conclusão sobre uma assinatura: situação, frase principal e o resumo (uma frase por item).</summary>
public sealed record SignatureReport(SignatureStatus Status, string Headline, IReadOnlyList<string> Summary);

/// <summary>Tela "Propriedades da assinatura": decide se a assinatura é válida, desconhecida ou inválida.</summary>
public static class SignatureReview
{
    /// <param name="trustedByUser">A pessoa marcou este certificado como confiável neste computador.</param>
    public static SignatureReport Evaluate(SignatureDetails sig, bool trustedByUser)
    {
        var lines = new List<string>();
        bool invalid = false;

        switch (sig.IsIntact)
        {
            case true when sig.CoversWholeFile:
                lines.Add("O documento não foi alterado desde que esta assinatura foi feita.");
                break;
            case true:
                lines.Add("A parte assinada está intacta, mas o documento recebeu alterações depois desta assinatura (por exemplo, outra assinatura ou comentários).");
                break;
            case false:
                lines.Add("O documento foi alterado depois desta assinatura: ela não vale mais.");
                invalid = true;
                break;
            default:
                lines.Add("Não foi possível conferir se o documento foi alterado depois desta assinatura.");
                break;
        }

        var cert = sig.Certificate;
        bool identityOk = false;
        if (cert is null)
            lines.Add("O certificado de quem assinou não veio na assinatura: a identidade não pode ser conferida.");
        else if (cert.IsTrustedBySystem)
        {
            lines.Add("A identidade de quem assinou foi confirmada por uma autoridade certificadora confiável instalada no Windows.");
            identityOk = true;
        }
        else if (trustedByUser)
        {
            lines.Add("A identidade de quem assinou é aceita porque você marcou este certificado como confiável neste computador.");
            identityOk = true;
        }
        else if (cert.IsSelfSigned)
            lines.Add("A identidade de quem assinou é desconhecida: o certificado foi criado pela própria pessoa (autoassinado) e não está na sua lista de certificados confiáveis.");
        else
            lines.Add("A identidade de quem assinou é desconhecida: não foi possível ligar o certificado a uma autoridade certificadora confiável, e ele não está na sua lista de certificados confiáveis.");

        if (cert is not null && sig.SignedAt is { } at && (at < cert.NotBefore || at > cert.NotAfter))
        {
            lines.Add($"O certificado estava fora do prazo de validade na data da assinatura (válido de {cert.NotBefore:dd/MM/yyyy} a {cert.NotAfter:dd/MM/yyyy}).");
            invalid = true;
        }

        switch (sig.CertificationLevel)
        {
            case 1: lines.Add("Este documento foi certificado e quem certificou não permite nenhuma alteração."); break;
            case 2: lines.Add("Este documento foi certificado: só é permitido preencher formulários e assinar."); break;
            case 3: lines.Add("Este documento foi certificado: é permitido preencher formulários, assinar e comentar."); break;
        }

        lines.Add(sig.TimestampAt is { } ts
            ? $"O horário vem de um carimbo de tempo de um servidor confiável: {ts:dd/MM/yyyy HH:mm:ss}."
            : "O horário da assinatura vem do relógio do computador de quem assinou.");

        var status = invalid ? SignatureStatus.Invalid : identityOk && sig.IsIntact == true ? SignatureStatus.Valid : SignatureStatus.Unknown;
        string headline = status switch
        {
            SignatureStatus.Valid => "A assinatura é VÁLIDA.",
            SignatureStatus.Invalid => "A assinatura é INVÁLIDA.",
            _ => "A validade da assinatura é DESCONHECIDA.",
        };
        return new SignatureReport(status, headline, lines);
    }
}
