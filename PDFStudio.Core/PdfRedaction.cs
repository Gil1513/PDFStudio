using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.Annotations;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using M = PDFStudio.Core.PdfMatrix;

namespace PDFStudio.Core;

/// <summary>Área a redigir, em pontos da tela (canto superior esquerdo da página visível).</summary>
public sealed record RedactionArea(int Page, double X, double Y, double Width, double Height)
{
    public bool Contains(double x, double y) => x >= X && x <= X + Width && y >= Y && y <= Y + Height;

    public bool Intersects(double x1, double y1, double x2, double y2) =>
        x2 > X && x1 < X + Width && y2 > Y && y1 < Y + Height;

    public bool Covers(double x1, double y1, double x2, double y2) =>
        x1 >= X - 1 && x2 <= X + Width + 1 && y1 >= Y - 1 && y2 <= Y + Height + 1;
}

/// <summary>
/// O que a redação fez. <paramref name="Letters"/>: letras apagadas do conteúdo; <paramref name="Images"/>: imagens
/// apagadas; <paramref name="Annotations"/>: comentários, links e campos apagados; <paramref name="CoveredOnly"/>:
/// coisas que só puderam ser cobertas (texto dentro de formulários do PDF, imagens só em parte na área).
/// </summary>
public sealed record RedactionResult(int Letters, int Images, int Annotations, int CoveredOnly);

/// <summary>
/// Etapa 6.2: redigir (apagar de verdade) o que está em áreas da página e pôr uma tarja por cima.
/// Cada letra tem a posição calculada pelas larguras da fonte; as que caem na área saem do conteúdo e viram
/// espaço vazio (o resto da linha fica no lugar). Imagens inteiras na área saem; comentários, links e campos
/// na área saem. O que não dá para apagar fica coberto pela tarja e é contado em <see cref="RedactionResult.CoveredOnly"/>.
/// </summary>
public static class PdfRedaction
{
    public static RedactionResult Apply(string input, IReadOnlyList<RedactionArea> areas, string output) =>
        Apply(input, areas, output, new AnnotationColor(0, 0, 0));

