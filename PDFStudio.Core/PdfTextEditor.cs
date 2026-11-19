using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Content;
using PdfSharp.Pdf.Content.Objects;

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

    // ---------------- Tirar os comandos de texto do bloco ----------------

    /// <summary>Matriz 2D do PDF [a b c d e f]; um ponto (x, y) vira (a·x + c·y + e, b·x + d·y + f).</summary>
    private readonly record struct M(double A, double B, double C, double D, double E, double F)
    {
        public static readonly M Identity = new(1, 0, 0, 1, 0, 0);
        /// <summary>Primeiro esta, depois <paramref name="o"/> (como o PDF faz: "this × o").</summary>
        public M Then(M o) => new(A * o.A + B * o.C, A * o.B + B * o.D, C * o.A + D * o.C, C * o.B + D * o.D,
            E * o.A + F * o.C + o.E, E * o.B + F * o.D + o.F);
        public (double X, double Y) Origin => (E, F);
    }

    /// <summary>
    /// Percorre os comandos de todos os trechos do conteúdo (acompanhando a posição do texto e as transformações)
    /// e apaga os que escrevem texto começando dentro do bloco. Trechos colocados pelo app (marca d'água,
    /// cabeçalho…) são lidos, mas não alterados. Devolve quantos comandos saíram.
    /// </summary>
    internal static int RemoveTextIn(PdfPage page, ContentBlock block)
    {
        if (page.Elements["/Contents"] is null) return 0;
        var geometry = PageGeometry.Of(page);
        var contents = page.Contents.Elements;
        var ctm = M.Identity;
        var stack = new Stack<M>();
        M tm = M.Identity, tlm = M.Identity;
        double leading = 0;
        bool lineInBlock = false, positioned = true;
        int removed = 0;

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

        for (int s = 0; s < contents.Count; s++)
        {
            if (contents.GetDictionary(s) is not { Stream: not null } stream) continue;
            byte[] data = stream.Stream.UnfilteredValue;
            bool appArtifact = Encoding.Latin1.GetString(data, 0, Math.Min(data.Length, 64)).TrimStart('q', ' ', '\r', '\n', '\t').StartsWith("/Artifact <</PDFStudio");
            CSequence seq;
            try { seq = ContentReader.ReadContent(data); }
            catch (ContentReaderException) { continue; } // trecho que o PDFsharp não entende: fica como está
            var drop = new List<int>();
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
                        if (lineInBlock && !appArtifact) drop.Add(i);
                        break;
                }
            }
            if (drop.Count == 0) continue;
            for (int k = drop.Count - 1; k >= 0; k--) seq.RemoveAt(drop[k]);
            stream.Elements.Remove("/Filter");
            stream.Elements.Remove("/DecodeParms");
            stream.Stream.Value = seq.ToContent();
            removed += drop.Count;
        }
        return removed;
    }

    private static double N(CObject o) => o switch
    {
        CInteger i => i.Value,
        CReal r => r.Value,
        _ => 0,
    };
}
