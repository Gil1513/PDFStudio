using System.Globalization;
using System.IO.Compression;
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

    /// <summary>Quanto do retângulo (0 a 1) fica dentro da área.</summary>
    public double OverlapFraction(double x1, double y1, double x2, double y2)
    {
        double w = Math.Max(0, Math.Min(x2, X + Width) - Math.Max(x1, X));
        double h = Math.Max(0, Math.Min(y2, Y + Height) - Math.Max(y1, Y));
        double total = (x2 - x1) * (y2 - y1);
        return total <= 1e-9 ? 0 : w * h / total;
    }
}

/// <summary>
/// O que a redação fez. <paramref name="Letters"/>: letras apagadas do conteúdo; <paramref name="Images"/>: imagens
/// apagadas (inteiras ou só a parte dentro da área); <paramref name="Annotations"/>: comentários, links e campos
/// apagados; <paramref name="CoveredOnly"/>: coisas que só puderam ser cobertas; <paramref name="Drawings"/>:
/// desenhos (linhas, formas) inteiros dentro da área, apagados.
/// </summary>
public sealed record RedactionResult(int Letters, int Images, int Annotations, int CoveredOnly, int Drawings = 0);

/// <summary>A redação não pôde ser feita com segurança (nada foi alterado). A mensagem explica o motivo.</summary>
public sealed class RedactionException(string message) : Exception(message);

/// <summary>
/// Descompacta um JPG em pixels crus (largura × altura × <paramref name="components"/> bytes, linha a linha, de
/// cima para baixo). null = não conseguiu. O Core não tem leitor de JPG; o app passa o do Windows.
/// </summary>
public delegate byte[]? JpegPixelDecoder(byte[] jpeg, int components);

/// <summary>
/// Etapa 6.2: redigir (apagar de verdade) o que está em áreas da página e pôr uma tarja por cima.
/// <list type="bullet">
/// <item>Letras: a posição de cada uma vem das larguras da fonte; as que caem na área saem do conteúdo e viram
/// espaço vazio (o resto da linha fica no lugar). Vale também para o texto dentro de "formulários" internos do PDF
/// (com cópia própria, para não mexer em outras páginas que usam o mesmo formulário).</item>
/// <item>Imagens: inteiras na área saem do arquivo; só em parte, os pixels embaixo da área viram preto. Formato de
/// imagem que não dá para editar → a redação recusa (nunca fica "só coberta").</item>
/// <item>Desenhos inteiros na área, comentários, links e campos (com o valor guardado no formulário) saem.</item>
/// <item>Conteúdo que não dá para ler → a redação recusa, em vez de deixar o que estava lá.</item>
/// </list>
/// </summary>
public static class PdfRedaction
{
    /// <summary>Até quantos "formulários" dentro de formulários a redação entra.</summary>
    private const int MaxDepth = 12;

    public static RedactionResult Apply(string input, IReadOnlyList<RedactionArea> areas, string output) =>
        Apply(input, areas, output, new AnnotationColor(0, 0, 0));

