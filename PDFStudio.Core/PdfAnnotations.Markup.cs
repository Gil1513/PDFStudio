using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

// Etapa 4.4: marcações de texto (realçar, sublinhar, riscar).
public static partial class PdfAnnotations
{
    /// <summary>
    /// Realce, sublinhado ou riscado sobre o texto. <paramref name="lines"/> são os retângulos de cada linha
    /// na tela (pontos, canto superior esquerdo), normalmente de <see cref="PdfTextLayout.LineRects"/>.
    /// <paramref name="text"/> (o texto marcado) fica guardado no comentário.
    /// </summary>
    public static void AddTextMarkup(string input, int pageIndex, AnnotationKind kind,
        IReadOnlyList<(double X, double Y, double Width, double Height)> lines, AnnotationStyle style, string author, string output,
        string? text = null)
    {
        string subtype = kind switch
        {
            AnnotationKind.Highlight => "/Highlight",
            AnnotationKind.Underline => "/Underline",
            AnnotationKind.StrikeOut => "/StrikeOut",
            _ => throw new ArgumentException("Use Highlight, Underline ou StrikeOut.", nameof(kind)),
        };
        var valid = lines.Where(l => l.Width > 0 && l.Height > 0).ToList();
        if (valid.Count == 0) throw new ArgumentException("Selecione um trecho de texto.", nameof(lines));

        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var geometry = PageGeometry.Of(page);
            // Cada linha vira 4 cantos no PDF, na ordem do padrão: topo-esq., topo-dir., base-esq., base-dir. (como se vê na tela).
            var quads = valid.Select(l => new[]
            {
                geometry.ViewToPdf(l.X, l.Y), geometry.ViewToPdf(l.X + l.Width, l.Y),
                geometry.ViewToPdf(l.X, l.Y + l.Height), geometry.ViewToPdf(l.X + l.Width, l.Y + l.Height),
            }).ToList();

            var rect = BoundingBox(quads.SelectMany(q => q), 1);
            // No realce, o padrão dos leitores é a cor "por baixo" do texto (multiplicar), deixando as letras legíveis.
            var annot = NewAnnotation(doc, page, subtype, rect, style with { Opacity = 1 }, author, text);
            var qp = new PdfArray(doc);
            foreach (var q in quads)
                foreach (var p in q) { qp.Elements.Add(new PdfReal(p.X)); qp.Elements.Add(new PdfReal(p.Y)); }
            annot.Elements["/QuadPoints"] = qp;

            var ops = new StringBuilder();
            PdfDictionary? resources = null;
            if (kind == AnnotationKind.Highlight)
            {
                resources = MultiplyResources(doc, style.Opacity);
                ops.Append($"/GSm gs {style.Color.Pdf} rg");
                foreach (var q in quads)
                    ops.Append($" {Local(q[0], rect)} m {Local(q[1], rect)} l {Local(q[3], rect)} l {Local(q[2], rect)} l h");
                ops.Append(" f");
            }
            else
            {
                ops.Append($"{style.Color.Pdf} RG");
                foreach (var q in quads)
                {
                    // "Para cima" da linha: da base para o topo. Sublinhado logo acima da base; riscado no meio.
                    double height = Distance(q[2], q[0]);
                    double t = kind == AnnotationKind.Underline ? 0.13 : 0.45;
                    var from = Lerp(q[2], q[0], t);
                    var to = Lerp(q[3], q[1], t);
                    ops.Append($" {N(Math.Max(0.75, height / 14))} w {Local(from, rect)} m {Local(to, rect)} l S");
                }
            }
            SetAppearance(doc, annot, rect.Width, rect.Height, ops.ToString(), kind == AnnotationKind.Highlight ? 1 : style.Opacity, resources);
        });
    }

    private static PdfDictionary MultiplyResources(PdfDocument doc, double opacity)
    {
        var gs = new PdfDictionary(doc);
        gs.Elements["/Type"] = new PdfName("/ExtGState");
        gs.Elements["/BM"] = new PdfName("/Multiply");
        gs.Elements["/ca"] = new PdfReal(opacity);
        gs.Elements["/CA"] = new PdfReal(opacity);
        var ext = new PdfDictionary(doc);
        ext.Elements["/GSm"] = gs;
        var resources = new PdfDictionary(doc);
        resources.Elements["/ExtGState"] = ext;
        return resources;
    }

    private static XPoint Lerp(XPoint a, XPoint b, double t) => new(a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t);

    private static double Distance(XPoint a, XPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
