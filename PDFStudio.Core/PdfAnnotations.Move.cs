using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

// Mover e redimensionar comentários já feitos (pedido que ficou da Etapa 4).
public static partial class PdfAnnotations
{
    /// <summary>Menor lado (em pontos) que um comentário pode ter depois de redimensionado.</summary>
    public const double MinCommentSide = 6;

    /// <summary>Realce, sublinhado e risco acompanham o texto da página: não se movem.</summary>
    private static readonly HashSet<string> FollowsText = ["/Highlight", "/Underline", "/StrikeOut", "/Squiggly"];

    /// <summary>O comentário pode ser arrastado para outro lugar?</summary>
    public static bool CanMove(AnnotationKind kind) =>
        kind is AnnotationKind.Rectangle or AnnotationKind.Oval or AnnotationKind.Line or AnnotationKind.Ink
            or AnnotationKind.TextBox or AnnotationKind.Note or AnnotationKind.Stamp;

    /// <summary>O comentário pode mudar de tamanho? (A nota é um ícone de tamanho fixo.)</summary>
    public static bool CanResize(AnnotationKind kind) => CanMove(kind) && kind != AnnotationKind.Note;

    /// <summary>
    /// Leva o comentário <paramref name="index"/> para a caixa (x, y, largura, altura) da tela, em pontos. Tudo o que
    /// está dentro dele (pontos da linha, traços do desenho) acompanha. Formas, linhas, desenhos e caixas de texto são
    /// redesenhados no tamanho novo (a espessura do traço e o tamanho da letra não mudam); o carimbo estica junto.
    /// A caixa é mantida dentro da página. Na caixa de texto, se o texto não couber, ela cresce para baixo.
    /// </summary>
    public static void MoveResize(string input, int pageIndex, int index, double x, double y, double width, double height, string output) =>
        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var a = AnnotationAt(page, index);
            string subtype = a.Elements.GetName("/Subtype");
            if (FollowsText.Contains(subtype))
                throw new InvalidOperationException("Realces, sublinhados e riscos acompanham o texto da página e não podem ser movidos.");
            if (!CanMove(KindOf(subtype, a)))
                throw new InvalidOperationException("Este tipo de comentário não pode ser movido pelo PDF Studio.");
            var r = a.Elements.GetArray("/Rect") ?? throw new InvalidOperationException("O comentário não tem posição.");

            var geometry = PageGeometry.Of(page);
            var (ox, oy, ow, oh) = geometry.PdfRectToView(r.Elements.GetReal(0), r.Elements.GetReal(1), r.Elements.GetReal(2), r.Elements.GetReal(3));
            if (subtype == "/Text") (width, height) = (ow, oh); // nota: só muda de lugar
            width = Math.Clamp(width, MinCommentSide, geometry.ViewWidth);
            height = Math.Clamp(height, MinCommentSide, geometry.ViewHeight);
            x = Math.Clamp(x, 0, geometry.ViewWidth - width);
            y = Math.Clamp(y, 0, geometry.ViewHeight - height);
            double sx = ow > 0 ? width / ow : 1, sy = oh > 0 ? height / oh : 1;
            bool resized = Math.Abs(width - ow) > 0.01 || Math.Abs(height - oh) > 0.01;

            // Um ponto do PDF dentro do comentário → o mesmo ponto na caixa nova.
            XPoint Map(XPoint p)
            {
                var v = geometry.PdfToView(p.X, p.Y);
                return geometry.ViewToPdf(x + (v.X - ox) * sx, y + (v.Y - oy) * sy);
            }

            var style = StyleOf(a);
            a.Elements["/Rect"] = geometry.ViewRectToPdf(x, y, width, height);
            switch (subtype)
            {
                case "/Line" when a.Elements.GetArray("/L") is { Elements.Count: 4 } l:
                {
                    var p1 = Map(new XPoint(l.Elements.GetReal(0), l.Elements.GetReal(1)));
                    var p2 = Map(new XPoint(l.Elements.GetReal(2), l.Elements.GetReal(3)));
                    bool arrow = a.Elements.GetArray("/LE") is { Elements.Count: 2 } le && le.Elements.GetName(1) is { Length: > 0 } end && end != "/None";
                    DrawLine(doc, a, p1, p2, arrow, style);
                    break;
                }
                case "/Ink" when a.Elements.GetArray("/InkList") is { } ink:
                {
                    var strokes = new List<IReadOnlyList<XPoint>>();
                    for (int i = 0; i < ink.Elements.Count; i++)
                    {
                        if (ink.Elements.GetArray(i) is not { } stroke) continue;
                        var points = new List<XPoint>();
                        for (int k = 0; k + 1 < stroke.Elements.Count; k += 2)
                            points.Add(Map(new XPoint(stroke.Elements.GetReal(k), stroke.Elements.GetReal(k + 1))));
                        if (points.Count >= 2) strokes.Add(points);
                    }
                    if (strokes.Count > 0) DrawInk(doc, a, strokes, style);
                    break;
                }
                case "/Square" or "/Circle" when resized:
                {
                    var box = a.Elements.GetRectangle("/Rect");
                    DrawShape(doc, a, subtype == "/Square", box.Width, box.Height, style);
                    break;
                }
                case "/FreeText" when resized:
                {
                    string text = a.Elements.GetString("/Contents");
                    string da = a.Elements.GetString("/DA");
                    double fontSize = FontSizeFromDA(da) ?? 12;
                    var textStyle = style with { Color = ColorFromDA(da) ?? new AnnotationColor(0, 0, 0) };
                    double needed = TextHeight(WrapText(text, fontSize, width - 2 * TextPadding).Count, fontSize);
                    if (needed > height)
                    {
                        height = Math.Min(needed, geometry.ViewHeight);
                        y = Math.Clamp(y, 0, geometry.ViewHeight - height);
                        a.Elements["/Rect"] = geometry.ViewRectToPdf(x, y, width, height);
                    }
                    WriteTextBoxAppearance(doc, a, geometry.Rotation, width, height, text, fontSize, textStyle);
                    break;
                }
                // Carimbo, nota, ou forma só movida: a aparência (/AP) é desenhada dentro do /Rect novo.
            }

            // A janela do pop-up (se houver) vai junto, sem mudar de tamanho.
            if (a.Elements.GetDictionary("/Popup")?.Elements.GetArray("/Rect") is { Elements.Count: 4 } pr)
            {
                var (px, py, pw, ph) = geometry.PdfRectToView(pr.Elements.GetReal(0), pr.Elements.GetReal(1), pr.Elements.GetReal(2), pr.Elements.GetReal(3));
                a.Elements.GetDictionary("/Popup")!.Elements["/Rect"] = geometry.ViewRectToPdf(px + x - ox, py + y - oy, pw, ph);
            }
            Touch(a);
        });

    /// <summary>Cor, espessura e transparência gravadas no comentário (para redesenhá-lo igual).</summary>
    private static AnnotationStyle StyleOf(PdfDictionary a)
    {
        var color = ReadColor(a.Elements.GetArray("/C")) ?? new AnnotationColor(0, 0, 0);
        double width = a.Elements.GetDictionary("/BS") is { } bs && bs.Elements.ContainsKey("/W") ? bs.Elements.GetReal("/W")
            : a.Elements.GetArray("/Border") is { Elements.Count: >= 3 } border ? border.Elements.GetReal(2)
            : 1;
        double opacity = a.Elements.ContainsKey("/CA") ? Math.Clamp(a.Elements.GetReal("/CA"), 0, 1) : 1;
        return new AnnotationStyle(color, width, opacity);
    }
}
