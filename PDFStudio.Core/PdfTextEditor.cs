using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;
using M = PDFStudio.Core.PdfMatrix;

namespace PDFStudio.Core;

/// <summary>Resultado da troca de texto: a fonte usada e se foi preciso cobrir o texto antigo com branco.</summary>
public sealed record TextEditResult(string FontUsed, bool Covered);

/// <summary>
/// Etapa 5.9: trocar um texto que já está escrito na página (um bloco achado por <see cref="PdfContentBlocks"/>).
/// 1) Tira do conteúdo da página os comandos que escrevem texto começando dentro do bloco.
/// 2) Se ainda sobrar texto ali (ex.: texto guardado dentro de um "formulário" do PDF), cobre a área com branco.
/// 3) Escreve o texto novo na mesma linha de base, com o mesmo espaço entre linhas, cor e tamanho.
/// </summary>
public static class PdfTextEditor
{
    /// <summary>Troca o texto do bloco por <paramref name="newText"/> (vazio = só apaga o texto antigo).</summary>
    /// <param name="fontFamily">Fonte para o texto novo; null = a original, se instalada, ou a mais parecida.</param>
    public static TextEditResult Replace(string input, int pageIndex, ContentBlock block, string newText, string output, string? fontFamily = null)
    {
        if (block.Kind != BlockKind.Text) throw new ArgumentException("O bloco escolhido não é de texto.", nameof(block));
        string font = fontFamily ?? ChooseFont(block.FontFamily);
        var style = new PageTextStyle(font, block.FontSize > 0 ? block.FontSize : 12, block.Color, block.Bold, block.Italic);
        var lines = PdfPageContent.SplitLines(newText.TrimEnd());
        var xfont = lines.Any(l => l.Length > 0) ? PdfPageContent.CreateFont(style) : null;

        // 1) Tira os comandos de texto do bloco (num arquivo temporário, para conferir o resultado).
        string temp = Path.Combine(Path.GetTempPath(), "PDFStudio", $"edit-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            PdfAnnotations.Edit(input, temp, doc => RemoveTextIn(doc.Pages[PdfPageContent.CheckPage(doc, pageIndex)], block));

            // 2) Sobrou alguma palavra antiga dentro do bloco? Então cobre com branco.
            bool cover = PdfTextLayout.Words(temp, pageIndex).Any(w =>
                block.Contains(w.X + w.Width / 2, w.Y + w.Height / 2));

            // 3) Escreve o texto novo.
            PdfAnnotations.Edit(temp, output, doc =>
            {
                var page = doc.Pages[pageIndex];
                if (!cover && xfont is null) return;
                PdfPageContent.DrawInView(page, gfx =>
                {
                    if (cover)
                        gfx.DrawRectangle(XBrushes.White, new XRect(block.X - 1, block.Y - 1, block.Width + 2, block.Height + 2));
                    if (xfont is null) return;
                    var brush = new XSolidBrush(XColor.FromArgb(block.Color.R, block.Color.G, block.Color.B));
                    double spacing = LineSpacing(block, xfont);
                    double baseline = block.Lines[0].Baseline;
                    for (int i = 0; i < lines.Count; i++)
                        if (lines[i].Length > 0)
                            gfx.DrawString(lines[i], xfont, brush, new XPoint(block.Lines[0].X, baseline + i * spacing)); // ponto = linha de base
                });
            });
            return new TextEditResult(font, cover);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* arquivo temporário: tanto faz */ }
        }
    }

    /// <summary>Espaço entre as linhas do bloco original (ou o normal da fonte, se ele tinha uma linha só).</summary>
    private static double LineSpacing(ContentBlock block, XFont font) =>
        block.Lines.Count > 1
            ? (block.Lines[^1].Baseline - block.Lines[0].Baseline) / (block.Lines.Count - 1)
            : font.GetHeight();

    /// <summary>
    /// A fonte original, se o app a tem (instalada ou baixada); senão, a gêmea que vem com o app
    /// (Calibri → Carlito…), uma parecida do Windows ou Arial. Veja <see cref="FontLibrary.Resolve"/>.
    /// </summary>
    public static string ChooseFont(string family) => FontLibrary.Resolve(family).Use;

    // ---------------- Apagar e mover blocos ----------------

    private const string CannotMove = "Este texto está gravado de um jeito que não dá para mover.";
    private const string CannotMoveImage = "Esta imagem está gravada de um jeito que não dá para mover.";
    private const string CannotDeleteImage = "Esta imagem está gravada de um jeito que não dá para apagar.";

    /// <summary>Apaga o bloco: texto (o que não der para tirar é coberto com branco) ou imagem.</summary>
    public static TextEditResult Delete(string input, int pageIndex, ContentBlock block, string output) =>
        Delete(input, pageIndex, [block], output);

    /// <summary>
    /// Apaga vários blocos (textos e imagens) de uma vez. Texto que não der para tirar de verdade (ex.: guardado
    /// dentro de outra parte do PDF) é coberto com branco; imagem que não der para tirar: erro, nada muda.
    /// </summary>
    public static TextEditResult Delete(string input, int pageIndex, IReadOnlyList<ContentBlock> blocks, string output)
    {
        if (blocks.Count == 0) throw new ArgumentException("Escolha o que apagar.", nameof(blocks));
        var texts = blocks.Where(b => b.Kind == BlockKind.Text).ToList();
        string temp = TempFile("delete");
        try
        {
            PdfAnnotations.Edit(input, temp, doc =>
            {
                var page = doc.Pages[PdfPageContent.CheckPage(doc, pageIndex)];
                var streams = Walk(page, blocks);
                int images = streams.Sum(st => st.Places?.Count ?? 0);
                if (images < blocks.Count(b => b.Kind == BlockKind.Image)) throw new InvalidOperationException(CannotDeleteImage);
                RemoveHits(streams);
            });

            // Sobrou palavra de algum texto? Cobre esse texto com branco.
            var words = texts.Count == 0 ? [] : PdfTextLayout.Words(temp, pageIndex);
            var cover = texts.Where(t => words.Any(w => t.Contains(w.X + w.Width / 2, w.Y + w.Height / 2))).ToList();
            if (cover.Count == 0)
            {
                File.Copy(temp, output, overwrite: true);
                return new TextEditResult("", false);
            }
            PdfAnnotations.Edit(temp, output, doc => PdfPageContent.DrawInView(doc.Pages[pageIndex], gfx =>
            {
                foreach (var t in cover)
                    gfx.DrawRectangle(XBrushes.White, new XRect(t.X - 1, t.Y - 1, t.Width + 2, t.Height + 2));
            }));
            return new TextEditResult("", true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* arquivo temporário: tanto faz */ }
        }
    }

    /// <summary>Move um bloco (texto ou imagem). Veja <see cref="Move(string, int, IReadOnlyList{ContentBlock}, double, double, string)"/>.</summary>
    public static void Move(string input, int pageIndex, ContentBlock block, double dx, double dy, string output) =>
        Move(input, pageIndex, [block], dx, dy, output);

    /// <summary>
    /// Move os blocos (textos e imagens) <paramref name="dx"/>, <paramref name="dy"/> pontos (na tela: x para a
    /// direita, y para baixo), todos juntos: a distância entre eles não muda. Nada é escrito de novo: o texto continua
    /// com a mesma fonte do PDF, letras, cor e espaçamento (os comandos que o escrevem são desenhados deslocados, dentro
    /// de um "formulário" do PDF) e a imagem continua a mesma. Se algum pedaço não puder ser movido, nada muda e dá erro.
    /// </summary>
    public static void Move(string input, int pageIndex, IReadOnlyList<ContentBlock> blocks, double dx, double dy, string output)
    {
        if (blocks.Count == 0) throw new ArgumentException("Escolha o que mover.", nameof(blocks));
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            if (!SamePath(input, output)) File.Copy(input, output, overwrite: true);
            return;
        }
        var texts = blocks.Where(b => b.Kind == BlockKind.Text).ToList();
        var images = blocks.Where(b => b.Kind == BlockKind.Image).ToList();
        string temp = TempFile("move");
        try
        {
            PdfAnnotations.Edit(input, temp, doc =>
            {
                var page = doc.Pages[PdfPageContent.CheckPage(doc, pageIndex)];
                if (images.Count > 0) MoveImagesIn(page, images, dx, dy);
                if (texts.Count > 0) MoveTextsIn(page, texts, dx, dy);
            });
            // Conferência: no lugar antigo não pode sobrar palavra dos textos (fora da área para onde eles foram).
            if (texts.Count > 0)
            {
                var moved = texts.Select(t => t with { X = t.X + dx, Y = t.Y + dy }).ToList();
                bool leftover = PdfTextLayout.Words(temp, pageIndex).Any(w =>
                {
                    double cx = w.X + w.Width / 2, cy = w.Y + w.Height / 2;
                    return texts.Any(t => t.Contains(cx, cy)) && !moved.Any(t => t.Contains(cx, cy));
                });
                if (leftover) throw new InvalidOperationException(CannotMove);
            }
            File.Copy(temp, output, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* arquivo temporário: tanto faz */ }
        }
    }

    private static string TempFile(string kind)
    {
        string temp = Path.Combine(Path.GetTempPath(), "PDFStudio", $"{kind}-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        return temp;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>O deslocamento da tela (dx, dy) em pontos do PDF, já com a rotação da página.</summary>
    private static (double X, double Y) PdfShift(PdfPage page, double dx, double dy)
    {
        var geometry = PageGeometry.Of(page);
        var from = geometry.ViewToPdf(0, 0);
        var to = geometry.ViewToPdf(dx, dy);
        return (to.X - from.X, to.Y - from.Y);
    }

    /// <summary>
    /// Move as imagens: o comando que desenha cada uma ganha um deslocamento ("q … cm /Imagem Do Q"). A imagem em si
    /// (o arquivo dentro do PDF) não muda. Imagem que não está desenhada direto na página: erro.
    /// </summary>
    private static void MoveImagesIn(PdfPage page, IReadOnlyList<ContentBlock> images, double dx, double dy)
    {
        var streams = Walk(page, images).Where(s => s.Seq is not null && s.Move is null && s.InBlock.Count > 0).ToList();
        if (streams.Sum(s => s.InBlock.Count) < images.Count) throw new InvalidOperationException(CannotMoveImage);
        var (tx, ty) = PdfShift(page, dx, dy);
        foreach (var s in streams)
        {
            for (int k = s.InBlock.Count - 1; k >= 0; k--)
            {
                int i = s.InBlock[k];
                var ctm = s.Places![i];
                // Deslocamento na página (tx, ty) escrito nas medidas de onde a imagem é desenhada: resolve a parte
                // linear da matriz atual (o "cm" novo é aplicado antes dela).
                double det = ctm.A * ctm.D - ctm.B * ctm.C;
                if (Math.Abs(det) < 1e-12) throw new InvalidOperationException(CannotMoveImage);
                double lx = (tx * ctm.D - ty * ctm.C) / det, ly = (ty * ctm.A - tx * ctm.B) / det;
                var shift = OpCodes.OperatorFromName("cm");
                foreach (double v in new[] { 1.0, 0, 0, 1, lx, ly }) shift.Operands.Add(new CReal { Value = v });
                s.Seq!.Insert(i + 1, OpCodes.OperatorFromName("Q"));
                s.Seq.Insert(i, shift);
                s.Seq.Insert(i, OpCodes.OperatorFromName("q"));
            }
            Rewrite(s);
        }
    }

    private static string Num(double v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);

    private static void MoveTextsIn(PdfPage page, IReadOnlyList<ContentBlock> texts, double dx, double dy)
    {
        var streams = Walk(page, texts);
        if (streams.Any(s => s.Seq is null && !s.AppArtifact)) throw new InvalidOperationException(CannotMove);
        var (tx, ty) = PdfShift(page, dx, dy);
        var hits = streams.Where(s => s.Seq is not null && s.InBlock.Count > 0).ToList();
        if (hits.Count == 0) throw new InvalidOperationException(CannotMove);

        // Só textos já movidos antes, cada um com o formulário inteiro escolhido: muda só o deslocamento deles.
        if (hits.All(s => s.Move is not null && s.InBlock.Count == s.TextOps))
        {
            foreach (var s in hits)
            {
                var (pageStream, cm, _) = s.Move!.Value;
                var args = cm.Operands;
                args[4] = new CReal { Value = N(args[4]) + tx };
                args[5] = new CReal { Value = N(args[5]) + ty };
                Rewrite(streams.First(o => ReferenceEquals(o.Stream, pageStream)));
            }
            return;
        }

        // 1) O "formulário" com só os textos escolhidos: o conteúdo da página inteiro (para valerem as mesmas fontes,
        // cores, posições e recortes), sem desenhos, imagens e sem os outros textos. Textos que já estavam num
        // formulário de mover entram com a posição em que eram desenhados.
        var form = new StringBuilder("q\n");
        foreach (var s in streams.Where(s => !s.AppArtifact && s.Seq is not null && s.Move is null))
            form.Append(OnlyText(s.Seq!, s.InBlock)).Append('\n');
        form.Append("Q\n");
        foreach (var s in hits.Where(s => s.Move is not null))
        {
            var at = s.Move!.Value.At;
            form.Append($"q {Num(at.A)} {Num(at.B)} {Num(at.C)} {Num(at.D)} {Num(at.E)} {Num(at.F)} cm\n")
                .Append(OnlyText(s.Seq!, s.InBlock)).Append("\nQ\n");
        }

        // 2) Tira os textos do lugar antigo.
        RemoveHits(streams);

        // 3) Desenha o formulário deslocado.
        var doc = page.Owner;
        var xobject = new PdfDictionary(doc);
        xobject.Elements.SetName("/Type", "/XObject");
        xobject.Elements.SetName("/Subtype", "/Form");
        xobject.Elements["/BBox"] = new PdfArray(doc, new PdfReal(-20000), new PdfReal(-20000), new PdfReal(20000), new PdfReal(20000));
        if (page.Elements["/Resources"] is { } resources) xobject.Elements["/Resources"] = resources;
        xobject.CreateStream(Encoding.Latin1.GetBytes(form.ToString()));
        doc.Internals.AddObject(xobject);

        var xobjects = page.Resources.Elements.GetDictionary("/XObject");
        if (xobjects is null)
        {
            xobjects = new PdfDictionary(doc);
            page.Resources.Elements["/XObject"] = xobjects;
        }
        string name;
        int n = 1;
        do name = $"/PSMove{n++}"; while (xobjects.Elements.ContainsKey(name));
        xobjects.Elements[name] = xobject.Reference;

        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes($"Q\nq 1 0 0 1 {Num(tx)} {Num(ty)} cm {name} Do Q\n"));
    }

    /// <summary>O conteúdo com só os comandos de texto escolhidos (o resto do texto sai; desenhos e imagens também).</summary>
    private static string OnlyText(CSequence seq, List<int> chosen)
    {
        var keep = chosen.ToHashSet();
        var kept = new CSequence();
        for (int i = 0; i < seq.Count; i++)
        {
            if (seq[i] is not COperator op) { kept.Add(seq[i]); continue; }
            switch (op.OpCode.OpCodeName)
            {
                case OpCodeName.Tj or OpCodeName.TJ or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble:
                    if (keep.Contains(i)) kept.Add(op);
                    else if (op.OpCode.OpCodeName is OpCodeName.QuoteSingle or OpCodeName.QuoteDouble)
                        kept.Add(OpCodes.OperatorFromName("T*")); // a mudança de linha continua valendo
                    break;
                case OpCodeName.S or OpCodeName.s or OpCodeName.f or OpCodeName.F or OpCodeName.fx
                    or OpCodeName.B or OpCodeName.Bx or OpCodeName.b or OpCodeName.bx:
                    kept.Add(OpCodes.OperatorFromName("n")); // o caminho termina sem pintar (o recorte continua)
                    break;
                case OpCodeName.Do or OpCodeName.sh or OpCodeName.BI or OpCodeName.ID or OpCodeName.EI:
                    break;
                default:
                    kept.Add(op);
                    break;
            }
        }
        return Encoding.Latin1.GetString(kept.ToContent());
    }

    // ---------------- Achar os comandos dos blocos ----------------

    /// <summary>
    /// Um trecho do conteúdo da página (ou do formulário de um texto já movido, com <see cref="Move"/>: o trecho da
    /// página que o desenha, o comando de deslocamento e a posição em que é desenhado), já lido, com os comandos que
    /// desenham os blocos (texto que começa dentro de um bloco de texto; imagem que ocupa um bloco de imagem).
    /// </summary>
    private sealed record StreamOps(PdfDictionary Stream, CSequence? Seq, List<int> InBlock, bool AppArtifact,
        int TextOps = 0, (PdfDictionary PageStream, COperator Cm, M At)? Move = null, Dictionary<int, M>? Places = null);

    /// <summary>Grava de volta um trecho alterado (sem compactação; o salvamento compacta de novo).</summary>
    private static void Rewrite(StreamOps s)
    {
        s.Stream.Elements.Remove("/Filter");
        s.Stream.Elements.Remove("/DecodeParms");
        s.Stream.Stream.Value = s.Seq!.ToContent();
    }

    /// <summary>Apaga os comandos achados. Devolve quantos saíram.</summary>
    private static int RemoveHits(List<StreamOps> streams)
    {
        int removed = 0;
        foreach (var s in streams)
        {
            if (s.Seq is null || s.AppArtifact || s.InBlock.Count == 0) continue;
            for (int k = s.InBlock.Count - 1; k >= 0; k--) s.Seq.RemoveAt(s.InBlock[k]);
            Rewrite(s);
            removed += s.InBlock.Count;
        }
        return removed;
    }

    /// <summary>Apaga da página os comandos de texto que começam dentro do bloco. Devolve quantos saíram.</summary>
    internal static int RemoveTextIn(PdfPage page, ContentBlock block) => RemoveHits(Walk(page, [block]));

    /// <summary>
    /// Percorre os comandos de todos os trechos do conteúdo (acompanhando a posição do texto e as transformações)
    /// e anota os que desenham os blocos. Trechos colocados pelo app (marca d'água, cabeçalho…) são lidos, mas nada
    /// deles é anotado. Trecho que o PDFsharp não entende: Seq = null. Os formulários de textos já movidos pelo app
    /// (/PSMove…) também são percorridos, no lugar onde são desenhados.
    /// </summary>
    private static List<StreamOps> Walk(PdfPage page, IReadOnlyList<ContentBlock> blocks)
    {
        var result = new List<StreamOps>();
        if (page.Elements["/Contents"] is null) return result;
        var texts = blocks.Where(b => b.Kind == BlockKind.Text).ToList();
        var images = blocks.Where(b => b.Kind == BlockKind.Image).ToList();
        var geometry = PageGeometry.Of(page);
        var contents = page.Contents.Elements;
        var xobjects = page.Resources.Elements.GetDictionary("/XObject");
        var ctm = M.Identity;
        var stack = new Stack<M>();
        M tm = M.Identity, tlm = M.Identity;
        double leading = 0;
        bool lineInBlock = false, positioned = true;
        var forms = new List<(PdfDictionary Form, M Ctm, PdfDictionary PageStream, COperator Cm)>();

        bool StartsInBlock()
        {
            var (x, y) = tm.Then(ctm).Origin;
            var view = geometry.PdfToView(x, y);
            double vx = view.X, vy = view.Y;
            return texts.Any(b => vx >= b.X - 1 && vx <= b.Right + 1 && vy >= b.Y - 1 && vy <= b.Bottom + 1);
        }
        void MoveLine(double tx, double ty)
        {
            tlm = new M(1, 0, 0, 1, tx, ty).Then(tlm);
            tm = tlm;
            positioned = true;
        }
        bool ImageInBlock()
        {
            // Imagem = quadrado 1 × 1 transformado pela matriz atual; confere se ocupa a mesma área de um bloco.
            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
            foreach (var (ux, uy) in new[] { (0.0, 0.0), (1.0, 0.0), (0.0, 1.0), (1.0, 1.0) })
            {
                var (px, py) = ctm.Apply(ux, uy);
                var v = geometry.PdfToView(px, py);
                l = Math.Min(l, v.X); r = Math.Max(r, v.X); t = Math.Min(t, v.Y); b = Math.Max(b, v.Y);
            }
            const double tolerance = 2;
            return images.Any(i => Math.Abs(l - i.X) < tolerance && Math.Abs(t - i.Y) < tolerance
                && Math.Abs(r - i.Right) < tolerance && Math.Abs(b - i.Bottom) < tolerance);
        }
        (List<int> InBlock, int TextOps, Dictionary<int, M> Places) Scan(CSequence seq, bool appArtifact, PdfDictionary? pageStream)
        {
            var places = new Dictionary<int, M>();
            var inBlock = new List<int>();
            int textOps = 0;
            for (int i = 0; i < seq.Count; i++)
            {
                if (seq[i] is not COperator op) continue;
                var args = op.Operands;
                switch (op.OpCode.OpCodeName)
                {
                    case OpCodeName.q: stack.Push(ctm); break;
                    case OpCodeName.Q: if (stack.Count > 0) ctm = stack.Pop(); break;
                    case OpCodeName.cm when args.Count == 6:
                        ctm = new M(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5])).Then(ctm);
                        break;
                    case OpCodeName.BT: tm = tlm = M.Identity; positioned = true; break;
                    case OpCodeName.Tm when args.Count == 6:
                        tm = tlm = new M(N(args[0]), N(args[1]), N(args[2]), N(args[3]), N(args[4]), N(args[5]));
                        positioned = true;
                        break;
                    case OpCodeName.Td when args.Count == 2: MoveLine(N(args[0]), N(args[1])); break;
                    case OpCodeName.TD when args.Count == 2: leading = -N(args[1]); MoveLine(N(args[0]), N(args[1])); break;
                    case OpCodeName.TL when args.Count == 1: leading = N(args[0]); break;
                    case OpCodeName.Tx: MoveLine(0, -leading); break; // T*
                    case OpCodeName.Tj or OpCodeName.TJ or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble:
                        if (op.OpCode.OpCodeName is OpCodeName.QuoteSingle or OpCodeName.QuoteDouble) MoveLine(0, -leading);
                        // Vários comandos seguidos na mesma linha (sem reposicionar) seguem a decisão do primeiro.
                        if (positioned) { lineInBlock = texts.Count > 0 && StartsInBlock(); positioned = false; }
                        if (lineInBlock && !appArtifact) inBlock.Add(i);
                        textOps++;
                        break;
                    // Texto já movido: "q 1 0 0 1 x y cm /PSMove1 Do Q".
                    case OpCodeName.Do when pageStream is not null && args.Count == 1 && args[0] is CName { Name: var name }
                        && name.StartsWith("/PSMove", StringComparison.Ordinal) && i > 0 && seq[i - 1] is COperator { OpCode.OpCodeName: OpCodeName.cm } cm
                        && cm.Operands.Count == 6 && xobjects?.Elements.GetDictionary(name) is { Stream: not null } form:
                        forms.Add((form, ctm, pageStream, cm));
                        break;
                    // Imagem de um bloco (só as desenhadas direto na página).
                    case OpCodeName.Do when images.Count > 0 && !appArtifact && pageStream is not null && args.Count == 1
                        && args[0] is CName { Name: var image } && xobjects?.Elements.GetDictionary(image) is { } x
                        && x.Elements.GetName("/Subtype") == "/Image" && ImageInBlock():
                        inBlock.Add(i);
                        places[i] = ctm;
                        break;
                }
            }
            return (inBlock, textOps, places);
        }

        for (int s = 0; s < contents.Count; s++)
        {
            if (contents.GetDictionary(s) is not { Stream: not null } stream) continue;
            byte[] data = stream.Stream.UnfilteredValue;
            bool appArtifact = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 64)).TrimStart('q', ' ', '\r', '\n', '\t').StartsWith("/Artifact <</PDFStudio");
            CSequence seq;
            try { seq = ContentReader.ReadContent(data); }
            catch (ContentReaderException)
            {
                result.Add(new StreamOps(stream, null, [], appArtifact)); // trecho que o PDFsharp não entende: fica como está
                continue;
            }
            var (inBlock, textOps, places) = Scan(seq, appArtifact, stream);
            result.Add(new StreamOps(stream, seq, inBlock, appArtifact, textOps, Places: places));
        }

        foreach (var (form, at, pageStream, cm) in forms)
        {
            CSequence seq;
            try { seq = ContentReader.ReadContent(form.Stream.UnfilteredValue); }
            catch (ContentReaderException) { continue; }
            ctm = at;
            stack.Clear();
            tm = tlm = M.Identity;
            positioned = true;
            var (inBlock, textOps, _) = Scan(seq, false, null);
            result.Add(new StreamOps(form, seq, inBlock, false, textOps, (pageStream, cm, at)));
        }
        return result;
    }

    private static double N(CObject o) => o switch
    {
        CInteger i => i.Value,
        CReal r => r.Value,
        _ => 0,
    };
}