    public static RedactionResult Apply(string input, IReadOnlyList<RedactionArea> areas, string output, AnnotationColor color)
    {
        var valid = areas.Where(a => a.Width > 0 && a.Height > 0).ToList();
        if (valid.Count == 0) throw new ArgumentException("Marque pelo menos uma área para redigir.", nameof(areas));

        string temp = Path.Combine(Path.GetTempPath(), "PDFStudio", $"redigir-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            int letters = 0, images = 0, annotations = 0, covered = 0;
            PdfAnnotations.Edit(input, temp, doc =>
            {
                foreach (var group in valid.GroupBy(a => a.Page))
                {
                    var page = doc.Pages[PdfPageContent.CheckPage(doc, group.Key)];
                    var list = group.ToList();
                    var counts = new Counts();
                    RedactContent(page, list, counts);
                    letters += counts.Letters;
                    images += counts.Images;
                    covered += counts.Covered;
                    annotations += RemoveAnnotations(page, list);
                }
            });

            // Sobrou texto nas áreas (ex.: dentro de um formulário do PDF)? Fica coberto pela tarja.
            foreach (var group in valid.GroupBy(a => a.Page))
                covered += PdfTextLayout.Words(temp, group.Key).Count(w => group.Any(a => a.Contains(w.X + w.Width / 2, w.Y + w.Height / 2)));

            var brush = new XSolidBrush(XColor.FromArgb(color.R, color.G, color.B));
            PdfAnnotations.Edit(temp, output, doc =>
            {
                foreach (var group in valid.GroupBy(a => a.Page))
                    PdfPageContent.DrawInView(doc.Pages[group.Key], gfx =>
                    {
                        foreach (var a in group) gfx.DrawRectangle(brush, a.X, a.Y, a.Width, a.Height);
                    });
            });
            return new RedactionResult(letters, images, annotations, covered);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temporário */ }
        }
    }

    // ---------------- Comentários, links e campos ----------------

    private static int RemoveAnnotations(PdfPage page, List<RedactionArea> areas)
    {
        var geometry = PageGeometry.Of(page);
        var doomed = new List<PdfAnnotation>();
        for (int i = 0; i < page.Annotations.Count; i++)
        {
            var annot = page.Annotations[i];
            if (annot.Elements.GetArray("/Rect") is not { Elements.Count: 4 } rect) continue;
            var (x1, y1, x2, y2) = ViewBox(geometry, [(rect.Elements.GetReal(0), rect.Elements.GetReal(1)), (rect.Elements.GetReal(2), rect.Elements.GetReal(3))]);
            if (!areas.Any(a => a.Intersects(x1, y1, x2, y2))) continue;
            doomed.Add(annot);
        }
        foreach (var annot in doomed)
        {
            annot.Elements.Remove("/V");  // valor de campo de formulário (quando o campo e o widget são o mesmo objeto)
            annot.Elements.Remove("/Contents");
            page.Annotations.Remove(annot);
        }
        return doomed.Count;
    }

    // ---------------- Conteúdo da página ----------------

    private sealed class Counts
    {
        public int Letters, Images, Covered;
    }

    /// <summary>Estado gráfico que importa aqui (salvo por q e restaurado por Q).</summary>
    private sealed record State(M Ctm, string Font, double Size, double CharSpacing, double WordSpacing, double Scale, double Leading, double Rise);

    private static void RedactContent(PdfPage page, List<RedactionArea> areas, Counts counts)
    {
        if (page.Elements["/Contents"] is null) return;
        var geometry = PageGeometry.Of(page);
        var fonts = new Dictionary<string, FontWidths>();
        var contents = page.Contents.Elements;
        var state = new State(M.Identity, "", 0, 0, 0, 1, 0, 0);
        var stack = new Stack<State>();
        M tm = M.Identity, tlm = M.Identity;

        void MoveLine(double tx, double ty)
        {
            tlm = M.Translate(tx, ty).Then(tlm);
            tm = tlm;
        }
        bool InArea(double tx, double ty)
        {
            var (x, y) = tm.Then(state.Ctm).Apply(tx, ty);
            var v = geometry.PdfToView(x, y);
            return areas.Any(a => a.Contains(v.X, v.Y));
        }

        for (int s = 0; s < contents.Count; s++)
        {
            if (contents.GetDictionary(s) is not { Stream: not null } stream) continue;
            CSequence seq;
            try { seq = ContentReader.ReadContent(stream.Stream.UnfilteredValue); }
            catch (ContentReaderException) { continue; }
            var changes = new SortedDictionary<int, string>(); // índice → conteúdo novo ("" = apagar)

            for (int i = 0; i < seq.Count; i++)
            {
                if (seq[i] is not COperator op) continue;
                var args = op.Operands;
                switch (op.OpCode.OpCodeName)
                {
                    case OpCodeName.q: stack.Push(state); break;
                    case OpCodeName.Q: if (stack.Count > 0) state = stack.Pop(); break;
                    case OpCodeName.cm when args.Count == 6:
                        state = state with { Ctm = new M(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5])).Then(state.Ctm) };
                        break;
                    case OpCodeName.BT: tm = tlm = M.Identity; break;
                    case OpCodeName.Tm when args.Count == 6:
                        tm = tlm = new M(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5]));
                        break;
                    case OpCodeName.Td when args.Count == 2: MoveLine(N(args[0]), N(args[1])); break;
                    case OpCodeName.TD when args.Count == 2: state = state with { Leading = -N(args[1]) }; MoveLine(N(args[0]), N(args[1])); break;
                    case OpCodeName.TL when args.Count == 1: state = state with { Leading = N(args[0]) }; break;
                    case OpCodeName.Tc when args.Count == 1: state = state with { CharSpacing = N(args[0]) }; break;
                    case OpCodeName.Tw when args.Count == 1: state = state with { WordSpacing = N(args[0]) }; break;
                    case OpCodeName.Tz when args.Count == 1: state = state with { Scale = N(args[0]) / 100 }; break;
                    case OpCodeName.Ts when args.Count == 1: state = state with { Rise = N(args[0]) }; break;
                    case OpCodeName.Tf when args.Count == 2 && args[0] is CName name:
                        state = state with { Font = name.Name, Size = N(args[1]) };
                        break;
                    case OpCodeName.Tx: MoveLine(0, -state.Leading); break; // T*
                    case OpCodeName.QuoteSingle when args.Count == 1:
                        MoveLine(0, -state.Leading);
                        Show(i, [args[0]], "T* ");
                        break;
                    case OpCodeName.QuoteDouble when args.Count == 3:
                        state = state with { WordSpacing = N(args[0]), CharSpacing = N(args[1]) };
                        MoveLine(0, -state.Leading);
                        Show(i, [args[2]], $"{F(N(args[0]))} Tw {F(N(args[1]))} Tc T* ");
                        break;
                    case OpCodeName.Tj when args.Count == 1: Show(i, [args[0]], ""); break;
                    case OpCodeName.TJ when args.Count == 1 && args[0] is CArray array: Show(i, [.. array], ""); break;
                    case OpCodeName.Do when args.Count == 1 && args[0] is CName xname:
                        if (XObjectInArea(page, geometry, state.Ctm, xname.Name, areas, counts)) changes[i] = "";
                        break;
                }
            }

            // Mostra (Tj/TJ/'/"): calcula a posição de cada letra e troca as que caem na área por espaço vazio.
            void Show(int index, List<CObject> items, string prefix)
            {
                var font = fonts.TryGetValue(state.Font, out var known) ? known
                    : fonts[state.Font] = FontWidths.Of(page, state.Font);
                double size = state.Size, scale = state.Scale;
                double x = 0;
                var parts = new List<object>(); // byte[] (letra mantida), double (ajuste do TJ)
                int removed = 0, total = 0;
                foreach (var item in items)
                {
                    if (item is CString str)
                    {
                        byte[] bytes = Bytes(str);
                        int step = font.TwoBytes ? 2 : 1;
                        for (int k = 0; k + step <= bytes.Length; k += step)
                        {
                            int code = step == 2 ? bytes[k] << 8 | bytes[k + 1] : bytes[k];
                            double w0 = font.Width(code) / 1000;
                            double adv = (w0 * size + state.CharSpacing + (step == 1 && code == 32 ? state.WordSpacing : 0)) * scale;
                            total++;
                            if (InArea(x + w0 * size * scale / 2, state.Rise + 0.35 * size))
                            {
                                removed++;
                                parts.Add(size * scale == 0 ? 0.0 : -adv * 1000 / (size * scale));
                            }
                            else parts.Add(bytes[k..(k + step)]);
                            x += adv;
                        }
                    }
                    else
                    {
                        double n = N(item);
                        x -= n / 1000 * size * scale;
                        parts.Add(n);
                    }
                }
                tm = M.Translate(x, 0).Then(tm);
                if (removed == 0) return;
                if (!font.Exact || size * scale == 0)
                {
                    // Larguras desconhecidas: não dá para manter o resto no lugar com segurança; sai o comando inteiro.
                    counts.Letters += total;
                    changes[index] = prefix.Length > 0 ? prefix.TrimEnd() : "";
                    return;
                }
                counts.Letters += removed;
                changes[index] = prefix + BuildTJ(parts);
            }

            if (changes.Count == 0) continue;
            foreach (var (index, text) in changes.Reverse())
            {
                seq.RemoveAt(index);
                if (text.Length == 0) continue;
                var replacement = ContentReader.ReadContent(Encoding.Latin1.GetBytes(text));
                for (int k = replacement.Count - 1; k >= 0; k--) seq.Insert(index, replacement[k]);
            }
            stream.Elements.Remove("/Filter");
            stream.Elements.Remove("/DecodeParms");
            stream.Stream.Value = seq.ToContent();
        }
    }

    /// <summary>Monta "[&lt;letras&gt; -123 &lt;letras&gt;] TJ", juntando letras seguidas e ajustes seguidos.</summary>
    private static string BuildTJ(List<object> parts)
    {
        var sb = new StringBuilder("[");
        var run = new List<byte>();
        double? shift = null;
        void FlushRun()
        {
            if (run.Count == 0) return;
            sb.Append('<').Append(Convert.ToHexString(run.ToArray())).Append('>');
            run.Clear();
        }
        void FlushShift()
        {
            if (shift is not { } n) return;
            if (Math.Abs(n) > 1e-6) sb.Append(' ').Append(F(n)).Append(' ');
            shift = null;
        }
        foreach (var part in parts)
        {
            if (part is byte[] bytes) { FlushShift(); run.AddRange(bytes); }
            else { FlushRun(); shift = (shift ?? 0) + (double)part; }
        }
        FlushRun();
        FlushShift();
        return sb.Append("] TJ").ToString();
    }

    /// <summary>
    /// Imagem ou formulário do PDF desenhado por "Do". Devolve true se é uma imagem inteira dentro da área
    /// (vai ser apagada); se só encosta na área, conta como "só coberta".
    /// </summary>
    private static bool XObjectInArea(PdfPage page, PageGeometry geometry, M ctm, string name, List<RedactionArea> areas, Counts counts)
    {
        if (page.Resources.Elements.GetDictionary("/XObject")?.Elements.GetDictionary(name) is not { } xobject) return false;
        bool image = xobject.Elements.GetName("/Subtype") == "/Image";
        (double, double)[] corners;
        if (image) corners = [ctm.Apply(0, 0), ctm.Apply(1, 0), ctm.Apply(0, 1), ctm.Apply(1, 1)];
        else if (xobject.Elements.GetArray("/BBox") is { Elements.Count: 4 } box)
        {
            double x1 = box.Elements.GetReal(0), y1 = box.Elements.GetReal(1), x2 = box.Elements.GetReal(2), y2 = box.Elements.GetReal(3);
            var form = xobject.Elements.GetArray("/Matrix") is { Elements.Count: 6 } m
                ? new M(m.Elements.GetReal(0), m.Elements.GetReal(1), m.Elements.GetReal(2), m.Elements.GetReal(3), m.Elements.GetReal(4), m.Elements.GetReal(5)).Then(ctm)
                : ctm;
            corners = [form.Apply(x1, y1), form.Apply(x2, y1), form.Apply(x1, y2), form.Apply(x2, y2)];
        }
        else return false;
        var (vx1, vy1, vx2, vy2) = ViewBox(geometry, corners);
        if (image && areas.Any(a => a.Covers(vx1, vy1, vx2, vy2)))
        {
            counts.Images++;
            return true;
        }
        if (areas.Any(a => a.Intersects(vx1, vy1, vx2, vy2))) counts.Covered++;
        return false;
    }

    private static (double X1, double Y1, double X2, double Y2) ViewBox(PageGeometry geometry, IEnumerable<(double X, double Y)> pdfPoints)
    {
        var view = pdfPoints.Select(p => geometry.PdfToView(p.X, p.Y)).ToList();
        return (view.Min(p => p.X), view.Min(p => p.Y), view.Max(p => p.X), view.Max(p => p.Y));
    }

    private static byte[] Bytes(CString s) => s.Value.Select(c => (byte)c).ToArray();

    private static double N(CObject o) => o switch
    {
        CInteger i => i.Value,
        CReal r => r.Value,
        _ => 0,
    };

    private static string F(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    // ---------------- Larguras das letras ----------------

    /// <summary>Larguras (em milésimos do tamanho da fonte) lidas do dicionário da fonte no PDF.</summary>
    private sealed class FontWidths
    {
        public bool TwoBytes { get; private init; }
        /// <summary>As larguras vêm do próprio PDF (senão, são uma estimativa).</summary>
        public bool Exact { get; private init; }
        private readonly Dictionary<int, double> _widths = new();
        private double _default = 500;

        public double Width(int code) => _widths.TryGetValue(code, out var w) ? w : _default;

        public static FontWidths Of(PdfPage page, string resourceName)
        {
            var dict = page.Resources.Elements.GetDictionary("/Font")?.Elements.GetDictionary(resourceName);
            if (dict is null) return new FontWidths { Exact = false };
            string subtype = dict.Elements.GetName("/Subtype");
            if (subtype == "/Type0")
            {
                bool identity = dict.Elements.GetName("/Encoding") is "/Identity-H";
                var descendant = dict.Elements.GetArray("/DescendantFonts")?.Elements.GetDictionary(0);
                var font = new FontWidths { TwoBytes = true, Exact = identity && descendant is not null };
                if (descendant is null) return font;
                font._default = descendant.Elements.ContainsKey("/DW") ? descendant.Elements.GetReal("/DW") : 1000;
                if (descendant.Elements.GetArray("/W") is { } w) ParseW(w, font._widths);
                return font;
            }
            if (subtype == "/Type3" || dict.Elements.GetArray("/Widths") is not { } widths)
                return new FontWidths { Exact = false };
            var simple = new FontWidths { Exact = true };
            int first = dict.Elements.GetInteger("/FirstChar");
            for (int i = 0; i < widths.Elements.Count; i++) simple._widths[first + i] = Real(widths.Elements[i]);
            simple._default = dict.Elements.GetDictionary("/FontDescriptor")?.Elements.GetReal("/MissingWidth") ?? 0;
            return simple;
        }

        /// <summary>/W das fontes CID: "c [w1 w2 …]" ou "c1 c2 w".</summary>
        private static void ParseW(PdfArray w, Dictionary<int, double> into)
        {
            int i = 0;
            while (i < w.Elements.Count)
            {
                int start = (int)Real(w.Elements[i]);
                if (i + 1 < w.Elements.Count && Resolve(w.Elements[i + 1]) is PdfArray list)
                {
                    for (int k = 0; k < list.Elements.Count; k++) into[start + k] = Real(list.Elements[k]);
                    i += 2;
                }
                else if (i + 2 < w.Elements.Count)
                {
                    int end = (int)Real(w.Elements[i + 1]);
                    double width = Real(w.Elements[i + 2]);
                    for (int c = start; c <= end && c - start < 65536; c++) into[c] = width;
                    i += 3;
                }
                else break;
            }
        }

        private static PdfItem? Resolve(PdfItem? item) => item is PdfReference r ? r.Value : item;

        private static double Real(PdfItem? item) => Resolve(item) switch
        {
            PdfInteger i => i.Value,
            PdfReal r => r.Value,
            PdfLongInteger l => l.Value,
            _ => 0,
        };
    }
}
