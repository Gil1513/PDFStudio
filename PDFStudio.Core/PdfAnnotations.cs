using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Cor RGB de um comentário.</summary>
public readonly record struct AnnotationColor(byte R, byte G, byte B)
{
    public static AnnotationColor FromHex(string hex)
    {
        hex = hex.TrimStart('#');
        return new(Convert.ToByte(hex[..2], 16), Convert.ToByte(hex[2..4], 16), Convert.ToByte(hex[4..6], 16));
    }

    public string ToHex() => $"#{R:X2}{G:X2}{B:X2}";

    /// <summary>Componentes de 0 a 1, no formato do PDF ("1 0 0").</summary>
    internal string Pdf => $"{N(R / 255.0)} {N(G / 255.0)} {N(B / 255.0)}";

    /// <summary>A cor como aparece nos comandos do PDF (ex.: "1 0 0").</summary>
    public string ToPdfString() => Pdf;

    internal PdfArray ToPdfArray(PdfDocument doc) => new(doc, new PdfReal(R / 255.0), new PdfReal(G / 255.0), new PdfReal(B / 255.0));

    internal static string N(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);
}

/// <summary>Tipos de comentário que o PDF Studio cria ou reconhece.</summary>
public enum AnnotationKind { Rectangle, Oval, Line, Ink, TextBox, Note, Highlight, Underline, StrikeOut, Stamp, Other }

/// <summary>Aparência de um comentário novo.</summary>
public sealed record AnnotationStyle(AnnotationColor Color, double LineWidth = 2, double Opacity = 1);

/// <summary>
/// Um comentário existente, com a posição já convertida para a tela (pontos, canto superior esquerdo).
/// <see cref="Id"/> é o identificador único gravado no PDF (/NM), vazio se o comentário não tiver um.
/// </summary>
public sealed record AnnotationInfo(
    int PageIndex,
    int Index,
    AnnotationKind Kind,
    string Subtype,
    double X, double Y, double Width, double Height,
    string Contents,
    string Author,
    DateTime? Modified,
    AnnotationColor? Color,
    bool Hidden,
    string Id = "");

/// <summary>
/// Comentários (anotações) do PDF: criar, listar e excluir. Cada comentário criado recebe uma
/// aparência própria (/AP), para ser exibido igual em qualquer leitor (Adobe, navegadores, celular).
/// </summary>
public static partial class PdfAnnotations
{
    /// <summary>Tipos que não são "comentários" para o usuário (links, campos de formulário, janelas pop-up).</summary>
    private static readonly HashSet<string> NotComments = ["/Link", "/Widget", "/Popup"];

    // ---------------- Formas ----------------

