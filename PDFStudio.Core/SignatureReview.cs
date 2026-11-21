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
    IReadOnlyList<string> Usages,
    /// <summary>Se o certificado foi cancelado (revogado) pela autoridade que o emitiu.</summary>
    RevocationCheck Revocation = RevocationCheck.NotApplicable,
    /// <summary>Problema no uso permitido do certificado (ex.: feito só para cifrar, não para assinar). null = nenhum.</summary>
    string? UsageProblem = null)
{
    /// <summary>Usos (EKU) que servem para assinar documentos.</summary>
    private static readonly HashSet<string> SigningPurposes =
    [
        "1.3.6.1.5.5.7.3.4",        // proteção de e-mail (é o que os certificados ICP-Brasil e-CPF/e-CNPJ trazem)
        "1.3.6.1.5.5.7.3.36",       // assinatura de documentos
        "1.3.6.1.4.1.311.10.3.12",  // assinatura de documentos (Microsoft)
        "1.2.840.113583.1.1.5",     // documentos autênticos (PDF)
        "1.3.6.1.5.5.7.3.2",        // autenticação de cliente (comum junto com os acima)
        "2.5.29.37.0",              // qualquer uso
    ];

    /// <summary>
    /// Lê o certificado; <paramref name="extra"/> = outros certificados que vieram na assinatura (cadeia).
    /// <paramref name="at"/> = momento em que a cadeia é conferida (hora confiável da assinatura, se houver).
    /// <paramref name="online"/> = pode perguntar pela internet se o certificado foi cancelado.
    /// </summary>
    public static SignerCertificate From(X509Certificate2 cert, X509Certificate2Collection? extra = null, DateTime? at = null, bool online = false)
    {
        string? Part(string oid) => cert.SubjectName.EnumerateRelativeDistinguishedNames()
            .Where(r => r.GetSingleElementType().Value == oid).Select(r => r.GetSingleElementValue()).FirstOrDefault(v => !string.IsNullOrWhiteSpace(v));

        bool trusted;
        try
        {
            using var chain = NewChain(extra, at);
            chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
            trusted = chain.Build(cert);
        }
        catch (CryptographicException)
        {
            trusted = false;
        }

        var revocation = RevocationCheck.NotApplicable;
        if (trusted)
        {
            try
            {
                using var chain = NewChain(extra, at);
                chain.ChainPolicy.RevocationMode = online ? X509RevocationMode.Online : X509RevocationMode.Offline;
                chain.ChainPolicy.RevocationFlag = X509RevocationFlag.EntireChain;
                // Sem internet, a não ser que a pessoa peça: abrir um PDF não deve acessar endereços escritos nele.
                chain.ChainPolicy.DisableCertificateDownloads = !online;
                if (online) chain.ChainPolicy.UrlRetrievalTimeout = TimeSpan.FromSeconds(15);
                chain.Build(cert);
                var flags = chain.ChainElements.SelectMany(e => e.ChainElementStatus).Select(x => x.Status)
                    .Aggregate(X509ChainStatusFlags.NoError, (acc, f) => acc | f);
                revocation = flags.HasFlag(X509ChainStatusFlags.Revoked) ? RevocationCheck.Revoked
                    : flags.HasFlag(X509ChainStatusFlags.RevocationStatusUnknown) || flags.HasFlag(X509ChainStatusFlags.OfflineRevocation)
                        ? RevocationCheck.NotChecked
                        : RevocationCheck.Good;
            }
            catch (CryptographicException)
            {
                revocation = RevocationCheck.NotChecked;
            }
        }

        string key = cert.GetRSAPublicKey() is { } rsa ? $"RSA {rsa.KeySize} bits"
            : cert.GetECDsaPublicKey() is { } ec ? $"ECDSA {ec.KeySize} bits"
            : cert.PublicKey.Oid.FriendlyName ?? cert.PublicKey.Oid.Value ?? "desconhecida";

        var usages = new List<string>();
        string? usageProblem = null;
        foreach (var ext in cert.Extensions)
        {
            if (ext is X509KeyUsageExtension ku)
            {
                if (!ku.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature) && !ku.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation))
                    usageProblem = "O certificado não foi feito para assinar (o uso dele não inclui assinatura digital).";
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.DigitalSignature)) usages.Add("Assinatura digital");
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.NonRepudiation)) usages.Add("Não repúdio");
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.KeyEncipherment)) usages.Add("Cifrar chaves");
                if (ku.KeyUsages.HasFlag(X509KeyUsageFlags.KeyCertSign)) usages.Add("Emitir certificados");
            }
            else if (ext is X509EnhancedKeyUsageExtension eku)
            {
                if (eku.EnhancedKeyUsages.Count > 0 && !eku.EnhancedKeyUsages.Cast<Oid>().Any(o => o.Value is { } v && SigningPurposes.Contains(v)))
                    usageProblem ??= "O certificado foi feito para outra finalidade (ex.: site ou programa), não para assinar documentos.";
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
            usages.Where(u => u.Length > 0).Distinct().ToList(),
            revocation, usageProblem);
    }

    private static X509Chain NewChain(X509Certificate2Collection? extra, DateTime? at)
    {
        var chain = new X509Chain();
        chain.ChainPolicy.VerificationFlags = X509VerificationFlags.IgnoreNotTimeValid; // a data é conferida à parte
        chain.ChainPolicy.DisableCertificateDownloads = true;
        if (at is { } when) chain.ChainPolicy.VerificationTime = when;
        if (extra is not null) chain.ChainPolicy.ExtraStore.AddRange(extra);
        return chain;
    }

    /// <summary>Hora que quem assinou gravou dentro da assinatura (atributo signingTime), se houver.</summary>
    internal static DateTime? SigningTimeOf(SignerInfo signer)
    {
        foreach (var attr in signer.SignedAttributes)
        {
            if (attr.Oid.Value != "1.2.840.113549.1.9.5" || attr.Values.Count == 0) continue;
            try { return new Pkcs9SigningTime(attr.Values[0].RawData).SigningTime.ToLocalTime(); }
            catch (CryptographicException) { return null; }
        }
        return null;
    }

    /// <summary>
    /// Confere o carimbo de tempo (RFC 3161): se ele é mesmo desta assinatura e se o servidor que o deu é
    /// reconhecido pelo Windows. Só um carimbo conferido serve como "hora confiável".
    /// </summary>
    internal static (DateTime? At, TimestampCheck State) CheckTimestamp(SignerInfo signer, X509Certificate2Collection? extra = null)
    {
        foreach (var attr in signer.UnsignedAttributes)
        {
            if (attr.Oid.Value != "1.2.840.113549.1.9.16.2.14") continue;
            foreach (var value in attr.Values)
            {
                if (!Rfc3161TimestampToken.TryDecode(value.RawData, out var token, out _)) return (null, TimestampCheck.Broken);
                var at = token.TokenInfo.Timestamp.LocalDateTime;
                var candidates = new X509Certificate2Collection();
                candidates.AddRange(token.AsSignedCms().Certificates);
                if (extra is not null) candidates.AddRange(extra);
                if (!token.VerifySignatureForSignerInfo(signer, out var tsa, candidates) || tsa is null)
                    return (at, TimestampCheck.Broken);
                using (tsa)
                {
                    try
                    {
                        using var chain = NewChain(candidates, at);
                        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                        chain.ChainPolicy.ApplicationPolicy.Add(new Oid("1.3.6.1.5.5.7.3.8")); // carimbo de tempo
                        bool ok = chain.Build(tsa) && at >= tsa.NotBefore && at <= tsa.NotAfter;
                        return (at, ok ? TimestampCheck.Trusted : TimestampCheck.UnknownServer);
                    }
                    catch (CryptographicException)
                    {
                        return (at, TimestampCheck.UnknownServer);
                    }
                }
            }
        }
        return (null, TimestampCheck.None);
    }

    private static string? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;
}

