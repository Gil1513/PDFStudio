using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

// Assinar um PDF que JÁ tem assinatura sem invalidar as anteriores: a assinatura nova (campo, aparência e
// dados) é acrescentada no fim do arquivo e cobre tudo o que veio antes. O PDFsharp regravaria o arquivo
// inteiro, então esta parte é feita com o gravador próprio (PdfIncrementalUpdate).
public static partial class PdfSigning
{
    /// <summary>Espaço reservado para a assinatura (CMS) dentro do arquivo, em bytes.</summary>
    private const int ContentsSize = 32 * 1024;
    private const string ByteRangePlaceholder = "[0 0000000000 0000000000 0000000000]";

    /// <summary>false = o PDF não tem assinatura (ou não dá para gravar no fim); aí assina do jeito comum.</summary>
    private static async Task<bool> TrySignIncrementallyAsync(SignatureRequest request, X509Certificate2 cert, CertificateSummary summary)
    {
        byte[] original = File.ReadAllBytes(request.InputPath);
        byte[] prepared;
        PdfDocument doc;
        try { doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Import); }
        catch (PdfReaderException) { return false; }
        using (doc)
        {
            if (!DocumentPropertiesService.HasSignatures(doc)) return false;
            Dictionary<int, byte[]> before;
            try { before = PdfIncrementalUpdate.Snapshot(doc); }
            catch (NotSupportedException) { return false; }

            var now = DateTime.Now;
            var sig = new PdfDictionary(doc);
            sig.Elements.SetName("/Type", "/Sig");
            sig.Elements.SetName("/Filter", "/Adobe.PPKLite");
            sig.Elements.SetName("/SubFilter", "/adbe.pkcs7.detached");
            sig.Elements["/Name"] = new PdfString(summary.Name, PdfStringEncoding.Unicode);
            sig.Elements["/M"] = new PdfString(PdfDateText(now));
            if (!string.IsNullOrWhiteSpace(request.Reason)) sig.Elements["/Reason"] = new PdfString(request.Reason, PdfStringEncoding.Unicode);
            if (!string.IsNullOrWhiteSpace(request.Location)) sig.Elements["/Location"] = new PdfString(request.Location, PdfStringEncoding.Unicode);
            if (!string.IsNullOrWhiteSpace(request.ContactInfo)) sig.Elements["/ContactInfo"] = new PdfString(request.ContactInfo, PdfStringEncoding.Unicode);
            sig.Elements["/ByteRange"] = new PdfLiteral(ByteRangePlaceholder);
            sig.Elements["/Contents"] = new PdfLiteral("<" + new string('0', ContentsSize * 2) + ">");
            doc.Internals.AddObject(sig);

            if (!string.IsNullOrWhiteSpace(request.FieldName))
            {
                // Assina dentro do campo vazio que já existe (o campo ganha o /V e a aparência nova).
                var found = PdfSignatureFields.FindEmptyField(doc, request.FieldName.Trim())
                    ?? throw new InvalidOperationException($"O campo de assinatura “{request.FieldName}” não existe mais neste PDF.");
                found.Field.Elements["/V"] = sig.Reference;
                var r = found.Widget.Elements.GetArray("/Rect")!;
                double w = Math.Abs(r.Elements.GetReal(2) - r.Elements.GetReal(0)), h = Math.Abs(r.Elements.GetReal(3) - r.Elements.GetReal(1));
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = StampForm(doc, request, summary, now, w, h).Reference;
                found.Widget.Elements["/AP"] = ap;
            }
            else
            {
                if (request.Visible && (request.PageNumber < 1 || request.PageNumber > doc.PageCount))
                    throw new ArgumentOutOfRangeException(nameof(request), $"A página deve estar entre 1 e {doc.PageCount}.");
                int pageIndex = request.Visible ? request.PageNumber - 1 : 0;
                var page = doc.Pages[pageIndex];
                var rect = request.Visible ? StampRectangle(page.Width.Point, page.Height.Point, request.Position) : new XRect(0, 0, 0, 0);

                var names = PdfSignatureFields.FieldNames(doc);
                int n = 1;
                while (names.Contains($"Assinatura{n}")) n++;

                var widget = new PdfDictionary(doc);
                widget.Elements.SetName("/Type", "/Annot");
                widget.Elements.SetName("/Subtype", "/Widget");
                widget.Elements.SetName("/FT", "/Sig");
                widget.Elements["/T"] = new PdfString($"Assinatura{n}", PdfStringEncoding.Unicode);
                widget.Elements.SetInteger("/F", 4); // imprimir
                widget.Elements["/Rect"] = new PdfArray(doc, new PdfReal(rect.X), new PdfReal(rect.Y), new PdfReal(rect.Right), new PdfReal(rect.Bottom));
                widget.Elements["/P"] = page.Reference;
                widget.Elements["/V"] = sig.Reference;
                var ap = new PdfDictionary(doc);
                ap.Elements["/N"] = (request.Visible ? StampForm(doc, request, summary, now, rect.Width, rect.Height) : EmptyForm(doc)).Reference;
                widget.Elements["/AP"] = ap;
                doc.Internals.AddObject(widget);

                var annots = page.Elements.GetArray("/Annots");
                if (annots is null)
                {
                    annots = new PdfArray(doc);
                    page.Elements["/Annots"] = annots;
                }
                annots.Elements.Add(widget.Reference!);

                var form = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
                if (form is null)
                {
                    form = new PdfDictionary(doc);
                    doc.Internals.Catalog.Elements["/AcroForm"] = form;
                }
                var fields = form.Elements.GetArray("/Fields");
                if (fields is null)
                {
                    fields = new PdfArray(doc);
                    form.Elements["/Fields"] = fields;
                }
                fields.Elements.Add(widget.Reference!);
            }
            var acro = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (acro is not null && acro.Elements.GetInteger("/SigFlags") != 3) acro.Elements.SetInteger("/SigFlags", 3);

            try
            {
                prepared = PdfIncrementalUpdate.Build(original, doc, before, patchedLater: new HashSet<int> { sig.Reference!.ObjectNumber });
            }
            catch (NotSupportedException) { return false; }
        }

