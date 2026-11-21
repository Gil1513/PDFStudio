using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PDFStudio.Core;

/// <summary>Os dados de quem vai assinar, que vão dentro do certificado.</summary>
/// <param name="Country">Sigla de 2 letras (BR).</param>
public sealed record CertificateIdentity(string Name, string? Email = null, string? Organization = null,
    string? OrganizationalUnit = null, string? City = null, string? State = null, string? Country = "BR");

/// <summary>
/// Cria um certificado digital (.pfx) autoassinado com os dados digitados, para assinar PDFs no PDF Studio.
/// Os leitores de PDF mostram o nome, mas a identidade aparece como "desconhecida" até quem recebe confiar no
/// certificado (não é um certificado ICP-Brasil emitido por uma autoridade).
/// </summary>
public static class CertificateMaker
{
    public const int MinPasswordLength = 6, MaxYears = 10;

    /// <summary>Confere os dados. Devolve a mensagem do problema, ou null se estiver tudo certo.</summary>
    public static string? Problem(CertificateIdentity id, string password, int years)
    {
        if (string.IsNullOrWhiteSpace(id.Name)) return "Escreva o nome de quem vai assinar.";
        if (id.Name.Trim().Length > 64) return "O nome pode ter no máximo 64 letras.";
        if (!string.IsNullOrWhiteSpace(id.Email) && !System.Text.RegularExpressions.Regex.IsMatch(id.Email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$"))
            return "O e-mail não parece válido.";
        if (!string.IsNullOrWhiteSpace(id.Country) && !System.Text.RegularExpressions.Regex.IsMatch(id.Country.Trim(), "^[A-Za-z]{2}$"))
            return "O país deve ser a sigla de 2 letras (ex.: BR).";
        if (password.Length < MinPasswordLength) return $"A senha precisa ter pelo menos {MinPasswordLength} caracteres.";
        if (years < 1 || years > MaxYears) return $"A validade deve ser de 1 a {MaxYears} anos.";
        return null;
    }

    /// <summary>O "nome completo" (DN) do certificado: CN=…, E=…, O=…, OU=…, L=…, S=…, C=….</summary>
    public static X500DistinguishedName Subject(CertificateIdentity id)
    {
        var b = new X500DistinguishedNameBuilder();
        // A ordem do builder é invertida na leitura: o país fica por fora e o nome por dentro (como de costume).
        if (Clean(id.Country) is { } c) b.AddCountryOrRegion(c.ToUpperInvariant());
        if (Clean(id.State) is { } st) b.AddStateOrProvinceName(st);
        if (Clean(id.City) is { } l) b.AddLocalityName(l);
        if (Clean(id.Organization) is { } o) b.AddOrganizationName(o);
        if (Clean(id.OrganizationalUnit) is { } ou) b.AddOrganizationalUnitName(ou);
        if (Clean(id.Email) is { } e) b.AddEmailAddress(e);
        b.AddCommonName(id.Name.Trim());
        return b.Build();
    }

    /// <summary>
    /// Cria o certificado (RSA 2048, SHA-256, uso: assinatura digital e não repúdio) e grava em
    /// <paramref name="pfxPath"/> protegido por <paramref name="password"/>. Erro nos dados = ArgumentException.
    /// </summary>
    public static CertificateSummary CreatePfx(CertificateIdentity id, string password, int years, string pfxPath)
    {
        if (Problem(id, password, years) is { } problem) throw new ArgumentException(problem);
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(Subject(id), rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(
            X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, critical: true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
        [
            new Oid("1.3.6.1.5.5.7.3.4"),       // proteção de e-mail
            new Oid("1.3.6.1.4.1.311.10.3.12"), // assinatura de documentos
            new Oid("1.2.840.113583.1.1.5"),    // documentos autênticos (leitores de PDF)
        ], critical: false));
        request.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(request.PublicKey, false));
        if (Clean(id.Email) is { } email)
        {
            var san = new SubjectAlternativeNameBuilder();
            san.AddEmailAddress(email);
            request.CertificateExtensions.Add(san.Build());
        }

        var start = DateTimeOffset.Now.AddMinutes(-5); // folga para relógios um pouco atrasados
        using var cert = request.CreateSelfSigned(start, start.AddYears(years));
        byte[] pfx = cert.ExportPkcs12(
            new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 200_000), password); // senha mais difícil de adivinhar por força bruta
        string full = Path.GetFullPath(pfxPath), temp = full + ".tmp";
        try
        {
            File.WriteAllBytes(temp, pfx);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
            CryptographicOperations.ZeroMemory(pfx);
        }
        return PdfSigning.InspectCertificate(full, password);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