/// <summary>Se o certificado foi cancelado (revogado).</summary>
public enum RevocationCheck
{
    /// <summary>Não se aplica: o certificado não vem de uma autoridade reconhecida (ex.: autoassinado).</summary>
    NotApplicable,
    /// <summary>Conferido: não foi cancelado.</summary>
    Good,
    /// <summary>Foi cancelado pela autoridade.</summary>
    Revoked,
    /// <summary>Não deu para saber (sem a lista de cancelados neste computador e sem consultar a internet).</summary>
    NotChecked,
}

/// <summary>
/// Valid = válida; ValidWithChanges = válida, e o documento recebeu depois só alterações permitidas (comentários,
/// outra assinatura, formulário); Unknown = não dá para confirmar; Invalid = não vale.
/// </summary>
public enum SignatureStatus { Valid, ValidWithChanges, Unknown, Invalid }

/// <summary>Conclusão sobre uma assinatura: situação, frase principal e o resumo (uma frase por item).</summary>
public sealed record SignatureReport(SignatureStatus Status, string Headline, IReadOnlyList<string> Summary);

/// <summary>Tela "Propriedades da assinatura": decide se a assinatura é válida, desconhecida ou inválida.</summary>
public static class SignatureReview
{
    /// <param name="trustedByUser">A pessoa marcou este certificado como confiável neste computador.</param>
    public static SignatureReport Evaluate(SignatureDetails sig, bool trustedByUser) => Evaluate(sig, trustedByUser, DateTime.Now);