        // Acha os espaços reservados (só na parte nova) e preenche: primeiro o /ByteRange, depois a assinatura.
        string tail = Encoding.Latin1.GetString(prepared, original.Length, prepared.Length - original.Length);
        int rangeAt = tail.IndexOf("/ByteRange " + ByteRangePlaceholder, StringComparison.Ordinal);
        int contentsAt = tail.IndexOf("/Contents <", StringComparison.Ordinal);
        if (rangeAt < 0 || contentsAt < 0) throw new InvalidOperationException("Não foi possível preparar a assinatura.");
        long b = original.Length + contentsAt + "/Contents ".Length;      // começo de "<"
        long c = b + ContentsSize * 2 + 2;                                // logo depois de ">"
        long d = prepared.Length - c;
        string range = string.Create(CultureInfo.InvariantCulture, $"[0 {b:D10} {c:D10} {d:D10}]");
        Encoding.ASCII.GetBytes(range).CopyTo(prepared, original.Length + rangeAt + "/ByteRange ".Length);

        byte[] signedData = [.. prepared.AsSpan(0, (int)b), .. prepared.AsSpan((int)c, (int)d)];
        byte[] cms = await CreateCmsAsync(signedData, cert, request.TimestampUrl);
        if (cms.Length > ContentsSize) throw new InvalidOperationException("A assinatura ficou grande demais para o espaço reservado.");
        Encoding.ASCII.GetBytes(Convert.ToHexString(cms)).CopyTo(prepared, b + 1);

        string full = Path.GetFullPath(request.OutputPath), temp = full + ".tmp";
        try
        {
            await File.WriteAllBytesAsync(temp, prepared);
            File.Move(temp, full, overwrite: true);
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
        return true;
    }

    /// <summary>Assinatura CMS "destacada" (SHA-256) dos bytes, com carimbo de tempo opcional (RFC 3161).</summary>
    private static async Task<byte[]> CreateCmsAsync(byte[] data, X509Certificate2 cert, string? timestampUrl)
    {
        var cms = new SignedCms(new ContentInfo(data), detached: true);
        var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, cert)
        {
            DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1"), // SHA-256
            IncludeOption = X509IncludeOption.WholeChain,
        };
        signer.SignedAttributes.Add(new Pkcs9SigningTime(DateTime.Now));
        cms.ComputeSignature(signer, silent: true);