    /// <summary>
    /// Desenha um retângulo ou uma oval. A posição (<paramref name="x"/>, <paramref name="y"/>, largura, altura)
    /// é a da tela, em pontos, a partir do canto superior esquerdo da página visível.
    /// </summary>
    public static void AddShape(string input, int pageIndex, AnnotationKind kind, double x, double y, double width, double height,
        AnnotationStyle style, string author, string output, string? contents = null)
    {
        if (kind is not (AnnotationKind.Rectangle or AnnotationKind.Oval))
            throw new ArgumentException("Use Rectangle ou Oval.", nameof(kind));

        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var rect = PageGeometry.Of(page).ViewRectToPdf(x, y, width, height);
            var annot = NewAnnotation(doc, page, kind == AnnotationKind.Rectangle ? "/Square" : "/Circle", rect, style, author, contents);

            annot.Elements["/BS"] = BorderStyle(doc, style.LineWidth);
            DrawShape(doc, annot, kind == AnnotationKind.Rectangle, rect.Width, rect.Height, style);
        });
    }

    /// <summary>Aparência do retângulo/oval do tamanho do /Rect (também usada ao redimensionar).</summary>
    private static void DrawShape(PdfDocument doc, PdfDictionary annot, bool rectangle, double w, double h, AnnotationStyle style)
    {
        double lw = style.LineWidth;
        string path = rectangle
            ? $"{N(lw / 2)} {N(lw / 2)} {N(w - lw)} {N(h - lw)} re"
            : Ellipse(lw / 2, lw / 2, w - lw, h - lw);
        SetAppearance(doc, annot, w, h, $"{style.Color.Pdf} RG {N(lw)} w {path} S", style.Opacity);
    }

    /// <summary>
    /// Linha reta de (x1, y1) a (x2, y2), posições da tela em pontos. Com <paramref name="arrow"/>,
    /// desenha uma ponta de seta no fim.
    /// </summary>
    public static void AddLine(string input, int pageIndex, double x1, double y1, double x2, double y2, bool arrow,
        AnnotationStyle style, string author, string output, string? contents = null)
    {
        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var geometry = PageGeometry.Of(page);
            var a = geometry.ViewToPdf(x1, y1);
            var b = geometry.ViewToPdf(x2, y2);

            var annot = NewAnnotation(doc, page, "/Line", BoundingBox([a, b], style.LineWidth + 2), style, author, contents);
            annot.Elements["/BS"] = BorderStyle(doc, style.LineWidth);
            if (arrow) annot.Elements["/LE"] = new PdfArray(doc, new PdfName("/None"), new PdfName("/OpenArrow"));
            DrawLine(doc, annot, a, b, arrow, style);
        });
    }

    /// <summary>Grava /L, o /Rect (com folga para a seta) e a aparência da linha de <paramref name="a"/> a <paramref name="b"/>.</summary>
    private static void DrawLine(PdfDocument doc, PdfDictionary annot, XPoint a, XPoint b, bool arrow, AnnotationStyle style)
    {
        // A ponta da seta: duas "asas" de comprimento proporcional à espessura.
        double lw = style.LineWidth, head = Math.Max(8, lw * 4);
        var points = new List<XPoint> { a, b };
        XPoint wing1 = default, wing2 = default;
        if (arrow)
        {
            double angle = Math.Atan2(b.Y - a.Y, b.X - a.X), spread = Math.PI / 7;
            wing1 = new XPoint(b.X - head * Math.Cos(angle - spread), b.Y - head * Math.Sin(angle - spread));
            wing2 = new XPoint(b.X - head * Math.Cos(angle + spread), b.Y - head * Math.Sin(angle + spread));
            points.Add(wing1);
            points.Add(wing2);
        }
        var rect = BoundingBox(points, lw + 2);
        annot.Elements["/Rect"] = rect;
        annot.Elements["/L"] = new PdfArray(doc, new PdfReal(a.X), new PdfReal(a.Y), new PdfReal(b.X), new PdfReal(b.Y));

        var ops = new StringBuilder($"{style.Color.Pdf} RG {N(lw)} w 1 J 1 j ");
        ops.Append($"{Local(a, rect)} m {Local(b, rect)} l S");
        if (arrow) ops.Append($" {Local(wing1, rect)} m {Local(b, rect)} l {Local(wing2, rect)} l S");
        SetAppearance(doc, annot, rect.Width, rect.Height, ops.ToString(), style.Opacity);
    }

    /// <summary>
    /// Desenho livre: cada traço é uma lista de pontos da tela (em pontos). Traços com menos de 2 pontos são ignorados.
    /// </summary>
    public static void AddInk(string input, int pageIndex, IReadOnlyList<IReadOnlyList<(double X, double Y)>> strokes,
        AnnotationStyle style, string author, string output, string? contents = null)
    {
        var valid = strokes.Where(s => s.Count >= 2).ToList();
        if (valid.Count == 0) throw new ArgumentException("O desenho precisa de ao menos um traço.", nameof(strokes));

        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var geometry = PageGeometry.Of(page);
            var pdfStrokes = valid.Select(s => s.Select(p => geometry.ViewToPdf(p.X, p.Y)).ToList()).ToList();
            var annot = NewAnnotation(doc, page, "/Ink", BoundingBox(pdfStrokes.SelectMany(s => s), style.LineWidth + 2), style, author, contents);
            annot.Elements["/BS"] = BorderStyle(doc, style.LineWidth);
            DrawInk(doc, annot, pdfStrokes, style);
        });
    }

    /// <summary>Grava /InkList, o /Rect e a aparência dos traços (pontos do PDF).</summary>
    private static void DrawInk(PdfDocument doc, PdfDictionary annot, IReadOnlyList<IReadOnlyList<XPoint>> strokes, AnnotationStyle style)
    {
        double lw = style.LineWidth;
        var rect = BoundingBox(strokes.SelectMany(s => s), lw + 2);
        annot.Elements["/Rect"] = rect;
        var inkList = new PdfArray(doc);
        foreach (var stroke in strokes)
        {
            var array = new PdfArray(doc);
            foreach (var p in stroke) { array.Elements.Add(new PdfReal(p.X)); array.Elements.Add(new PdfReal(p.Y)); }
            inkList.Elements.Add(array);
        }
        annot.Elements["/InkList"] = inkList;

        var ops = new StringBuilder($"{style.Color.Pdf} RG {N(lw)} w 1 J 1 j");
        foreach (var stroke in strokes)
        {
            ops.Append($" {Local(stroke[0], rect)} m");
            foreach (var p in stroke.Skip(1)) ops.Append($" {Local(p, rect)} l");
            ops.Append(" S");
        }
        SetAppearance(doc, annot, rect.Width, rect.Height, ops.ToString(), style.Opacity);
    }

    // ---------------- Listar / excluir ----------------

    /// <summary>Lista os comentários de todas as páginas, na ordem em que aparecem.</summary>
    public static List<AnnotationInfo> List(string path)
    {
        using var doc = PdfLoad.Open(new MemoryStream(File.ReadAllBytes(path)), PdfDocumentOpenMode.Import);
        var result = new List<AnnotationInfo>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            var page = doc.Pages[p];
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) continue;
            var geometry = PageGeometry.Of(page);
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var a = annots.Elements.GetDictionary(i);
                if (a is null) continue;
                string subtype = a.Elements.GetName("/Subtype");
                if (NotComments.Contains(subtype) || a.Elements.GetArray("/Rect") is not { Elements.Count: 4 } r) continue;

                var (vx, vy, vw, vh) = geometry.PdfRectToView(r.Elements.GetReal(0), r.Elements.GetReal(1), r.Elements.GetReal(2), r.Elements.GetReal(3));
                result.Add(new AnnotationInfo(p, i, KindOf(subtype, a), subtype, vx, vy, vw, vh,
                    a.Elements.GetString("/Contents"), a.Elements.GetString("/T"),
                    DocumentPropertiesService.ParsePdfDate(a.Elements.GetString("/M")),
                    ReadColor(a.Elements.GetArray("/C")) ?? ColorFromDA(a.Elements.GetString("/DA")),
                    (a.Elements.GetInteger("/F") & HiddenFlag) != 0,
                    a.Elements.GetString("/NM")));
            }
        }
        return result;
    }

    /// <summary>Exclui o comentário <paramref name="index"/> (posição em /Annots) da página, e o pop-up ligado a ele.</summary>
    public static void Delete(string input, int pageIndex, int index, string output) =>
        Edit(input, output, doc =>
        {
            var annots = GetPage(doc, pageIndex).Elements.GetArray("/Annots")
                ?? throw new InvalidOperationException("A página não tem comentários.");
            var target = annots.Elements.GetDictionary(index) ?? throw new ArgumentOutOfRangeException(nameof(index));
            var popup = target.Elements.GetDictionary("/Popup");
            for (int i = annots.Elements.Count - 1; i >= 0; i--)
            {
                var a = annots.Elements.GetDictionary(i);
                if (ReferenceEquals(a, target) || (popup is not null && ReferenceEquals(a, popup))) annots.Elements.RemoveAt(i);
            }
        });

    // ---------------- Infraestrutura ----------------

    private const int PrintFlag = 4, HiddenFlag = 2;

    /// <summary>Abre o PDF para edição (lido da memória, então pode gravar por cima dele mesmo), aplica e salva.</summary>
    internal static void Edit(string input, string output, Action<PdfDocument> edit)
    {
        byte[] original = File.ReadAllBytes(input);
        if (TryEditIncrementally(original, output, edit)) return;

        PdfDocument doc;
        try
        {
            doc = PdfLoad.Open(new MemoryStream(original), PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Este PDF é protegido por senha; não é possível adicionar comentários nele.", ex);
        }
        using (doc)
        {
            var before = doc.Pages.Cast<PdfPage>().Select(ContentsKey).ToList();
            edit(doc);
            for (int i = 0; i < doc.PageCount && i < before.Count; i++)
                if (ContentsKey(doc.Pages[i]) != before[i]) MergeContentStreams(doc.Pages[i]);
            PdfOperations.SaveSafely(doc, output);
        }
    }

    /// <summary>Quais pedaços de conteúdo a página tem (para saber se a edição mexeu nela).</summary>
    private static string ContentsKey(PdfPage page) => page.Elements["/Contents"] switch
    {
        PdfArray array => string.Join(",", array.Elements.Select(e => e is PdfReference r ? r.ObjectNumber.ToString() : "d")),
        PdfReference r => r.ObjectNumber.ToString(),
        _ => "",
    };

    /// <summary>
    /// Junta os pedaços do conteúdo da página num só (o desenho é exatamente o mesmo). Cada edição acrescenta pedaços
    /// e "camadas" (q/Q) que passam de um pedaço para o outro; depois de muitas edições, o leitor de PDF do Windows
    /// desiste e mostra a página inteira em branco. Com um pedaço só, isso não acontece.
    /// </summary>
    internal static void MergeContentStreams(PdfPage page)
    {
        var contents = page.Contents;
        if (contents.Elements.Count < 2) return;
        var doc = page.Owner;
        // Marca d'água, fundo, cabeçalho… (trechos marcados pelo app) ficam num pedaço próprio: é assim que dá para
        // removê-los depois. Os outros pedaços seguidos viram um só.
        var pieces = new List<(byte[] Data, PdfItem? Keep)>();
        foreach (var item in contents.Elements)
        {
            if ((item is PdfReference r ? r.Value : item) is not PdfDictionary { Stream: { } stream }) return;
            if (!stream.TryUnfilter()) return; // compressão que não dá para abrir: deixa como está
            bool artifact = Encoding.Latin1.GetString(stream.Value).Contains("/Artifact <</PDFStudio ", StringComparison.Ordinal);
            pieces.Add((stream.Value, artifact ? item : null));
        }
        var merged = new List<PdfItem>();
        using var run = new MemoryStream();
        void Flush()
        {
            if (run.Length == 0) return;
            var content = new PdfDictionary(doc);
            content.CreateStream(run.ToArray());
            doc.Internals.AddObject(content);
            merged.Add(content.Reference!);
            run.SetLength(0);
        }
        foreach (var (data, keep) in pieces)
        {
            if (keep is not null) { Flush(); merged.Add(keep); continue; }
            run.Write(data);
            run.WriteByte((byte)'\n');
        }
        Flush();
        if (merged.Count == contents.Elements.Count) return; // nada a juntar
        contents.Elements.Clear();
        foreach (var item in merged) contents.Elements.Add(item);
    }

    /// <summary>
    /// PDF assinado: grava só o que mudou no fim do arquivo, e a assinatura continua valendo. Abre em modo
    /// "Import" porque o modo "Modify" do PDFsharp renumera os objetos ao abrir (e a gravação no fim precisa dos
    /// números que estão no arquivo). false = não é assinado ou não deu; aí o chamador grava do jeito comum.
    /// </summary>
    private static bool TryEditIncrementally(byte[] original, string output, Action<PdfDocument> edit)
    {
        PdfDocument doc;
        try { doc = PdfReader.Open(new MemoryStream(original), PdfDocumentOpenMode.Import); }
        catch (PdfReaderException) { return false; }
        using (doc)
        {
            if (!DocumentPropertiesService.HasSignatures(doc)) return false;
            try
            {
                var before = PdfIncrementalUpdate.Snapshot(doc);
                edit(doc);
                PdfIncrementalUpdate.Write(original, doc, before, output);
                return true;
            }
            catch (NotSupportedException) { return false; } // algo que não sabemos regravar: grava do jeito comum
        }
    }

    private static PdfPage GetPage(PdfDocument doc, int index) =>
        index >= 0 && index < doc.PageCount ? doc.Pages[index] : throw new ArgumentOutOfRangeException(nameof(index), $"A página {index + 1} não existe.");

    /// <summary>Cria o dicionário do comentário com os dados comuns e o acrescenta à página.</summary>
    private static PdfDictionary NewAnnotation(PdfDocument doc, PdfPage page, string subtype, PdfRectangle rect,
        AnnotationStyle style, string author, string? contents)
    {
        var a = new PdfDictionary(doc);
        a.Elements["/Type"] = new PdfName("/Annot");
        a.Elements["/Subtype"] = new PdfName(subtype);
        a.Elements["/Rect"] = rect;
        a.Elements["/C"] = style.Color.ToPdfArray(doc);
        a.Elements["/F"] = new PdfInteger(PrintFlag);
        a.Elements["/NM"] = new PdfString(Guid.NewGuid().ToString());
        if (!string.IsNullOrWhiteSpace(author)) a.Elements["/T"] = new PdfString(author, PdfStringEncoding.Unicode);
        if (!string.IsNullOrEmpty(contents)) a.Elements["/Contents"] = new PdfString(contents, PdfStringEncoding.Unicode);
        string now = PdfDate(DateTime.Now);
        a.Elements["/M"] = new PdfString(now);
        a.Elements["/CreationDate"] = new PdfString(now);
        if (style.Opacity < 1) a.Elements["/CA"] = new PdfReal(style.Opacity);
        if (page.Reference is not null) a.Elements["/P"] = page.Reference;

        doc.Internals.AddObject(a);
        var annots = page.Elements.GetArray("/Annots");
        if (annots is null)
        {
            annots = new PdfArray(doc);
            page.Elements["/Annots"] = annots;
        }
        annots.Elements.Add(a.Reference!);
        return a;
    }

    /// <summary>
    /// Grava a aparência (/AP /N): um "desenho" em coordenadas locais (0,0 = canto inferior esquerdo do /Rect).
    /// <paramref name="matrix"/> permite desenhar "de pé" em páginas giradas.
    /// </summary>
    private static void SetAppearance(PdfDocument doc, PdfDictionary annot, double width, double height, string operators,
        double opacity = 1, PdfDictionary? resources = null, string? matrix = null)
    {
        var form = new PdfDictionary(doc);
        form.Elements["/Type"] = new PdfName("/XObject");
        form.Elements["/Subtype"] = new PdfName("/Form");
        form.Elements["/BBox"] = new PdfRectangle(new XPoint(0, 0), new XPoint(width, height));
        if (matrix is not null) form.Elements["/Matrix"] = ParseArray(doc, matrix);

        resources ??= new PdfDictionary(doc);
        if (opacity < 1)
        {
            var gs = new PdfDictionary(doc);
            gs.Elements["/Type"] = new PdfName("/ExtGState");
            gs.Elements["/CA"] = new PdfReal(opacity);
            gs.Elements["/ca"] = new PdfReal(opacity);
            var ext = resources.Elements.GetDictionary("/ExtGState") ?? new PdfDictionary(doc);
            ext.Elements["/GSa"] = gs;
            resources.Elements["/ExtGState"] = ext;
            operators = "/GSa gs " + operators;
        }
        form.Elements["/Resources"] = resources;
        form.CreateStream(Encoding.ASCII.GetBytes(operators));
        doc.Internals.AddObject(form);

        var ap = new PdfDictionary(doc);
        ap.Elements["/N"] = form.Reference!;
        annot.Elements["/AP"] = ap;
    }

    private static PdfArray ParseArray(PdfDocument doc, string numbers)
    {
        var array = new PdfArray(doc);
        foreach (var part in numbers.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            array.Elements.Add(new PdfReal(double.Parse(part, CultureInfo.InvariantCulture)));
        return array;
    }

    private static PdfDictionary BorderStyle(PdfDocument doc, double width)
    {
        var bs = new PdfDictionary(doc);
        bs.Elements["/W"] = new PdfReal(width);
        return bs;
    }

    /// <summary>Menor retângulo que contém os pontos, com uma folga (para a espessura do traço).</summary>
    private static PdfRectangle BoundingBox(IEnumerable<XPoint> points, double margin)
    {
        var list = points.ToList();
        return new PdfRectangle(
            new XPoint(list.Min(p => p.X) - margin, list.Min(p => p.Y) - margin),
            new XPoint(list.Max(p => p.X) + margin, list.Max(p => p.Y) + margin));
    }

    /// <summary>Ponto do PDF nas coordenadas locais da aparência (origem no canto inferior esquerdo do /Rect).</summary>
    private static string Local(XPoint p, PdfRectangle rect) => $"{N(p.X - rect.X1)} {N(p.Y - rect.Y1)}";

    /// <summary>Elipse dentro do retângulo, feita com 4 curvas de Bézier.</summary>
    private static string Ellipse(double x, double y, double w, double h)
    {
        const double k = 0.5522847498;
        double rx = w / 2, ry = h / 2, cx = x + rx, cy = y + ry, ox = rx * k, oy = ry * k;
        return $"{N(cx + rx)} {N(cy)} m " +
               $"{N(cx + rx)} {N(cy + oy)} {N(cx + ox)} {N(cy + ry)} {N(cx)} {N(cy + ry)} c " +
               $"{N(cx - ox)} {N(cy + ry)} {N(cx - rx)} {N(cy + oy)} {N(cx - rx)} {N(cy)} c " +
               $"{N(cx - rx)} {N(cy - oy)} {N(cx - ox)} {N(cy - ry)} {N(cx)} {N(cy - ry)} c " +
               $"{N(cx + ox)} {N(cy - ry)} {N(cx + rx)} {N(cy - oy)} {N(cx + rx)} {N(cy)} c h";
    }

    private static AnnotationKind KindOf(string subtype, PdfDictionary a) => subtype switch
    {
        "/Square" => AnnotationKind.Rectangle,
        "/Circle" => AnnotationKind.Oval,
        "/Line" => AnnotationKind.Line,
        "/Ink" => AnnotationKind.Ink,
        "/FreeText" => AnnotationKind.TextBox,
        "/Text" => AnnotationKind.Note,
        "/Highlight" => AnnotationKind.Highlight,
        "/Underline" => AnnotationKind.Underline,
        "/StrikeOut" => AnnotationKind.StrikeOut,
        "/Stamp" => AnnotationKind.Stamp,
        _ => AnnotationKind.Other,
    };

    private static AnnotationColor? ReadColor(PdfArray? c) =>
        c is { Elements.Count: 3 }
            ? new AnnotationColor(ToByte(c.Elements.GetReal(0)), ToByte(c.Elements.GetReal(1)), ToByte(c.Elements.GetReal(2)))
            : null;

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);

    /// <summary>Data no formato do PDF: D:AAAAMMDDhhmmss-03'00'.</summary>
    internal static string PdfDate(DateTime local)
    {
        var offset = TimeZoneInfo.Local.GetUtcOffset(local);
        char sign = offset < TimeSpan.Zero ? '-' : '+';
        offset = offset.Duration();
        return $"D:{local:yyyyMMddHHmmss}{sign}{offset.Hours:00}'{offset.Minutes:00}'";
    }

    private static string N(double v) => AnnotationColor.N(v);
}
