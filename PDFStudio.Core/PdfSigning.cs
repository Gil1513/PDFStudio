using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.IO;
using PdfSharp.Pdf.Signatures;

namespace PDFStudio.Core;

public enum SignaturePosition { BottomRight, BottomLeft, TopRight, TopLeft }

/// <summary>Parâmetros de uma assinatura.</summary>
public sealed class SignatureRequest
{
    public required string InputPath { get; init; }
    public required string OutputPath { get; init; }
    public required string PfxPath { get; init; }
    public string? PfxPassword { get; init; }

    /// <summary>true = carimbo visível na página; false = assinatura invisível (só nos dados do arquivo).</summary>
    public bool Visible { get; init; }
    /// <summary>Página do carimbo visível (1 em diante).</summary>
    public int PageNumber { get; init; } = 1;
    public SignaturePosition Position { get; init; } = SignaturePosition.BottomRight;

    public string? Reason { get; init; }
    public string? Location { get; init; }
    public string? ContactInfo { get; init; }
    /// <summary>Servidor de carimbo de tempo (RFC 3161), opcional. Requer internet.</summary>
    public string? TimestampUrl { get; init; }
}

/// <summary>Resumo de um certificado .pfx para exibir ao usuário.</summary>
public sealed record CertificateSummary(string Name, string Issuer, DateTime NotBefore, DateTime NotAfter, bool HasPrivateKey)
{
    public bool IsValidNow => DateTime.Now >= NotBefore && DateTime.Now <= NotAfter;
}

/// <summary>Assinatura digital PAdES/CMS com certificado .pfx (PDFsharp 6.2.4, MIT + criptografia do .NET).</summary>
public static class PdfSigning
{
    /// <summary>Tamanho do carimbo visível, em pontos (1/72 pol.).</summary>
    public const double StampWidth = 230, StampHeight = 72, StampMargin = 36;

    static PdfSigning()
    {
        // Usa as fontes instaladas no Windows para desenhar o carimbo visível.
        if (OperatingSystem.IsWindows() && GlobalFontSettings.FontResolver is null)
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
    }

