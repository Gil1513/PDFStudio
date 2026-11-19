using System.Globalization;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PDFStudio.Core;

/// <summary>
/// Resultado do achatamento: comentários e campos que viraram parte da página, e quantos foram tirados sem
/// desenho (não tinham aparência guardada no PDF ou estavam ocultos).
/// </summary>
public sealed record FlattenResult(int Comments, int Fields, int WithoutAppearance);

/// <summary>
/// Etapa 6.3: achatar. Cada comentário e/ou campo de formulário é desenhado no conteúdo da página com a aparência
/// que o próprio PDF guarda dele (/AP) e depois sai da lista de anotações: deixa de poder ser editado ou apagado.
/// Links continuam funcionando e campos de assinatura digital não são tocados.
/// </summary>
public static class PdfFlatten
{
    public static FlattenResult Apply(string input, string output, bool comments = true, bool fields = true)
    {
        if (!comments && !fields) throw new ArgumentException("Escolha o que achatar: comentários, campos ou os dois.");
        int flatComments = 0, flatFields = 0, without = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            foreach (var page in doc.Pages)
            {
                var draw = new StringBuilder();
                var remove = new List<PdfDictionary>();
                for (int i = 0; i < page.Annotations.Count; i++)
                {
                    var annot = page.Annotations[i];
                    string subtype = annot.Elements.GetName("/Subtype");
                    bool widget = subtype == "/Widget";
                    if (subtype == "/Link") continue;
                    if (widget ? !fields || IsSignature(annot) : !comments) continue;
                    remove.Add(annot);
                    if (subtype == "/Popup") continue; // a janelinha do comentário: sai junto, sem desenho
                    int flags = annot.Elements.GetInteger("/F");
                    bool hidden = (flags & 2) != 0 || (flags & 32) != 0; // Hidden, NoView
                    if (!hidden && Appearance(annot) is { } appearance && Placement(annot, appearance) is { } cm)
                    {
                        string name = AddXObject(doc, page, appearance);
                        draw.Append("q ").Append(cm).Append(" cm ").Append(name).Append(" Do Q\n");
                    }
                    else without++;
                    if (widget) flatFields++; else flatComments++;
                }
                if (remove.Count == 0) continue;
                foreach (var annot in remove) RemoveAnnotation(page, annot);
                if (draw.Length > 0)
                {
                    // Isola o conteúdo antigo (q … Q) para as transformações dele não afetarem os desenhos novos.
                    page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
                    page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("Q\n" + draw));
                }
            }
            if (fields) CleanAcroForm(doc);
        });
        return new FlattenResult(flatComments, flatFields, without);
    }

    /// <summary>Quantos comentários (sem contar links e janelinhas) e campos o PDF tem.</summary>
    public static (int Comments, int Fields) Count(string path)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        int c = 0, f = 0;
        foreach (var page in doc.Pages)
            for (int i = 0; i < page.Annotations.Count; i++)
            {
                var annot = page.Annotations[i];
                switch (annot.Elements.GetName("/Subtype"))
                {
                    case "/Link" or "/Popup": break;
                    case "/Widget": if (!IsSignature(annot)) f++; break;
                    default: c++; break;
                }
            }
        return (c, f);
    }

    // ---------------- Aparência ----------------

    /// <summary>A aparência normal (/AP /N); em caixas de seleção, a do estado atual (/AS).</summary>
    private static PdfDictionary? Appearance(PdfDictionary annot)
    {
        if (annot.Elements.GetDictionary("/AP") is not { } ap) return null;
        var normal = ap.Elements.GetDictionary("/N");
        if (normal is null) return null;
        if (normal.Stream is not null) return normal;
        string state = annot.Elements.GetName("/AS");
        return state.Length > 0 ? normal.Elements.GetDictionary(state) is { Stream: not null } s ? s : null : null;
    }

    /// <summary>
    /// Matriz que leva a caixa do desenho (/BBox transformada pela /Matrix) para o retângulo da anotação (/Rect),
    /// como manda a norma do PDF (12.5.5).
    /// </summary>
    private static string? Placement(PdfDictionary annot, PdfDictionary appearance)
    {
        if (annot.Elements.GetArray("/Rect") is not { Elements.Count: 4 } r) return null;
        double rx1 = Math.Min(r.Elements.GetReal(0), r.Elements.GetReal(2)), ry1 = Math.Min(r.Elements.GetReal(1), r.Elements.GetReal(3));
        double rx2 = Math.Max(r.Elements.GetReal(0), r.Elements.GetReal(2)), ry2 = Math.Max(r.Elements.GetReal(1), r.Elements.GetReal(3));
        if (appearance.Elements.GetArray("/BBox") is not { Elements.Count: 4 } b) return null;
        var m = appearance.Elements.GetArray("/Matrix") is { Elements.Count: 6 } a
            ? new PdfMatrix(a.Elements.GetReal(0), a.Elements.GetReal(1), a.Elements.GetReal(2), a.Elements.GetReal(3), a.Elements.GetReal(4), a.Elements.GetReal(5))
            : PdfMatrix.Identity;
        var corners = new[]
        {
            m.Apply(b.Elements.GetReal(0), b.Elements.GetReal(1)), m.Apply(b.Elements.GetReal(2), b.Elements.GetReal(1)),
            m.Apply(b.Elements.GetReal(0), b.Elements.GetReal(3)), m.Apply(b.Elements.GetReal(2), b.Elements.GetReal(3)),
        };
        double bx1 = corners.Min(c => c.X), by1 = corners.Min(c => c.Y), bx2 = corners.Max(c => c.X), by2 = corners.Max(c => c.Y);
        if (bx2 - bx1 < 1e-6 || by2 - by1 < 1e-6) return null;
        double sx = (rx2 - rx1) / (bx2 - bx1), sy = (ry2 - ry1) / (by2 - by1);
        return string.Join(' ', new[] { sx, 0, 0, sy, rx1 - sx * bx1, ry1 - sy * by1 }
            .Select(v => v.ToString("0.#####", CultureInfo.InvariantCulture)));
    }

    /// <summary>Põe o desenho nos recursos da página com um nome novo (/FlatN) e devolve o nome.</summary>
    private static string AddXObject(PdfDocument doc, PdfPage page, PdfDictionary appearance)
    {
        if (!appearance.IsIndirect) doc.Internals.AddObject(appearance);
        appearance.Elements.SetName("/Type", "/XObject");
        appearance.Elements.SetName("/Subtype", "/Form");
        var resources = page.Resources;
        var xobjects = resources.Elements.GetDictionary("/XObject");
        if (xobjects is null)
        {
            xobjects = new PdfDictionary(doc);
            resources.Elements["/XObject"] = xobjects;
        }
        int n = 1;
        while (xobjects.Elements.ContainsKey($"/Flat{n}")) n++;
        string name = $"/Flat{n}";
        xobjects.Elements[name] = appearance.Reference;
        return name;
    }

    // ---------------- Tirar anotações e campos ----------------

    private static void RemoveAnnotation(PdfPage page, PdfDictionary annot)
    {
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null) return;
        for (int i = annots.Elements.Count - 1; i >= 0; i--)
        {
            var item = annots.Elements[i];
            if (ReferenceEquals(item is PdfReference r ? r.Value : item, annot)) annots.Elements.RemoveAt(i);
        }
    }

    private static bool IsSignature(PdfDictionary annot)
    {
        for (var d = annot; d is not null; d = d.Elements.GetDictionary("/Parent"))
            if (d.Elements.GetName("/FT") is { Length: > 0 } ft) return ft == "/Sig";
        return false;
    }

    /// <summary>Tira do formulário os campos achatados (ficam só os de assinatura); sem campos, tira o formulário.</summary>
    private static void CleanAcroForm(PdfDocument doc)
    {
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm") is not { } form) return;
        if (form.Elements.GetArray("/Fields") is { } list)
            for (int i = list.Elements.Count - 1; i >= 0; i--)
                if (list.Elements.GetDictionary(i) is not { } field || !IsSignature(field)) list.Elements.RemoveAt(i);
        form.Elements.Remove("/NeedAppearances");
        if (form.Elements.GetArray("/Fields") is not { Elements.Count: > 0 }) doc.Internals.Catalog.Elements.Remove("/AcroForm");
    }
}
