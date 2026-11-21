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
    /// <summary>
    /// Etapa 6.4: assinar dentro deste campo vazio (nome). O carimbo fica no lugar e no tamanho do campo;
    /// PageNumber e Position são ignorados.
    /// </summary>
    public string? FieldName { get; init; }

    public string? Reason { get; init; }
    public string? Location { get; init; }
    /// <summary>
    /// Contato. O PDFsharp 6.2.4 não grava /ContactInfo nos dados da assinatura,
    /// então ele só aparece no carimbo visível.
    /// </summary>
    public string? ContactInfo { get; init; }

    // ---- Texto do carimbo visível (não altera o certificado) ----

    /// <summary>Nome mostrado no carimbo. Vazio = nome do certificado.</summary>
    public string? StampSignerName { get; init; }
    /// <summary>Primeira linha do carimbo. Vazio = "Assinado digitalmente por:".</summary>
    public string? StampTitle { get; init; }
    /// <summary>Mostra a data e a hora no carimbo.</summary>
    public bool StampShowDate { get; init; } = true;
    /// <summary>Mostra o motivo e o local no carimbo.</summary>
    public bool StampShowDetails { get; init; } = true;
    /// <summary>Servidor de carimbo de tempo (RFC 3161), opcional. Requer internet.</summary>
    public string? TimestampUrl { get; init; }
}

/// <summary>Resumo de um certificado .pfx para exibir ao usuário.</summary>
public sealed record CertificateSummary(string Name, string Issuer, DateTime NotBefore, DateTime NotAfter, bool HasPrivateKey)
{
    public bool IsValidNow => DateTime.Now >= NotBefore && DateTime.Now <= NotAfter;
}

/// <summary>Assinatura digital PAdES/CMS com certificado .pfx (PDFsharp 6.2.4, MIT + criptografia do .NET).</summary>
public static partial class PdfSigning
{
    /// <summary>Tamanho do carimbo visível, em pontos (1/72 pol.).</summary>
    public const double StampWidth = 230, StampHeight = 72, StampMargin = 36;

    public const string DefaultStampTitle = "Assinado digitalmente por:";

    static PdfSigning()
    {
        // Usa as fontes instaladas no Windows para desenhar o carimbo visível.
        PdfFonts.Ensure();
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
    public static bool HasSignatures(string pdfPath) => DocumentPropertiesService.HasSignatures(pdfPath); // inclui campos dentro de grupos (/Kids)

    public static async Task<CertificateSummary> SignAsync(SignatureRequest request)
    {
        using var cert = LoadCertificate(request.PfxPath, request.PfxPassword);
        if (!cert.HasPrivateKey)
            throw new InvalidOperationException("O certificado não contém a chave privada, necessária para assinar.");
        var summary = Summarize(cert);

        // PDF que já tem assinatura: a nova vai no fim do arquivo e as anteriores continuam valendo.
        if (await TrySignIncrementallyAsync(request, cert, summary)) return summary;

        PdfDocument doc;
        try
        {
            doc = PdfLoad.Open(request.InputPath, PdfDocumentOpenMode.Modify);
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
                if (request.FieldName is null && (request.PageNumber < 1 || request.PageNumber > doc.PageCount))
                    throw new ArgumentOutOfRangeException(nameof(request), $"A página deve estar entre 1 e {doc.PageCount}.");
                if (!string.IsNullOrWhiteSpace(request.FieldName))
                {
                    // O campo vazio sai e a assinatura nova ocupa o mesmo lugar.
                    if (!PdfSignatureFields.RemoveField(doc, request.FieldName.Trim(), out var place))
                        throw new InvalidOperationException($"O campo de assinatura “{request.FieldName}” não existe mais neste PDF.");
                    pageIndex = place.Page;
                    rectangle = new XRect(place.X, place.Y, place.Width, place.Height);
                }
                else
                {
                    pageIndex = request.PageNumber - 1;
                    var page = doc.Pages[pageIndex];
                    rectangle = StampRectangle(page.Width.Point, page.Height.Point, request.Position);
                }
                appearance = new StampAppearance(
                    string.IsNullOrWhiteSpace(request.StampTitle) ? DefaultStampTitle : request.StampTitle.Trim(),
                    string.IsNullOrWhiteSpace(request.StampSignerName) ? summary.Name : request.StampSignerName.Trim(),
                    request.StampShowDetails ? request.Reason : null,
                    request.StampShowDetails ? request.Location : null,
                    request.StampShowDetails ? request.ContactInfo : null,
                    request.StampShowDate ? DateTime.Now : null);
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

    /// <summary>Desenha o carimbo visível: borda, nome do signatário, data, razão e localização.</summary>
    private sealed class StampAppearance(string title, string signer, string? reason, string? location, string? contact, DateTime? when) : IAnnotationAppearanceHandler
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
            gfx.DrawString(Fit(gfx, title, bodyFont, rect.Width - 16), bodyFont, new XSolidBrush(accent), x, y);
            y += 12;
            gfx.DrawString(Fit(gfx, signer, titleFont, rect.Width - 16), titleFont, XBrushes.Black, x, y);
            if (when is { } date)
            {
                y += 12;
                gfx.DrawString($"Data: {date:dd/MM/yyyy HH:mm:ss zzz}", bodyFont, XBrushes.Black, x, y);
            }
            if (!string.IsNullOrWhiteSpace(reason))
            {
                y += 10;
                gfx.DrawString(Fit(gfx, $"Razão: {reason}", bodyFont, rect.Width - 16), bodyFont, XBrushes.Black, x, y);
            }
            if (!string.IsNullOrWhiteSpace(location))
            {
                y += 10;
                gfx.DrawString(Fit(gfx, $"Localização: {location}", bodyFont, rect.Width - 16), bodyFont, XBrushes.Black, x, y);
            }
            if (!string.IsNullOrWhiteSpace(contact))
            {
                y += 10;
                gfx.DrawString(Fit(gfx, $"Contato: {contact}", bodyFont, rect.Width - 16), bodyFont, XBrushes.Black, x, y);
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