    internal static SignatureReport Evaluate(SignatureDetails sig, bool trustedByUser, DateTime now)
    {
        var lines = new List<string>();
        bool invalid = false, unknown = false;
        var changes = sig.ChangesAfter;

        if (sig.BadLayout)
        {
            lines.Add("A assinatura está montada de um jeito irregular: o trecho assinado não corresponde ao lugar dela no arquivo. Isso pode ser uma tentativa de enganar.");
            invalid = true;
        }
        else switch (sig.IsIntact)
        {
            case true when sig.CoversWholeFile:
                lines.Add("O documento não foi alterado desde que esta assinatura foi feita.");
                break;
            case true when changes.HasFlag(SignatureChanges.NotAllowed):
                lines.Add("Depois desta assinatura o documento recebeu alterações que não são permitidas (por exemplo, no texto, nas imagens ou nas páginas). O que aparece na tela pode não ser o que foi assinado.");
                invalid = true;
                break;
            case true when changes.HasFlag(SignatureChanges.Unknown):
                lines.Add("A parte assinada está intacta, mas o documento recebeu alterações depois desta assinatura que não puderam ser conferidas.");
                unknown = true;
                break;
            case true:
                lines.Add($"A parte assinada está intacta. Depois desta assinatura o documento recebeu {Describe(changes)}, o que é permitido e não muda o conteúdo assinado.");
                break;
            case false:
                lines.Add("O documento foi alterado depois desta assinatura: ela não vale mais.");
                invalid = true;
                break;
            default:
                lines.Add("Não foi possível conferir se o documento foi alterado depois desta assinatura.");
                unknown = true;
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

        if (cert?.UsageProblem is { } usage)
        {
            lines.Add(usage);
            invalid = true;
        }

        switch (cert?.Revocation)
        {
            case RevocationCheck.Revoked:
                lines.Add("O certificado foi CANCELADO (revogado) pela autoridade que o emitiu.");
                invalid = true;
                break;
            case RevocationCheck.Good:
                lines.Add("O certificado não está na lista de certificados cancelados.");
                break;
            case RevocationCheck.NotChecked:
                lines.Add("Não foi possível conferir se o certificado foi cancelado (o Windows não tem a lista guardada). Use \"Conferir cancelamento pela internet\" para consultar.");
                break;
        }

        // Hora confiável = só a do carimbo de tempo conferido. A outra é a que o computador de quem assinou informou.
        DateTime? trustedTime = sig.TimestampState == TimestampCheck.Trusted ? sig.TimestampAt : null;
        switch (sig.TimestampState)
        {
            case TimestampCheck.Trusted:
                lines.Add($"O horário vem de um carimbo de tempo de um servidor confiável: {sig.TimestampAt:dd/MM/yyyy HH:mm:ss}.");
                break;
            case TimestampCheck.UnknownServer:
                lines.Add($"Há um carimbo de tempo ({sig.TimestampAt:dd/MM/yyyy HH:mm:ss}), mas de um servidor que o Windows não reconhece; o horário não pode ser confirmado.");
                break;
            case TimestampCheck.Broken:
                lines.Add("O carimbo de tempo desta assinatura não confere com ela.");
                unknown = true;
                break;
            default:
                lines.Add("O horário da assinatura vem do relógio do computador de quem assinou.");
                break;
        }

        if (cert is not null)
        {
            DateTime? when = trustedTime ?? sig.SignedAt;
            if (when is { } at && (at < cert.NotBefore || at > cert.NotAfter))
            {
                lines.Add($"O certificado estava fora do prazo de validade na data da assinatura (válido de {cert.NotBefore:dd/MM/yyyy} a {cert.NotAfter:dd/MM/yyyy}).");
                invalid = true;
            }
            else if (trustedTime is null && now > cert.NotAfter)
            {
                lines.Add($"O certificado venceu em {cert.NotAfter:dd/MM/yyyy} e não há carimbo de tempo confiável que prove que a assinatura foi feita antes disso.");
                unknown = true;
            }
        }

        switch (sig.CertificationLevel)
        {
            case 1: lines.Add("Este documento foi certificado e quem certificou não permite nenhuma alteração."); break;
            case 2: lines.Add("Este documento foi certificado: só é permitido preencher formulários e assinar."); break;
            case 3: lines.Add("Este documento foi certificado: é permitido preencher formulários, assinar e comentar."); break;
        }

        bool laterChanges = !sig.CoversWholeFile && changes != SignatureChanges.None;
        var status = invalid ? SignatureStatus.Invalid
            : unknown || !identityOk || sig.IsIntact != true ? SignatureStatus.Unknown
            : laterChanges ? SignatureStatus.ValidWithChanges
            : SignatureStatus.Valid;
        string headline = status switch
        {
            SignatureStatus.Valid => "A assinatura é VÁLIDA.",
            SignatureStatus.ValidWithChanges => $"A assinatura é VÁLIDA, com {Describe(changes)} depois dela.",
            SignatureStatus.Invalid => "A assinatura é INVÁLIDA.",
            _ => "A validade da assinatura é DESCONHECIDA.",
        };
        return new SignatureReport(status, headline, lines);
    }

    /// <summary>"comentários", "outra assinatura", "comentários e outra assinatura"…</summary>
    public static string Describe(SignatureChanges changes)
    {
        var parts = new List<string>();
        if (changes.HasFlag(SignatureChanges.Comments)) parts.Add("comentários");
        if (changes.HasFlag(SignatureChanges.FormFill)) parts.Add("preenchimento de formulário");
        if (changes.HasFlag(SignatureChanges.Signatures)) parts.Add("outra assinatura");
        return parts.Count switch
        {
            0 => "alterações",
            1 => parts[0],
            _ => string.Join(", ", parts[..^1]) + " e " + parts[^1],
        };
    }
}