    public static X509Certificate2 LoadCertificate(string pfxPath, string? password)
    {
        try
        {
            return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, password, X509KeyStorageFlags.EphemeralKeySet);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidOperationException("Não foi possível abrir o certificado. Verifique a senha do arquivo .pfx.", ex);
        }
    }

    public static CertificateSummary InspectCertificate(string pfxPath, string? password)
    {
        using var cert = LoadCertificate(pfxPath, password);
        return Summarize(cert);
    }

    private static CertificateSummary Summarize(X509Certificate2 cert) => new(
        cert.GetNameInfo(X509NameType.SimpleName, false),
        cert.GetNameInfo(X509NameType.SimpleName, true),
        cert.NotBefore, cert.NotAfter, cert.HasPrivateKey);

    /// <summary>Indica se o PDF já contém assinaturas (que seriam invalidadas por uma nova gravação).</summary>
    public static bool HasSignatures(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields");
        if (fields is null) return false;
        for (int i = 0; i < fields.Elements.Count; i++)
        {
            var field = fields.Elements.GetDictionary(i);
            if (field?.Elements.GetName("/FT") == "/Sig" && field.Elements.ContainsKey("/V")) return true;
        }
        return false;
    }

    public static async Task<CertificateSummary> SignAsync(SignatureRequest request)
    {
        using var cert = LoadCertificate(request.PfxPath, request.PfxPassword);
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException("O certificado não contém a chave privada, necessária para assinar.");
        var summary = Summarize(cert);

        PdfDocument doc;
        try
        {
            doc = PdfReader.Open(request.InputPath, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("O PDF está protegido por senha e não pode ser assinado.", ex);
        }

        using (doc)
        {
            int pageIndex = 0;
            XRect rectangle = default; // retângulo vazio = assinatura invisível
            IAnnotationAppearanceHandler? appearance = null;
            if (request.Visible)
            {
                if (request.PageNumber < 1 || request.PageNumber > doc.PageCount)
                    throw new ArgumentOutOfRangeException(nameof(request), $"A página deve estar entre 1 e {doc.PageCount}.");
                pageIndex = request.PageNumber - 1;
                var page = doc.Pages[pageIndex];
                rectangle = StampRectangle(page.Width.Point, page.Height.Point, request.Position);
                appearance = new StampAppearance(summary.Name, request.Reason, request.Location, DateTime.Now);
            }

            var options = new DigitalSignatureOptions
            {
                Reason = request.Reason ?? "",
                Location = request.Location ?? "",
                ContactInfo = request.ContactInfo ?? "",
                AppName = "PDF Studio",
                PageIndex = pageIndex,
                Rectangle = rectangle,
                AppearanceHandler = appearance!,
            };

            Uri? tsa = string.IsNullOrWhiteSpace(request.TimestampUrl) ? null : new Uri(request.TimestampUrl);
            var signer = new PdfSharpDefaultSigner(cert, PdfMessageDigestType.SHA256, tsa);
            DigitalSignatureHandler.ForDocument(doc, signer, options);

            // Salva em temporário e só depois substitui: permite assinar "por cima" do original.
            string full = Path.GetFullPath(request.OutputPath), temp = full + ".tmp";
            try
            {
                await doc.SaveAsync(temp);
                File.Move(temp, full, overwrite: true);
            }
            finally
            {
                if (File.Exists(temp)) File.Delete(temp);
            }
        }
        return summary;
    }

    /// <summary>
    /// Retângulo do carimbo em coordenadas de PDF (origem no canto INFERIOR esquerdo da página),
    /// que é o sistema usado por DigitalSignatureOptions.Rectangle.
    /// </summary>
    public static XRect StampRectangle(double pageWidth, double pageHeight, SignaturePosition position)
    {
        double left = position is SignaturePosition.BottomLeft or SignaturePosition.TopLeft
            ? StampMargin : pageWidth - StampMargin - StampWidth;
        double bottom = position is SignaturePosition.BottomLeft or SignaturePosition.BottomRight
            ? StampMargin : pageHeight - StampMargin - StampHeight;
        return new XRect(left, bottom, StampWidth, StampHeight);
    }

    /// <summary>Desenha o carimbo visível: borda, nome do signatário, data, motivo e local.</summary>
    private sealed class StampAppearance(string signer, string? reason, string? location, DateTime when) : IAnnotationAppearanceHandler
    {
        public void DrawAppearance(XGraphics gfx, XRect rect)
        {
            // O desenho acontece dentro do próprio carimbo (BBox 0,0,largura,altura):
            // usamos só o tamanho de "rect", não a posição dele na página.
            var accent = XColor.FromArgb(0, 95, 184);
            var titleFont = new XFont("Arial", 9, XFontStyleEx.Bold);
            var bodyFont = new XFont("Arial", 7.5, XFontStyleEx.Regular);

            gfx.DrawRectangle(new XPen(accent, 1), new XSolidBrush(XColor.FromArgb(245, 249, 255)),
                new XRect(0.5, 0.5, rect.Width - 1, rect.Height - 1));

            double x = 8, y = 14;
            gfx.DrawString("Assinado digitalmente por:", bodyFont, new XSolidBrush(accent), x, y);
            y += 12;
            gfx.DrawString(Fit(gfx, signer, titleFont, rect.Width - 16), titleFont, XBrushes.Black, x, y);
            y += 12;
            gfx.DrawString($"Data: {when:dd/MM/yyyy HH:mm:ss zzz}", bodyFont, XBrushes.Black, x, y);
            if (!string.IsNullOrWhiteSpace(reason))
            {
                y += 10;
                gfx.DrawString(Fit(gfx, $"Motivo: {reason}", bodyFont, rect.Width - 16), bodyFont, XBrushes.Black, x, y);
            }
            if (!string.IsNullOrWhiteSpace(location))
            {
                y += 10;
                gfx.DrawString(Fit(gfx, $"Local: {location}", bodyFont, rect.Width - 16), bodyFont, XBrushes.Black, x, y);
            }
        }

        /// <summary>Corta o texto com "…" se não couber na largura.</summary>
        private static string Fit(XGraphics gfx, string text, XFont font, double width)
        {
            if (gfx.MeasureString(text, font).Width <= width) return text;
            while (text.Length > 1 && gfx.MeasureString(text + "…", font).Width > width) text = text[..^1];
            return text + "…";
        }
    }
}