        if (!string.IsNullOrWhiteSpace(timestampUrl))
        {
            var info = cms.SignerInfos[0];
            var tsRequest = Rfc3161TimestampRequest.CreateFromSignerInfo(info, HashAlgorithmName.SHA256,
                requestSignerCertificates: true, nonce: RandomNumberGenerator.GetBytes(8));
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var body = new ByteArrayContent(tsRequest.Encode());
            body.Headers.ContentType = new MediaTypeHeaderValue("application/timestamp-query");
            using var response = await http.PostAsync(new Uri(timestampUrl), body);
            response.EnsureSuccessStatusCode();
            var token = tsRequest.ProcessResponse(await response.Content.ReadAsByteArrayAsync(), out _);
            info.AddUnsignedAttribute(new AsnEncodedData(new Oid("1.2.840.113549.1.9.16.2.14"), token.AsSignedCms().Encode()));
        }
        return cms.Encode();
    }

    /// <summary>Desenho do carimbo visível (mesmo conteúdo do carimbo comum), com as fontes padrão do PDF.</summary>
    private static PdfDictionary StampForm(PdfDocument doc, SignatureRequest request, CertificateSummary summary, DateTime when,
        double width, double height)
    {
        string title = string.IsNullOrWhiteSpace(request.StampTitle) ? DefaultStampTitle : request.StampTitle.Trim();
        string name = string.IsNullOrWhiteSpace(request.StampSignerName) ? summary.Name : request.StampSignerName.Trim();
        var lines = new List<(string Text, bool Bold, double Size, double Gap)>
        {
            (title, false, 7.5, 14),
            (name, true, 9, 12),
        };
        if (request.StampShowDate) lines.Add(($"Data: {when:dd/MM/yyyy HH:mm:ss zzz}", false, 7.5, 12));
        if (request.StampShowDetails)
        {
            if (!string.IsNullOrWhiteSpace(request.Reason)) lines.Add(($"Razão: {request.Reason}", false, 7.5, 10));
            if (!string.IsNullOrWhiteSpace(request.Location)) lines.Add(($"Localização: {request.Location}", false, 7.5, 10));
            if (!string.IsNullOrWhiteSpace(request.ContactInfo)) lines.Add(($"Contato: {request.ContactInfo}", false, 7.5, 10));
        }

        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var gfx = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        var content = new StringBuilder()
            .Append("q 0.961 0.976 1 rg 0 0.373 0.722 RG 1 w 0.5 0.5 ").Append(F(width - 1)).Append(' ').Append(F(height - 1)).Append(" re B\n");
        double y = 0;
        bool first = true;
        foreach (var (text, bold, size, gap) in lines)
        {
            y += gap;
            if (y > height - 2) break;
            var font = new XFont("Arial", size, bold ? XFontStyleEx.Bold : XFontStyleEx.Regular);
            string fitted = text;
            if (gfx.MeasureString(fitted, font).Width > width - 16)
            {
                while (fitted.Length > 1 && gfx.MeasureString(fitted + "...", font).Width > width - 16) fitted = fitted[..^1];
                fitted += "...";
            }
            content.Append(first ? "0 0.373 0.722 rg" : "0 0 0 rg")
                .Append(" BT /").Append(bold ? "HeBo" : "Helv").Append(' ').Append(F(size)).Append(" Tf 8 ")
                .Append(F(height - y)).Append(" Td (").Append(Escape(fitted)).Append(") Tj ET\n");
            first = false;
        }
        content.Append("Q\n");

        var fonts = new PdfDictionary(doc);
        fonts.Elements["/Helv"] = StandardFont(doc, "/Helvetica");
        fonts.Elements["/HeBo"] = StandardFont(doc, "/Helvetica-Bold");
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;
        var form = NewForm(doc, width, height);
        form.Elements["/Resources"] = resources;
        form.CreateStream(Encoding.Latin1.GetBytes(content.ToString()));
        return form;
    }

    private static PdfDictionary EmptyForm(PdfDocument doc)
    {
        var form = NewForm(doc, 0, 0);
        form.CreateStream([]);
        return form;
    }

    private static PdfDictionary NewForm(PdfDocument doc, double width, double height)
    {
        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(width), new PdfReal(height));
        doc.Internals.AddObject(form);
        return form;
    }

    /// <summary>Fonte padrão do PDF (só o nome: o leitor usa a dele; nenhum arquivo de fonte vai junto).</summary>
    private static PdfDictionary StandardFont(PdfDocument doc, string baseFont)
    {
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", baseFont);
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        return font;
    }

    /// <summary>Texto para (…) do PDF em WinAnsi: acentos do português passam; o resto vira "?".</summary>
    private static string Escape(string text)
    {
        var sb = new StringBuilder();
        foreach (char ch in text)
        {
            char c = ch is >= ' ' and <= 'ÿ' and not (>= '\u007f' and <= '\u009f') ? ch : '?';
            if (c is '(' or ')' or '\\') sb.Append('\\');
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static string PdfDateText(DateTime value)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(value);
        string sign = offset < TimeSpan.Zero ? "-" : "+";
        offset = offset.Duration();
        return "D:" + value.ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture) + $"{sign}{offset.Hours:D2}'{offset.Minutes:D2}'";
    }
}