    public static RedactionResult Apply(string input, IReadOnlyList<RedactionArea> areas, string output, AnnotationColor color,
        JpegPixelDecoder? decodeJpeg = null)
    {
        var valid = areas.Where(a => a.Width > 0 && a.Height > 0).ToList();
        if (valid.Count == 0) throw new ArgumentException("Marque pelo menos uma área para redigir.", nameof(areas));

        string temp = Path.Combine(Path.GetTempPath(), "PDFStudio", $"redigir-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            var counts = new Counts();
            int annotations = 0;
            PdfAnnotations.Edit(input, temp, doc =>
            {
                foreach (var group in valid.GroupBy(a => a.Page))
                {
                    var page = doc.Pages[PdfPageContent.CheckPage(doc, group.Key)];
                    var ctx = new Ctx(doc, PageGeometry.Of(page), group.ToList(), counts, decodeJpeg);
                    RedactPage(ctx, page);
                    annotations += RemoveAnnotations(doc, page, ctx.Areas);
                    page.Elements.Remove("/Thumb"); // miniatura guardada no arquivo mostraria o que foi apagado
                }
            });

            // Sobrou texto nas áreas (ex.: fonte desenhada, "Type3")? Fica coberto pela tarja e é avisado.
            int covered = counts.Covered;
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
            return new RedactionResult(counts.Letters, counts.Images, annotations, covered, counts.Drawings);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* temporário */ }
        }
    }

    // ---------------- Comentários, links e campos ----------------

    private static int RemoveAnnotations(PdfDocument doc, PdfPage page, List<RedactionArea> areas)
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
        // A janelinha (popup) de um comentário apagado vai junto.
        for (int i = 0; i < page.Annotations.Count; i++)
        {
            var annot = page.Annotations[i];
            if (annot.Elements.GetName("/Subtype") == "/Popup" && !doomed.Any(d => Same(d, annot))
                && annot.Elements.GetDictionary("/Parent") is { } parent && doomed.Any(d => Same(d, parent)))
                doomed.Add(annot);
        }
        foreach (var annot in doomed)
        {
            annot.Elements.Remove("/V");  // valor de campo de formulário (quando o campo e o widget são o mesmo objeto)
            annot.Elements.Remove("/DV");
            annot.Elements.Remove("/Contents");
            annot.Elements.Remove("/AP");
            if (annot.Elements.GetName("/Subtype") == "/Widget") DetachField(doc, annot, 0);
            page.Annotations.Remove(annot);
        }
        return doomed.Count(a => a.Elements.GetName("/Subtype") != "/Popup");
    }

    /// <summary>
    /// Tira o campo do formulário do documento (/AcroForm), com o valor guardado. Senão o valor continuaria no
    /// arquivo mesmo sem o campo aparecer na página.
    /// </summary>
    private static void DetachField(PdfDocument doc, PdfDictionary field, int depth)
    {
        if (depth > 20) return;
        field.Elements.Remove("/V");
        field.Elements.Remove("/DV");
        if (field.Elements.GetDictionary("/Parent") is { } parent)
        {
            parent.Elements.Remove("/V");
            parent.Elements.Remove("/DV");
            if (parent.Elements.GetArray("/Kids") is { } kids)
            {
                RemoveFrom(kids, field);
                if (kids.Elements.Count == 0) DetachField(doc, parent, depth + 1);
            }
            return;
        }
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is { } fields)
            RemoveFrom(fields, field);
    }

    private static void RemoveFrom(PdfArray array, PdfDictionary item)
    {
        for (int i = array.Elements.Count - 1; i >= 0; i--)
        {
            var element = array.Elements[i];
            var resolved = element is PdfReference r ? r.Value : element;
            if (ReferenceEquals(resolved, item) || (element is PdfReference re && item.Reference is { } ir && re.ObjectID == ir.ObjectID))
                array.Elements.RemoveAt(i);
        }
    }

    private static bool Same(PdfDictionary a, PdfDictionary b) =>
        ReferenceEquals(a, b) || (a.Reference is { } ra && b.Reference is { } rb && ra.ObjectID == rb.ObjectID);

    // ---------------- Conteúdo da página ----------------

    private sealed class Counts
    {
        public int Letters, Images, Covered, Drawings;
    }

    private sealed class Ctx(PdfDocument doc, PageGeometry geometry, List<RedactionArea> areas, Counts counts, JpegPixelDecoder? decodeJpeg)
    {
        public PdfDocument Doc { get; } = doc;
        public PageGeometry Geometry { get; } = geometry;
        public List<RedactionArea> Areas { get; } = areas;
        public Counts Counts { get; } = counts;
        public JpegPixelDecoder? DecodeJpeg { get; } = decodeJpeg;
        /// <summary>Formulários já abertos neste caminho (um formulário que se chama a si mesmo não trava).</summary>
        public HashSet<PdfObjectID> Opening { get; } = [];

        public bool InAnyArea(double x1, double y1, double x2, double y2) => Areas.Any(a => a.Intersects(x1, y1, x2, y2));
    }

    /// <summary>Estado gráfico que importa aqui (salvo por q e restaurado por Q).</summary>
    private sealed record State(M Ctm, string Font, double Size, double CharSpacing, double WordSpacing, double Scale, double Leading, double Rise);

    private static void RedactPage(Ctx ctx, PdfPage page)
    {
        if (page.Elements["/Contents"] is null) return;
        var streams = new List<PdfDictionary>();
        var contents = page.Contents.Elements;
        for (int s = 0; s < contents.Count; s++)
            if (contents.GetDictionary(s) is { } stream) streams.Add(stream);
        var resources = page.Elements.GetDictionary("/Resources");
        RedactStreams(ctx, page, resources, streams, new State(M.Identity, "", 0, 0, 0, 1, 0, 0), 0);
    }

    /// <summary>
    /// Apaga o que cai nas áreas em <paramref name="streams"/> (o conteúdo da página, ou de um formulário interno).
    /// <paramref name="owner"/> é quem tem os recursos (página ou formulário); só ganha recursos próprios se algo
    /// for trocado neles. Devolve true se mudou alguma coisa.
    /// </summary>
    private static bool RedactStreams(Ctx ctx, PdfDictionary owner, PdfDictionary? resources, List<PdfDictionary> streams, State start, int depth)
    {
        var fonts = new Dictionary<string, FontWidths>();
        var state = start;
        var stack = new Stack<State>();
        M tm = M.Identity, tlm = M.Identity;
        bool changedAny = false;
        PdfDictionary? privateXObjects = null; // cópia própria dos /XObject de owner (criada só se precisar)
        var removedImages = new HashSet<string>();
        var usedNames = new HashSet<string>();

        PdfDictionary? XObject(string name) =>
            (privateXObjects ?? resources?.Elements.GetDictionary("/XObject"))?.Elements.GetDictionary(name);

        PdfDictionary WritableXObjects()
        {
            if (privateXObjects is not null) return privateXObjects;
            var res = Copy(ctx.Doc, resources);
            owner.Elements["/Resources"] = res;
            privateXObjects = Copy(ctx.Doc, res.Elements.GetDictionary("/XObject"));
            res.Elements["/XObject"] = privateXObjects;
            resources = res;
            return privateXObjects;
        }

        void MoveLine(double tx, double ty)
        {
            tlm = M.Translate(tx, ty).Then(tlm);
            tm = tlm;
        }

        foreach (var stream in streams)
        {
            if (stream.Stream is null) continue;
            CSequence seq;
            try
            {
                var filters = Filters(stream.Elements.GetValue("/Filter"));
                byte[] data;
                if (filters.Count == 1 && filters[0] is "/FlateDecode" or "/Fl" && stream.Elements["/DecodeParms"] is null)
                {
                    // Descompacta aqui: a biblioteca não avisa quando falha (devolve outra coisa em silêncio).
                    using var z = new ZLibStream(new MemoryStream(stream.Stream.Value), CompressionMode.Decompress);
                    using var plain = new MemoryStream();
                    z.CopyTo(plain);
                    data = plain.ToArray();
                    if (data.Length == 0 && stream.Stream.Value.Length > 2) throw new InvalidDataException(); // nada saiu de algo que não é vazio
                }
                else
                {
                    data = stream.Stream.UnfilteredValue;
                    if (filters.Count > 0 && data.AsSpan().SequenceEqual(stream.Stream.Value)) throw new InvalidDataException();
                }
                seq = ContentReader.ReadContent(data);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                throw new RedactionException("Uma parte desta página não pôde ser lida, então não dá para garantir que o que está " +
                    "embaixo da tarja seria apagado. Nada foi alterado.");
            }
            var changes = new SortedDictionary<int, string>(); // índice → conteúdo novo ("" = apagar)

            // Desenho (caminho) em construção: onde começou e os pontos, já na tela.
            int pathStart = -1;
            bool pathClips = false;
            var pathPoints = new List<(double X, double Y)>();
            void AddPoint(int index, double x, double y)
            {
                if (pathStart < 0) pathStart = index;
                var (px, py) = state.Ctm.Apply(x, y);
                var v = ctx.Geometry.PdfToView(px, py);
                pathPoints.Add((v.X, v.Y));
            }
            void EndPath(int index, bool paints)
            {
                if (paints && pathStart >= 0 && !pathClips && pathPoints.Count > 0)
                {
                    double x1 = pathPoints.Min(p => p.X), y1 = pathPoints.Min(p => p.Y), x2 = pathPoints.Max(p => p.X), y2 = pathPoints.Max(p => p.Y);
                    if (ctx.Areas.Any(a => a.Covers(x1, y1, x2, y2)))
                    {
                        for (int k = pathStart; k <= index; k++) changes[k] = "";
                        ctx.Counts.Drawings++;
                    }
                }
                pathStart = -1;
                pathClips = false;
                pathPoints.Clear();
            }

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

                    // Desenhos
                    case OpCodeName.m or OpCodeName.l when args.Count == 2: AddPoint(i, N(args[0]), N(args[1])); break;
                    case OpCodeName.c when args.Count == 6:
                        AddPoint(i, N(args[0]), N(args[1])); AddPoint(i, N(args[2]), N(args[3])); AddPoint(i, N(args[4]), N(args[5]));
                        break;
                    case OpCodeName.v or OpCodeName.y when args.Count == 4:
                        AddPoint(i, N(args[0]), N(args[1])); AddPoint(i, N(args[2]), N(args[3]));
                        break;
                    case OpCodeName.re when args.Count == 4:
                        double rx = N(args[0]), ry = N(args[1]), rw = N(args[2]), rh = N(args[3]);
                        AddPoint(i, rx, ry); AddPoint(i, rx + rw, ry); AddPoint(i, rx, ry + rh); AddPoint(i, rx + rw, ry + rh);
                        break;
                    case OpCodeName.h: if (pathStart < 0) pathStart = i; break;
                    case OpCodeName.W or OpCodeName.Wx: pathClips = true; break;
                    case OpCodeName.S or OpCodeName.s or OpCodeName.f or OpCodeName.F or OpCodeName.fx
                        or OpCodeName.B or OpCodeName.Bx or OpCodeName.b or OpCodeName.bx:
                        EndPath(i, paints: true);
                        break;
                    case OpCodeName.n: EndPath(i, paints: false); break;

                    // Texto
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

                    // Imagem no meio do conteúdo (BI … ID … EI): sai inteira se encosta na área.
                    case OpCodeName.BI:
                    {
                        int end = i;
                        while (end < seq.Count && !(seq[end] is COperator e && e.OpCode.OpCodeName == OpCodeName.EI)) end++;
                        if (end >= seq.Count) end = i;
                        var (x1, y1, x2, y2) = ViewBox(ctx.Geometry, UnitSquare(state.Ctm));
                        if (ctx.InAnyArea(x1, y1, x2, y2))
                        {
                            for (int k = i; k <= end; k++) changes[k] = "";
                            ctx.Counts.Images++;
                        }
                        i = end;
                        break;
                    }

                    case OpCodeName.Do when args.Count == 1 && args[0] is CName xname:
                        DrawObject(i, xname.Name);
                        break;
                }
            }

            void DrawObject(int index, string name)
            {
                if (XObject(name) is not { } xobject) return;
                string subtype = xobject.Elements.GetName("/Subtype");
                if (subtype == "/Image")
                {
                    var (x1, y1, x2, y2) = ViewBox(ctx.Geometry, UnitSquare(state.Ctm));
                    if (ctx.Areas.Any(a => a.Covers(x1, y1, x2, y2)))
                    {
                        changes[index] = "";
                        removedImages.Add(name);
                        ctx.Counts.Images++;
                        return;
                    }
                    usedNames.Add(name);
                    if (!ctx.InAnyArea(x1, y1, x2, y2)) return;
                    var blacked = BlackOutImage(ctx, xobject, state.Ctm);
                    WritableXObjects().Elements[name] = blacked.Reference!;
                    changedAny = true;
                    ctx.Counts.Images++;
                    return;
                }
                usedNames.Add(name);
                if (subtype != "/Form" || xobject.Stream is null) return;
                if (xobject.Elements.GetArray("/BBox") is not { Elements.Count: 4 } box) return;
                var formCtm = xobject.Elements.GetArray("/Matrix") is { Elements.Count: 6 } fm
                    ? new M(fm.Elements.GetReal(0), fm.Elements.GetReal(1), fm.Elements.GetReal(2), fm.Elements.GetReal(3), fm.Elements.GetReal(4), fm.Elements.GetReal(5)).Then(state.Ctm)
                    : state.Ctm;
                double bx1 = box.Elements.GetReal(0), by1 = box.Elements.GetReal(1), bx2 = box.Elements.GetReal(2), by2 = box.Elements.GetReal(3);
                var (vx1, vy1, vx2, vy2) = ViewBox(ctx.Geometry, [formCtm.Apply(bx1, by1), formCtm.Apply(bx2, by1), formCtm.Apply(bx1, by2), formCtm.Apply(bx2, by2)]);
                if (!ctx.InAnyArea(vx1, vy1, vx2, vy2)) return;
                if (depth >= MaxDepth || (xobject.Reference is { } r && ctx.Opening.Contains(r.ObjectID)))
                    throw new RedactionException("Esta página tem conteúdo guardado em camadas demais (ou que se repete sem fim); " +
                        "não dá para garantir que o que está embaixo da tarja seria apagado. Nada foi alterado.");

                // Cópia própria do formulário: outras páginas que usam o mesmo formulário ficam como estão.
                var copy = CopyStream(ctx.Doc, xobject);
                var formResources = xobject.Elements.GetDictionary("/Resources") ?? resources;
                if (xobject.Reference is { } opened) ctx.Opening.Add(opened.ObjectID);
                bool changed;
                try { changed = RedactStreams(ctx, copy, formResources, [copy], state with { Ctm = formCtm }, depth + 1); }
                finally { if (xobject.Reference is { } done) ctx.Opening.Remove(done.ObjectID); }
                if (!changed) return;
                WritableXObjects().Elements[name] = copy.Reference!;
                changedAny = true;
            }

            // Mostra (Tj/TJ/'/"): calcula a caixa de cada letra e troca as que caem na área por espaço vazio.
            void Show(int index, List<CObject> items, string prefix)
            {
                var font = fonts.TryGetValue(state.Font, out var known) ? known
                    : fonts[state.Font] = FontWidths.Of(resources, state.Font);
                double size = state.Size, scale = state.Scale;
                double x = 0;
                var parts = new List<object>(); // byte[] (letra mantida), double (ajuste do TJ)
                int removed = 0, total = 0;
                var toView = tm.Then(state.Ctm);
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
                            if (GlyphInArea(ctx, toView, x, x + w0 * size * scale, state.Rise - 0.22 * size, state.Rise + 0.78 * size))
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
                    ctx.Counts.Letters += total;
                    changes[index] = prefix.Length > 0 ? prefix.TrimEnd() : "";
                    return;
                }
                ctx.Counts.Letters += removed;
                changes[index] = prefix + BuildTJ(parts);
            }

            if (changes.Count == 0) continue;
            changedAny = true;
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

        // Imagens apagadas que não são mais desenhadas aqui saem dos recursos: assim os dados saem do arquivo.
        foreach (string name in removedImages.Where(n => !usedNames.Contains(n)))
            WritableXObjects().Elements.Remove(name);
        return changedAny || removedImages.Count > 0;
    }

    /// <summary>A letra sai se o meio dela está na área ou se mais de um terço dela está embaixo da tarja.</summary>
    private static bool GlyphInArea(Ctx ctx, M toPdf, double x1, double x2, double y1, double y2)
    {
        var (vx1, vy1, vx2, vy2) = ViewBox(ctx.Geometry, [toPdf.Apply(x1, y1), toPdf.Apply(x2, y1), toPdf.Apply(x1, y2), toPdf.Apply(x2, y2)]);
        var (cx, cy) = toPdf.Apply((x1 + x2) / 2, y1 + (y2 - y1) * 0.57);
        var center = ctx.Geometry.PdfToView(cx, cy);
        return ctx.Areas.Any(a => a.Contains(center.X, center.Y) || a.OverlapFraction(vx1, vy1, vx2, vy2) >= 0.35);
    }

    private static (double X, double Y)[] UnitSquare(M ctm) => [ctm.Apply(0, 0), ctm.Apply(1, 0), ctm.Apply(0, 1), ctm.Apply(1, 1)];

    /// <summary>Cópia rasa (direta) de um dicionário; null vira dicionário vazio.</summary>
    private static PdfDictionary Copy(PdfDocument doc, PdfDictionary? source)
    {
        var copy = new PdfDictionary(doc);
        if (source is not null)
            foreach (string key in source.Elements.Keys) copy.Elements[key] = source.Elements[key];
        return copy;
    }

    /// <summary>Cópia de um fluxo (formulário ou imagem) como objeto novo, já descompactado.</summary>
    private static PdfDictionary CopyStream(PdfDocument doc, PdfDictionary source)
    {
        var copy = new PdfDictionary(doc);
        foreach (string key in source.Elements.Keys)
            if (key is not ("/Length" or "/Filter" or "/DecodeParms")) copy.Elements[key] = source.Elements[key];
        copy.CreateStream(source.Stream!.UnfilteredValue);
        doc.Internals.AddObject(copy);
        return copy;
    }

    // ---------------- Imagens só em parte na área ----------------

    private static RedactionException CannotEditImage(string why) => new(
        $"Uma imagem encosta na área, mas não dá para apagar só a parte dela embaixo da tarja ({why}). " +
        "Aumente a área para cobrir a imagem inteira (aí ela sai toda) ou ajuste a área para não encostar nela. Nada foi alterado.");

    /// <summary>
    /// Nova imagem igual à original, com os pixels embaixo das áreas apagados (zerados). A original fica para
    /// outras páginas que a usam; aqui passa a ser desenhada a nova.
    /// </summary>
    private static PdfDictionary BlackOutImage(Ctx ctx, PdfDictionary image, M ctm)
    {
        var inverse = ctm.Invert() ?? throw CannotEditImage("posição inválida");
        var regions = ctx.Areas
            .Select(a => new[] { (a.X, a.Y), (a.X + a.Width, a.Y), (a.X, a.Y + a.Height), (a.X + a.Width, a.Y + a.Height) }
                .Select(p => ctx.Geometry.ViewToPdf(p.Item1, p.Item2))
                .Select(p => inverse.Apply(p.X, p.Y))
                .ToList())
            .Select(c => (S1: c.Min(p => p.X), T1: c.Min(p => p.Y), S2: c.Max(p => p.X), T2: c.Max(p => p.Y)))
            .Where(r => r.S2 > 0 && r.S1 < 1 && r.T2 > 0 && r.T1 < 1)
            .ToList();

        var result = BlackOut(ctx, image, regions, isMask: image.Elements.GetBoolean("/ImageMask"));
        // Máscaras (transparência, recorte) também guardam o desenho da imagem: apagam junto.
        foreach (string key in new[] { "/SMask", "/Mask" })
            if (image.Elements.GetValue(key) is PdfDictionary { Stream: not null } mask)
                result.Elements[key] = BlackOut(ctx, mask, regions, isMask: true).Reference!;
        return result;
    }

    private static PdfDictionary BlackOut(Ctx ctx, PdfDictionary image, List<(double S1, double T1, double S2, double T2)> regions, bool isMask)
    {
        var e = image.Elements;
        int width = e.GetInteger("/Width"), height = e.GetInteger("/Height");
        bool stencil = e.GetBoolean("/ImageMask");
        int bpc = stencil ? 1 : e.GetInteger("/BitsPerComponent");
        int components = stencil || (isMask && !e.ContainsKey("/ColorSpace")) ? 1 : Components(e.GetValue("/ColorSpace"));
        if (width <= 0 || height <= 0 || (long)width * height > 200_000_000) throw CannotEditImage("tamanho inválido");
        if (components <= 0) throw CannotEditImage("tipo de cor desconhecido");
        if (bpc is not (1 or 2 or 4 or 8 or 16)) throw CannotEditImage("formato de pixel desconhecido");

        var filters = Filters(e.GetValue("/Filter"));
        long stride = ((long)width * components * bpc + 7) / 8;
        byte[] pixels;
        if (filters.Count == 1 && filters[0] == "/DCTDecode")
        {
            if (ctx.DecodeJpeg is null || bpc != 8 || components is not (1 or 3)) throw CannotEditImage("JPG em formato especial");
            pixels = ctx.DecodeJpeg(image.Stream!.Value, components) ?? throw CannotEditImage("JPG que não abriu");
        }
        else if (filters.All(f => f is "/FlateDecode" or "/Fl" or "/LZWDecode" or "/LZW" or "/RunLengthDecode" or "/RL"
                     or "/ASCIIHexDecode" or "/AHx" or "/ASCII85Decode" or "/A85"))
        {
            try { pixels = filters.Count == 0 ? image.Stream!.Value : image.Stream!.UnfilteredValue; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw CannotEditImage("dados que não abriram"); }
        }
        else throw CannotEditImage($"formato {string.Join(" + ", filters).TrimStart('/')}");
        if (pixels.LongLength < stride * height) throw CannotEditImage("dados incompletos");
        pixels = pixels[..(int)(stride * height)];

        foreach (var (s1, t1, s2, t2) in regions)
        {
            // Na imagem, a linha 0 é a de cima (t = 1).
            int c1 = Math.Clamp((int)Math.Floor(s1 * width), 0, width), c2 = Math.Clamp((int)Math.Ceiling(s2 * width), 0, width);
            int r1 = Math.Clamp((int)Math.Floor((1 - t2) * height), 0, height), r2 = Math.Clamp((int)Math.Ceiling((1 - t1) * height), 0, height);
            int bitsPerPixel = components * bpc;
            for (int row = r1; row < r2; row++)
            {
                long rowStart = row * stride * 8;
                long from = rowStart + (long)c1 * bitsPerPixel, to = rowStart + (long)c2 * bitsPerPixel;
                ClearBits(pixels, from, to);
            }
        }

        var copy = new PdfDictionary(ctx.Doc);
        foreach (string key in e.Keys)
            if (key is not ("/Length" or "/Filter" or "/DecodeParms")) copy.Elements[key] = e[key];
        using (var packed = new MemoryStream())
        {
            using (var z = new ZLibStream(packed, CompressionLevel.Optimal, leaveOpen: true)) z.Write(pixels);
            copy.CreateStream(packed.ToArray());
        }
        copy.Elements["/Filter"] = new PdfName("/FlateDecode");
        ctx.Doc.Internals.AddObject(copy);
        return copy;
    }

    /// <summary>Zera os bits [from, to) (contando do bit mais alto do primeiro byte).</summary>
    private static void ClearBits(byte[] data, long from, long to)
    {
        long bit = from;
        while (bit < to && bit % 8 != 0) { data[bit / 8] &= (byte)~(0x80 >> (int)(bit % 8)); bit++; }
        while (bit + 8 <= to) { data[bit / 8] = 0; bit += 8; }
        while (bit < to) { data[bit / 8] &= (byte)~(0x80 >> (int)(bit % 8)); bit++; }
    }

    private static List<string> Filters(PdfItem? filter) => filter switch
    {
        null => [],
        PdfName n => [n.Value],
        PdfArray a => Enumerable.Range(0, a.Elements.Count).Select(i => a.Elements.GetName(i)).ToList(),
        _ => ["?"],
    };

    /// <summary>Quantos números por pixel o espaço de cor usa (0 = desconhecido).</summary>
    private static int Components(PdfItem? colorSpace)
    {
        var cs = colorSpace is PdfReference r ? r.Value : colorSpace;
        switch (cs)
        {
            case PdfName n:
                return n.Value switch
                {
                    "/DeviceGray" or "/G" or "/CalGray" => 1,
                    "/DeviceRGB" or "/RGB" or "/CalRGB" or "/Lab" => 3,
                    "/DeviceCMYK" or "/CMYK" => 4,
                    _ => 0,
                };
            case PdfArray a when a.Elements.Count > 0:
                string family = a.Elements.GetName(0);
                return family switch
                {
                    "/Indexed" or "/I" or "/Separation" => 1,
                    "/CalGray" => 1,
                    "/CalRGB" or "/Lab" => 3,
                    "/ICCBased" when a.Elements.Count > 1 && (a.Elements[1] is PdfReference { Value: PdfDictionary icc }) => icc.Elements.GetInteger("/N"),
                    "/DeviceN" when a.Elements.Count > 1 && (a.Elements[1] is PdfReference rr ? rr.Value : a.Elements[1]) is PdfArray names => names.Elements.Count,
                    _ => 0,
                };
            default:
                return 0;
        }
    }

    // ---------------- Auxiliares ----------------

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

        public static FontWidths Of(PdfDictionary? resources, string resourceName)
        {
            var dict = resources?.Elements.GetDictionary("/Font")?.Elements.GetDictionary(resourceName);
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
