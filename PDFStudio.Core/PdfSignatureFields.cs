using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Campo de assinatura: nome, página e retângulo na tela (pontos, canto superior esquerdo); se já foi assinado.</summary>
public sealed record SignatureField(string Name, int Page, double X, double Y, double Width, double Height, bool Signed);

/// <summary>
/// Etapa 6.4: campos de assinatura vazios ("Assinar aqui"), para alguém assinar depois — no PDF Studio
/// (Assinar → "Onde assinar") ou em outro leitor de PDF.
/// </summary>
public static class PdfSignatureFields
{
    public const double MinWidth = 60, MinHeight = 24;

    /// <summary>Todos os campos de assinatura do PDF, na ordem das páginas.</summary>
    public static List<SignatureField> List(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var list = new List<SignatureField>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            var geometry = PageGeometry.Of(page);
            for (int i = 0; i < page.Annotations.Count; i++)
            {
                var annot = page.Annotations[i];
                if (annot.Elements.GetName("/Subtype") != "/Widget" || FieldOf(annot) is not { } field) continue;
                if (annot.Elements.GetArray("/Rect") is not { Elements.Count: 4 } r) continue;
                var a = geometry.PdfToView(r.Elements.GetReal(0), r.Elements.GetReal(1));
                var b = geometry.PdfToView(r.Elements.GetReal(2), r.Elements.GetReal(3));
                list.Add(new SignatureField(NameOf(field), p,
                    Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y),
                    field.Elements.ContainsKey("/V")));
            }
        }
        return list;
    }

    /// <summary>Um nome livre ("Assinatura1", "Assinatura2"…).</summary>
    public static string SuggestName(string path)
    {
        var used = List(path).Select(f => f.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        int n = 1;
        while (used.Contains($"Assinatura{n}")) n++;
        return $"Assinatura{n}";
    }

    /// <summary>Cria um campo vazio no retângulo da tela (x, y, largura, altura em pontos) da página.</summary>
    public static void Add(string input, int pageIndex, double x, double y, double width, double height, string name, string output)
    {
        name = name.Trim();
        if (name.Length == 0) throw new ArgumentException("Dê um nome ao campo.", nameof(name));
        if (name.Contains('.')) throw new ArgumentException("O nome do campo não pode ter ponto.", nameof(name));
        if (width < MinWidth || height < MinHeight)
            throw new ArgumentException($"O campo precisa ter pelo menos {MinWidth:0} × {MinHeight:0} pontos.");
        if (List(input).Any(f => f.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException($"Já existe um campo chamado “{name}”.", nameof(name));

        PdfAnnotations.Edit(input, output, doc =>
        {
            var page = doc.Pages[PdfPageContent.CheckPage(doc, pageIndex)];
            var geometry = PageGeometry.Of(page);
            var a = geometry.ViewToPdf(x, y);
            var b = geometry.ViewToPdf(x + width, y + height);

            var widget = new PdfDictionary(doc);
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements.SetName("/FT", "/Sig");
            widget.Elements["/T"] = new PdfString(name, PdfStringEncoding.Unicode);
            widget.Elements.SetInteger("/F", 4); // imprimir
            widget.Elements["/Rect"] = new PdfArray(doc,
                new PdfReal(Math.Min(a.X, b.X)), new PdfReal(Math.Min(a.Y, b.Y)), new PdfReal(Math.Max(a.X, b.X)), new PdfReal(Math.Max(a.Y, b.Y)));
            widget.Elements["/P"] = page.Reference;
            var appearance = new PdfDictionary(doc);
            appearance.Elements["/N"] = Appearance(doc, width, height, page.Rotate).Reference;
            widget.Elements["/AP"] = appearance;
            doc.Internals.AddObject(widget);

            var annots = page.Elements.GetArray("/Annots");
            if (annots is null)
            {
                annots = new PdfArray(doc);
                page.Elements["/Annots"] = annots;
            }
            annots.Elements.Add(widget.Reference);

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
            fields.Elements.Add(widget.Reference);
        });
    }

    /// <summary>Exclui um campo ainda não assinado.</summary>
    public static void Remove(string input, string name, string output)
    {
        PdfAnnotations.Edit(input, output, doc =>
        {
            if (!RemoveField(doc, name, out _))
                throw new ArgumentException($"Não há campo de assinatura vazio chamado “{name}”.", nameof(name));
        });
    }

    /// <summary>
    /// Tira o campo vazio <paramref name="name"/> e devolve onde ele estava (página e retângulo do PDF, origem
    /// embaixo à esquerda) — usado para assinar no lugar dele.
    /// </summary>
    internal static bool RemoveField(PdfDocument doc, string name, out (int Page, double X, double Y, double Width, double Height) place)
    {
        place = default;
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                if (annots.Elements.GetDictionary(i) is not { } annot || annot.Elements.GetName("/Subtype") != "/Widget") continue;
                if (FieldOf(annot) is not { } field || !NameOf(field).Equals(name, StringComparison.OrdinalIgnoreCase)) continue;
                if (field.Elements.ContainsKey("/V")) throw new InvalidOperationException($"O campo “{name}” já está assinado.");
                var r = annot.Elements.GetArray("/Rect")!;
                double x1 = Math.Min(r.Elements.GetReal(0), r.Elements.GetReal(2)), y1 = Math.Min(r.Elements.GetReal(1), r.Elements.GetReal(3));
                place = (p, x1, y1, Math.Abs(r.Elements.GetReal(2) - r.Elements.GetReal(0)), Math.Abs(r.Elements.GetReal(3) - r.Elements.GetReal(1)));
                annots.Elements.RemoveAt(i);
                if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is { } fields)
                    for (int k = fields.Elements.Count - 1; k >= 0; k--)
                        if (ReferenceEquals(fields.Elements.GetDictionary(k), field)) fields.Elements.RemoveAt(k);
                return true;
            }
        }
        return false;
    }

    /// <summary>O campo de assinatura da anotação (ela mesma ou um "pai" com /FT /Sig), ou null.</summary>
    private static PdfDictionary? FieldOf(PdfDictionary annot)
    {
        for (var d = annot; d is not null; d = d.Elements.GetDictionary("/Parent"))
            if (d.Elements.GetName("/FT") is { Length: > 0 } ft) return ft == "/Sig" ? d : null;
        return null;
    }

    private static string NameOf(PdfDictionary field) => field.Elements.GetString("/T");

    /// <summary>
    /// Desenho do campo vazio: fundo azul-claro, borda tracejada e "Assinar aqui". Em página girada, a /Matrix
    /// gira o desenho para ele aparecer "de pé" na tela.
    /// </summary>
    private static PdfDictionary Appearance(PdfDocument doc, double width, double height, int rotation)
    {
        static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);
        var font = new PdfDictionary(doc);
        font.Elements.SetName("/Type", "/Font");
        font.Elements.SetName("/Subtype", "/Type1");
        font.Elements.SetName("/BaseFont", "/Helvetica");
        font.Elements.SetName("/Encoding", "/WinAnsiEncoding");
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/Helv"] = font;
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;

        double size = Math.Clamp(height * 0.32, 7, 14);
        var content = new StringBuilder()
            .Append("q 0.94 0.96 1 rg 0 0 ").Append(F(width)).Append(' ').Append(F(height)).Append(" re f\n")
            .Append("0 0.37 0.72 RG 1 w [4 2] 0 d 0.5 0.5 ").Append(F(width - 1)).Append(' ').Append(F(height - 1)).Append(" re S\n")
            .Append("BT /Helv ").Append(F(size)).Append(" Tf 0 0.37 0.72 rg ")
            .Append(F(6)).Append(' ').Append(F((height - size) / 2 + size * 0.2)).Append(" Td (Assinar aqui) Tj ET Q\n");

        var form = new PdfDictionary(doc);
        form.Elements.SetName("/Type", "/XObject");
        form.Elements.SetName("/Subtype", "/Form");
        form.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(width), new PdfReal(height));
        int r = ((rotation % 360) + 360) % 360;
        if (r != 0)
        {
            double rad = r * Math.PI / 180, cos = Math.Round(Math.Cos(rad)), sin = Math.Round(Math.Sin(rad));
            form.Elements["/Matrix"] = new PdfArray(doc, new PdfReal(cos), new PdfReal(sin), new PdfReal(-sin), new PdfReal(cos), new PdfReal(0), new PdfReal(0));
        }
        form.Elements["/Resources"] = resources;
        form.CreateStream(Encoding.ASCII.GetBytes(content.ToString()));
        doc.Internals.AddObject(form);
        return form;
    }
}
