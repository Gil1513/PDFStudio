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

    // ---------------- Apagar e mover o bloco ----------------

    /// <summary>Apaga o texto do bloco da página (o que não der para tirar é coberto com branco).</summary>
    public static TextEditResult Delete(string input, int pageIndex, ContentBlock block, string output) =>
        Replace(input, pageIndex, block, "", output);

    /// <summary>
    /// Move o texto do bloco <paramref name="dx"/>, <paramref name="dy"/> pontos (na tela: x para a direita, y para
    /// baixo). O texto continua o mesmo de antes (mesma fonte do PDF, letras, cor, espaçamento): os comandos que o
    /// escrevem saem do lugar antigo e são desenhados de novo, deslocados, dentro de um "formulário" do PDF.
    /// Se algum pedaço do texto não puder ser movido, nada muda e dá erro.
    /// </summary>
    public static void Move(string input, int pageIndex, ContentBlock block, double dx, double dy, string output)
    {
        if (block.Kind != BlockKind.Text) throw new ArgumentException("O bloco escolhido não é de texto.", nameof(block));
        if (Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            if (!SamePath(input, output)) File.Copy(input, output, overwrite: true);
            return;
        }
        string temp = Path.Combine(Path.GetTempPath(), "PDFStudio", $"move-{Guid.NewGuid():N}.pdf");
        Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
        try
        {
            PdfAnnotations.Edit(input, temp, doc => MoveIn(doc.Pages[PdfPageContent.CheckPage(doc, pageIndex)], block, dx, dy));
            // Conferência: no lugar antigo não pode sobrar palavra do bloco (fora da área para onde ele foi).
            var moved = block with { X = block.X + dx, Y = block.Y + dy };
            bool leftover = PdfTextLayout.Words(temp, pageIndex).Any(w =>
            {
                double cx = w.X + w.Width / 2, cy = w.Y + w.Height / 2;
                return block.Contains(cx, cy) && !moved.Contains(cx, cy);
            });
            if (leftover) throw new InvalidOperationException(CannotMove);
            File.Copy(temp, output, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* arquivo temporário: tanto faz */ }
        }
    }

    private const string CannotMove = "Este texto está gravado de um jeito que não dá para mover.";

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    private static void MoveIn(PdfPage page, ContentBlock block, double dx, double dy)
    {
        var streams = Walk(page, block);
        if (streams.Any(s => s.Seq is null && !s.AppArtifact)) throw new InvalidOperationException(CannotMove);
        var geometry = PageGeometry.Of(page);
        var from = geometry.ViewToPdf(block.X, block.Y);
        var to = geometry.ViewToPdf(block.X + dx, block.Y + dy);
        static string Num(double v) => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

        bool inPage = streams.Any(s => s.Move is null && s.InBlock.Count > 0);
        var moved = streams.Where(s => s.Move is not null && s.InBlock.Count > 0).ToList();
        if (!inPage && moved.Count == 0) throw new InvalidOperationException(CannotMove);
        if (moved.Count > 0)
        {
            // Texto que já foi movido antes: muda só o deslocamento do formulário dele (se o formulário for só desse bloco).
            if (inPage || moved.Any(s => s.InBlock.Count != s.TextOps)) throw new InvalidOperationException(CannotMove);
            foreach (var s in moved)
            {
                var (pageStream, cm) = s.Move!.Value;
                var args = cm.Operands;
                double tx = N(args[4]) + to.X - from.X, ty = N(args[5]) + to.Y - from.Y;
                args[4] = new CReal { Value = tx };
                args[5] = new CReal { Value = ty };
                var owner = streams.First(o => ReferenceEquals(o.Stream, pageStream));
                pageStream.Elements.Remove("/Filter");
                pageStream.Elements.Remove("/DecodeParms");
                pageStream.Stream.Value = owner.Seq!.ToContent();
            }
            return;
        }

        // 1) O "formulário" com só o texto do bloco: o conteúdo da página inteiro (para valerem as mesmas fontes,
        // cores, posições e recortes), sem desenhos, imagens e sem os outros textos.
        var form = new StringBuilder();
        foreach (var s in streams)
        {
            if (s.AppArtifact || s.Seq is null || s.Move is not null) continue;
            var inBlock = s.InBlock.ToHashSet();
            var kept = new CSequence();
            for (int i = 0; i < s.Seq.Count; i++)
            {
                if (s.Seq[i] is not COperator op) { kept.Add(s.Seq[i]); continue; }
                switch (op.OpCode.OpCodeName)
                {
                    case OpCodeName.Tj or OpCodeName.TJ or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble:
                        if (inBlock.Contains(i)) kept.Add(op);
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
            form.Append(Encoding.Latin1.GetString(kept.ToContent())).Append('\n');
        }

        // 2) Tira o texto do lugar antigo.
        RemoveTextIn(page, block);

        // 3) Desenha o formulário deslocado (o deslocamento da tela vira deslocamento no PDF, com a rotação da página).
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
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(
            $"Q\nq 1 0 0 1 {Num(to.X - from.X)} {Num(to.Y - from.Y)} cm {name} Do Q\n"));
    }

    // ---------------- Tirar os comandos de texto do bloco ----------------

    /// <summary>
    /// Um trecho do conteúdo da página (ou do formulário de um bloco já movido, com <see cref="Move"/>: o trecho da
    /// página que o desenha e o comando de deslocamento), já lido, com os comandos de texto que começam dentro do bloco.
    /// </summary>
    private sealed record StreamOps(PdfDictionary Stream, CSequence? Seq, List<int> InBlock, bool AppArtifact,
        int TextOps = 0, (PdfDictionary PageStream, COperator Cm)? Move = null);

    /// <summary>Apaga da página os comandos de texto que começam dentro do bloco. Devolve quantos saíram.</summary>
    internal static int RemoveTextIn(PdfPage page, ContentBlock block)
    {
        int removed = 0;
        foreach (var s in Walk(page, block))
        {
            if (s.Seq is null || s.AppArtifact || s.InBlock.Count == 0) continue;
            for (int k = s.InBlock.Count - 1; k >= 0; k--) s.Seq.RemoveAt(s.InBlock[k]);
            s.Stream.Elements.Remove("/Filter");
            s.Stream.Elements.Remove("/DecodeParms");
            s.Stream.Stream.Value = s.Seq.ToContent();
            removed += s.InBlock.Count;
        }
        return removed;
    }

    /// <summary>
    /// Percorre os comandos de todos os trechos do conteúdo (acompanhando a posição do texto e as transformações)
    /// e anota os que escrevem texto começando dentro do bloco. Trechos colocados pelo app (marca d'água,
    /// cabeçalho…) são lidos, mas nada deles é anotado. Trecho que o PDFsharp não entende: Seq = null.
    /// Os formulários de blocos já movidos pelo app (/PSMove…) também são percorridos, no lugar onde são desenhados.
    /// </summary>
    private static List<StreamOps> Walk(PdfPage page, ContentBlock block)
    {
        var result = new List<StreamOps>();
        if (page.Elements["/Contents"] is null) return result;
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
            return vx >= block.X - 1 && vx <= block.Right + 1 && vy >= block.Y - 1 && vy <= block.Bottom + 1;
        }
        void MoveLine(double tx, double ty)
        {
            tlm = new M(1, 0, 0, 1, tx, ty).Then(tlm);
            tm = tlm;
            positioned = true;
        }
        (List<int> InBlock, int TextOps) Scan(CSequence seq, bool appArtifact, PdfDictionary? pageStream)
        {
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
                        if (positioned) { lineInBlock = StartsInBlock(); positioned = false; }
                        if (lineInBlock && !appArtifact) inBlock.Add(i);
                        textOps++;
                        break;
                    // Bloco já movido: "q 1 0 0 1 x y cm /PSMove1 Do Q".
                    case OpCodeName.Do when pageStream is not null && args.Count == 1 && args[0] is CName { Name: var name }
                        && name.StartsWith("/PSMove", StringComparison.Ordinal) && i > 0 && seq[i - 1] is COperator { OpCode.OpCodeName: OpCodeName.cm } cm
                        && cm.Operands.Count == 6 && xobjects?.Elements.GetDictionary(name) is { Stream: not null } form:
                        forms.Add((form, ctm, pageStream, cm));
                        break;
                }
            }
            return (inBlock, textOps);
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
            var (inBlock, textOps) = Scan(seq, appArtifact, stream);
            result.Add(new StreamOps(stream, seq, inBlock, appArtifact, textOps));
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
            var (inBlock, textOps) = Scan(seq, false, null);
            result.Add(new StreamOps(form, seq, inBlock, false, textOps, (pageStream, cm)));
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
