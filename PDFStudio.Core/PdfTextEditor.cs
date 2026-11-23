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
    private const string CannotMoveObject = "Algum dos itens está gravado de um jeito que não dá para mover.";
    private const string CannotMoveToRotated = "As duas páginas estão giradas de jeitos diferentes. Gire uma delas igual à outra e tente de novo.";
    private const string CannotCopy = "Não deu para copiar: o original mudou depois de copiado ou está gravado de um jeito que não dá para copiar. Copie de novo.";
    private const string CannotDeleteObject = "Algum dos itens está gravado de um jeito que não dá para apagar.";

    // ---------------- Desenhos (linhas, traços, retângulos…) ----------------

    /// <summary>
    /// Os desenhos da página (cada comando que pinta um caminho: linha, traço, retângulo, caixa…), como blocos do
    /// tipo <see cref="BlockKind.Shape"/>, na posição da tela. Fundos que cobrem quase a página inteira ficam de fora.
    /// Desenhos iguais no mesmo lugar (ex.: preenchimento e contorno) viram um bloco só. PDF ilegível: lista vazia.
    /// </summary>
    public static List<ContentBlock> Shapes(string path, int pageIndex)
    {
        try
        {
            using var doc = PdfLoad.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
            if (pageIndex < 0 || pageIndex >= doc.PageCount) return [];
            var page = doc.Pages[pageIndex];
            var geometry = PageGeometry.Of(page);
            double pageArea = geometry.ViewWidth * geometry.ViewHeight;
            var result = new List<ContentBlock>();
            Walk(page, [], (x, y, w, h) =>
            {
                if (w < 0.5 && h < 0.5) return; // ponto solto
                if (w * h > pageArea * 0.6) return; // fundo da página
                if (result.Any(b => Math.Abs(b.X - x) < 0.5 && Math.Abs(b.Y - y) < 0.5 && Math.Abs(b.Width - w) < 0.5 && Math.Abs(b.Height - h) < 0.5)) return;
                result.Add(new ContentBlock(BlockKind.Shape, x, y, w, h, []));
            });
            return result;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

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
                int objects = streams.Sum(st => st.Places?.Count ?? 0);
                int wanted = blocks.Count(b => b.Kind != BlockKind.Text);
                if (objects < wanted)
                    throw new InvalidOperationException(wanted == 1 && blocks.Any(b => b.Kind == BlockKind.Image) ? CannotDeleteImage : CannotDeleteObject);
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
        var objects = blocks.Where(b => b.Kind != BlockKind.Text).ToList();
        string temp = TempFile("move");
        try
        {
            PdfAnnotations.Edit(input, temp, doc =>
            {
                var page = doc.Pages[PdfPageContent.CheckPage(doc, pageIndex)];
                if (objects.Count > 0) MoveObjectsIn(page, objects, dx, dy);
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

    /// <summary>
    /// Leva os blocos (textos, imagens e desenhos) da página <paramref name="fromPage"/> para a página
    /// <paramref name="toPage"/>. Cada bloco vai para (X + <paramref name="dx"/>, Y + <paramref name="dy"/>) na página
    /// de destino (pontos da tela); a distância entre eles não muda. Como no mover dentro da página, nada é escrito de
    /// novo: os mesmos comandos passam a ser desenhados na outra página (dentro de um "formulário" do PDF).
    /// </summary>
    public static void MoveToPage(string input, int fromPage, IReadOnlyList<ContentBlock> blocks, int toPage, double dx, double dy, string output)
    {
        if (fromPage == toPage) { Move(input, fromPage, blocks, dx, dy, output); return; }
        DrawOnPage(input, fromPage, blocks, toPage, dx, dy, output, keepOriginal: false);
    }

    /// <summary>
    /// Copia os blocos (textos, imagens e desenhos) da página <paramref name="fromPage"/> para a página
    /// <paramref name="toPage"/> (pode ser a mesma), em (X + <paramref name="dx"/>, Y + <paramref name="dy"/>). O original
    /// fica onde está; a cópia usa os mesmos comandos, então tem a mesma letra, tamanho, cor e espaçamento.
    /// </summary>
    public static void CopyToPage(string input, int fromPage, IReadOnlyList<ContentBlock> blocks, int toPage, double dx, double dy, string output) =>
        DrawOnPage(input, fromPage, blocks, toPage, dx, dy, output, keepOriginal: true);

    private static void DrawOnPage(string input, int fromPage, IReadOnlyList<ContentBlock> blocks, int toPage, double dx, double dy, string output, bool keepOriginal)
    {
        if (blocks.Count == 0) throw new ArgumentException(keepOriginal ? "Escolha o que copiar." : "Escolha o que mover.", nameof(blocks));
        string cannot = keepOriginal ? CannotCopy : CannotMove;
        var texts = blocks.Where(b => b.Kind == BlockKind.Text).ToList();
        int objectCount = blocks.Count - texts.Count;
        string temp = TempFile("move");
        try
        {
            PdfAnnotations.Edit(input, temp, doc =>
            {
                var source = doc.Pages[PdfPageContent.CheckPage(doc, fromPage)];
                var target = doc.Pages[PdfPageContent.CheckPage(doc, toPage)];
                var from = PageGeometry.Of(source);
                var to = PageGeometry.Of(target);
                if (from.Rotation != to.Rotation) throw new InvalidOperationException(CannotMoveToRotated);

                var streams = Walk(source, blocks);
                if (streams.Any(s => s.Seq is null && !s.AppArtifact)) throw new InvalidOperationException(cannot);
                var hits = streams.Where(s => s.Seq is not null && s.InBlock.Count > 0).ToList();
                if (hits.Count == 0) throw new InvalidOperationException(cannot);
                int objectHits = hits.Sum(s => s.InBlock.Count(i => s.Seq![i] is COperator { OpCode.OpCodeName: var op } && (op == OpCodeName.Do || IsPaint(op))));
                if (objectHits < objectCount) throw new InvalidOperationException(keepOriginal ? CannotCopy : CannotMoveObject);
                // Texto copiado: todas as palavras dele precisam estar entre os comandos achados.
                if (keepOriginal && texts.Count > 0 && !hits.Any(s => s.InBlock.Any(i => s.Seq![i] is COperator { OpCode.OpCodeName: OpCodeName.Tj or OpCodeName.TJ or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble })))
                    throw new InvalidOperationException(CannotCopy);

                // 1) Formulário com só o que foi escolhido, nas posições da página de origem.
                var forms = BuildForms(doc, source.Elements["/Resources"], streams, hits, OnlyChosen);

                // 2) Mover: tira da página de origem. Copiar: o original fica.
                if (!keepOriginal) RemoveHits(streams);

                // 3) Desenha na página de destino, deslocado: um ponto da origem vai para o lugar escolhido no destino.
                var b0 = blocks[0];
                var a = from.ViewToPdf(b0.X, b0.Y);
                var z = to.ViewToPdf(b0.X + dx, b0.Y + dy);
                DrawForms(target, forms, z.X - a.X, z.Y - a.Y);
            });
            // Conferência: ao mover, na página de origem não pode sobrar palavra dos textos movidos; ao copiar, a cópia
            // precisa ter o mesmo texto do original.
            if (!keepOriginal && texts.Count > 0 && PdfTextLayout.Words(temp, fromPage).Any(w =>
                    texts.Any(t => t.Contains(w.X + w.Width / 2, w.Y + w.Height / 2))))
                throw new InvalidOperationException(CannotMove);
            if (keepOriginal && texts.Count > 0)
            {
                // As letras que apareceram na página de destino precisam incluir todas as letras copiadas.
                static Dictionary<char, int> Letters(IEnumerable<string> parts) => parts.SelectMany(p => p)
                    .Where(c => !char.IsWhiteSpace(c)).GroupBy(c => c).ToDictionary(g => g.Key, g => g.Count());
                var before = Letters(PdfTextLayout.Words(input, toPage).Select(w => w.Text));
                var after = Letters(PdfTextLayout.Words(temp, toPage).Select(w => w.Text));
                var copied = Letters(texts.Select(t => t.Text));
                if (copied.Any(c => after.GetValueOrDefault(c.Key) - before.GetValueOrDefault(c.Key) < c.Value))
                    throw new InvalidOperationException(CannotCopy);
            }
            File.Copy(temp, output, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { /* arquivo temporário: tanto faz */ }
        }
    }

    /// <summary>
    /// O formulário com só o que foi escolhido (<paramref name="only"/> filtra cada conteúdo), nas posições em que era
    /// desenhado. O que já estava num formulário de mover/copiar vira um formulário menor com as fontes e imagens
    /// dele (que podem ser de outra página), desenhado na mesma posição.
    /// </summary>
    private static List<(PdfDictionary Form, M At)> BuildForms(PdfDocument doc, PdfItem? pageResources, List<StreamOps> streams,
        List<StreamOps> hits, Func<CSequence, List<int>, string> only)
    {
        // Cada pedaço vira um formulário desenhado direto na página (nunca um dentro do outro): assim ele pode ser
        // movido, copiado e apagado de novo depois.
        var forms = new List<(PdfDictionary, M)>();
        if (hits.Any(s => s.Move is null))
        {
            var form = new StringBuilder("q\n");
            foreach (var s in streams.Where(s => !s.AppArtifact && s.Seq is not null && s.Move is null))
                form.Append(only(s.Seq!, s.InBlock)).Append('\n');
            form.Append("Q\n");
            forms.Add((NewForm(doc, pageResources, form.ToString()), M.Identity));
        }
        // O que já tinha sido movido/copiado antes: com as fontes e imagens do formulário dele (podem ser de outra página).
        foreach (var s in hits.Where(s => s.Move is not null))
            forms.Add((NewForm(doc, s.Stream.Elements["/Resources"], only(s.Seq!, s.InBlock)), s.Move!.Value.At));
        return forms;
    }

    /// <summary>
    /// Desenha os formulários na página, deslocados (tx, ty em pontos do PDF). Cada um vai marcado como um item
    /// ("/PSItem"): a leitura dos blocos não junta o que foi colado/movido com o texto vizinho, mesmo encostado.
    /// </summary>
    private static void DrawForms(PdfPage page, List<(PdfDictionary Form, M At)> forms, double tx, double ty)
    {
        var doc = page.Owner;
        var xobjects = page.Resources.Elements.GetDictionary("/XObject");
        if (xobjects is null)
        {
            xobjects = new PdfDictionary(doc);
            page.Resources.Elements["/XObject"] = xobjects;
        }
        var draw = new StringBuilder("Q\n");
        int n = 1;
        foreach (var (form, at) in forms)
        {
            string name;
            do name = $"/PSMove{n++}"; while (xobjects.Elements.ContainsKey(name));
            xobjects.Elements[name] = form.Reference;
            var m = at.Then(new M(1, 0, 0, 1, tx, ty));
            draw.Append($"/PSItem BMC q {Num(m.A)} {Num(m.B)} {Num(m.C)} {Num(m.D)} {Num(m.E)} {Num(m.F)} cm {name} Do Q EMC\n");
        }
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q\n"));
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes(draw.ToString()));
    }

    /// <summary>Formulário do PDF (desenho reaproveitável) com o conteúdo e os recursos (fontes, imagens) dados.</summary>
    private static PdfDictionary NewForm(PdfDocument doc, PdfItem? resources, string content)
    {
        var xobject = new PdfDictionary(doc);
        xobject.Elements.SetName("/Type", "/XObject");
        xobject.Elements.SetName("/Subtype", "/Form");
        xobject.Elements["/BBox"] = new PdfArray(doc, new PdfReal(-20000), new PdfReal(-20000), new PdfReal(20000), new PdfReal(20000));
        if (resources is not null) xobject.Elements["/Resources"] = resources;
        xobject.CreateStream(Encoding.Latin1.GetBytes(content));
        doc.Internals.AddObject(xobject);
        return xobject;
    }

    /// <summary>O conteúdo com só os comandos escolhidos (textos, imagens e desenhos); o resto não aparece.</summary>
    private static string OnlyChosen(CSequence seq, List<int> chosen)
    {
        var keep = chosen.ToHashSet();
        var kept = new CSequence();
        for (int i = 0; i < seq.Count; i++)
        {
            if (seq[i] is not COperator op) { kept.Add(seq[i]); continue; }
            var name = op.OpCode.OpCodeName;
            if (name is OpCodeName.Tj or OpCodeName.TJ or OpCodeName.QuoteSingle or OpCodeName.QuoteDouble)
            {
                if (keep.Contains(i)) kept.Add(op);
                else if (name is OpCodeName.QuoteSingle or OpCodeName.QuoteDouble) kept.Add(OpCodes.OperatorFromName("T*"));
            }
            else if (IsPaint(name)) kept.Add(keep.Contains(i) ? op : OpCodes.OperatorFromName("n"));
            else if (name == OpCodeName.Do) { if (keep.Contains(i)) kept.Add(op); }
            else if (name is OpCodeName.sh or OpCodeName.BI or OpCodeName.ID or OpCodeName.EI) { }
            else kept.Add(op);
        }
        return Encoding.Latin1.GetString(kept.ToContent());
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
    /// Move imagens e desenhos: os comandos que desenham cada um ficam entre "q 1 0 0 1 x y cm … Q" (um
    /// deslocamento). A imagem e o desenho em si não mudam. Item que não está desenhado direto na página: erro.
    /// </summary>
    private static void MoveObjectsIn(PdfPage page, IReadOnlyList<ContentBlock> objects, double dx, double dy)
    {
        var streams = Walk(page, objects).Where(s => s.Seq is not null && s.Move is null && s.InBlock.Count > 0).ToList();
        if (streams.Sum(s => s.InBlock.Count) < objects.Count)
            throw new InvalidOperationException(objects.Count == 1 && objects[0].Kind == BlockKind.Image ? CannotMoveImage : CannotMoveObject);
        var (tx, ty) = PdfShift(page, dx, dy);
        foreach (var s in streams)
        {
            for (int k = s.InBlock.Count - 1; k >= 0; k--)
            {
                int i = s.InBlock[k], first = s.Starts![i];
                var ctm = s.Places![i];
                // Deslocamento na página (tx, ty) escrito nas medidas de onde a imagem é desenhada: resolve a parte
                // linear da matriz atual (o "cm" novo é aplicado antes dela).
                double det = ctm.A * ctm.D - ctm.B * ctm.C;
                if (Math.Abs(det) < 1e-12) throw new InvalidOperationException(CannotMoveObject);
                double lx = (tx * ctm.D - ty * ctm.C) / det, ly = (ty * ctm.A - tx * ctm.B) / det;
                var shift = OpCodes.OperatorFromName("cm");
                foreach (double v in new[] { 1.0, 0, 0, 1, lx, ly }) shift.Operands.Add(new CReal { Value = v });
                s.Seq!.Insert(i + 1, OpCodes.OperatorFromName("Q"));
                s.Seq.Insert(first, shift);
                s.Seq.Insert(first, OpCodes.OperatorFromName("q"));
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
        var forms = BuildForms(page.Owner, page.Elements["/Resources"], streams, hits, OnlyText);

        // 2) Tira os textos do lugar antigo.
        RemoveHits(streams);

        // 3) Desenha os formulários deslocados.
        DrawForms(page, forms, tx, ty);
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
        int TextOps = 0, (PdfDictionary PageStream, COperator Cm, M At)? Move = null, Dictionary<int, M>? Places = null,
        Dictionary<int, int>? Starts = null);

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
            for (int k = s.InBlock.Count - 1; k >= 0; k--)
            {
                int i = s.InBlock[k];
                bool paint = s.Seq[i] is COperator { OpCode.OpCodeName: var name } && IsPaint(name);
                s.Seq.RemoveAt(i);
                if (paint) s.Seq.Insert(i, OpCodes.OperatorFromName("n")); // desenho apagado: o caminho termina sem pintar
            }
            Rewrite(s);
            removed += s.InBlock.Count;
        }
        return removed;
    }

    private static bool IsPaint(OpCodeName name) => name is OpCodeName.S or OpCodeName.s or OpCodeName.f or OpCodeName.F
        or OpCodeName.fx or OpCodeName.B or OpCodeName.Bx or OpCodeName.b or OpCodeName.bx;

    /// <summary>Apaga da página os comandos de texto que começam dentro do bloco. Devolve quantos saíram.</summary>
    internal static int RemoveTextIn(PdfPage page, ContentBlock block) => RemoveHits(Walk(page, [block]));

    /// <summary>
    /// Percorre os comandos de todos os trechos do conteúdo (acompanhando a posição do texto e as transformações)
    /// e anota os que desenham os blocos. Trechos colocados pelo app (marca d'água, cabeçalho…) são lidos, mas nada
    /// deles é anotado. Trecho que o PDFsharp não entende: Seq = null. Os formulários de textos já movidos pelo app
    /// (/PSMove…) também são percorridos, no lugar onde são desenhados.
    /// </summary>
    /// <param name="onShape">Recebe a área (tela) de cada desenho pintado direto na página (para achar os desenhos).</param>
    private static List<StreamOps> Walk(PdfPage page, IReadOnlyList<ContentBlock> blocks, Action<double, double, double, double>? onShape = null)
    {
        var result = new List<StreamOps>();
        if (page.Elements["/Contents"] is null) return result;
        var texts = blocks.Where(b => b.Kind == BlockKind.Text).ToList();
        var images = blocks.Where(b => b.Kind == BlockKind.Image).ToList();
        var shapes = blocks.Where(b => b.Kind == BlockKind.Shape).ToList();
        var geometry = PageGeometry.Of(page);
        int pathStart = -1;
        var pathPoints = new List<(double X, double Y)>();
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
            // Além de começar dentro do bloco, a linha precisa estar na linha de base de uma das linhas dele: a última
            // linha do texto de cima pode começar dentro da borda de cima do bloco (encostado) e não é dele.
            return texts.Any(b => vx >= b.X - 1 && vx <= b.Right + 1 && vy >= b.Y - 1 && vy <= b.Bottom + 1
                && (b.Lines.Count == 0 || b.Lines.Any(l => Math.Abs(vy - l.Baseline) <= Math.Max(2, l.Height * 0.45))));
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
        void AddPoint(double x, double y) => pathPoints.Add(ctm.Apply(x, y));
        (List<int> InBlock, int TextOps, Dictionary<int, M> Places, Dictionary<int, int> Starts) Scan(CSequence seq, bool appArtifact, PdfDictionary? pageStream)
        {
            var places = new Dictionary<int, M>();
            var starts = new Dictionary<int, int>();
            pathStart = -1;
            pathPoints.Clear();
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
                        starts[i] = i;
                        break;
                    // Desenhos: comandos que montam o caminho e o comando que o pinta.
                    case OpCodeName.m or OpCodeName.l when args.Count == 2:
                        if (pathStart < 0) pathStart = i;
                        AddPoint(N(args[0]), N(args[1]));
                        break;
                    case OpCodeName.c when args.Count == 6:
                        if (pathStart < 0) pathStart = i;
                        AddPoint(N(args[0]), N(args[1])); AddPoint(N(args[2]), N(args[3])); AddPoint(N(args[4]), N(args[5]));
                        break;
                    case OpCodeName.v or OpCodeName.y when args.Count == 4:
                        if (pathStart < 0) pathStart = i;
                        AddPoint(N(args[0]), N(args[1])); AddPoint(N(args[2]), N(args[3]));
                        break;
                    case OpCodeName.re when args.Count == 4:
                        if (pathStart < 0) pathStart = i;
                        double rx = N(args[0]), ry = N(args[1]), rw = N(args[2]), rh = N(args[3]);
                        AddPoint(rx, ry); AddPoint(rx + rw, ry); AddPoint(rx, ry + rh); AddPoint(rx + rw, ry + rh);
                        break;
                    case OpCodeName.h:
                        if (pathStart < 0) pathStart = i;
                        break;
                    case OpCodeName.n:
                        pathStart = -1;
                        pathPoints.Clear();
                        break;
                    case var paint when IsPaint(paint):
                        if (pathStart >= 0 && pathPoints.Count > 0 && !appArtifact && pageStream is not null)
                        {
                            double l = double.MaxValue, t = double.MaxValue, r = double.MinValue, b = double.MinValue;
                            foreach (var (px, py) in pathPoints)
                            {
                                var v = geometry.PdfToView(px, py);
                                l = Math.Min(l, v.X); r = Math.Max(r, v.X); t = Math.Min(t, v.Y); b = Math.Max(b, v.Y);
                            }
                            onShape?.Invoke(l, t, r - l, b - t);
                            if (shapes.Any(sh => Math.Abs(sh.X - l) < 1 && Math.Abs(sh.Y - t) < 1 && Math.Abs(sh.Right - r) < 1 && Math.Abs(sh.Bottom - b) < 1))
                            {
                                inBlock.Add(i);
                                places[i] = ctm;
                                starts[i] = pathStart;
                            }
                        }
                        pathStart = -1;
                        pathPoints.Clear();
                        break;
                }
            }
            return (inBlock, textOps, places, starts);
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
            var (inBlock, textOps, places, starts) = Scan(seq, appArtifact, stream);
            result.Add(new StreamOps(stream, seq, inBlock, appArtifact, textOps, Places: places, Starts: starts));
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
            var (inBlock, textOps, _, _) = Scan(seq, false, null);
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
