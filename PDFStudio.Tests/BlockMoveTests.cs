using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Modo Editar: apagar e mover um bloco de texto inteiro, sem mudar a letra.</summary>
public class BlockMoveTests
{
    private static ContentBlock BlockStarting(string path, string start) =>
        Assert.Single(PdfContentBlocks.Detect(path, 0), b => b.Kind == BlockKind.Text && b.Text.StartsWith(start));

    private static string AllText(string path) => string.Join(" ", PdfTextLayout.Words(path, 0).Select(w => w.Text));

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Paragraph_MovesWithTheSameLook_OnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir, rotation);
        var old = BlockStarting(input, "Primeira");
        string output = Path.Combine(dir, "movido.pdf");

        PdfTextEditor.Move(input, 0, old, 40, 200, output);

        var now = BlockStarting(output, "Primeira");
        Assert.Equal(old.Text, now.Text);
        Assert.Equal(old.X + 40, now.X, 1.0);
        Assert.Equal(old.Y + 200, now.Y, 1.0);
        Assert.Equal(old.FontName, now.FontName); // a mesma fonte gravada no PDF
        Assert.Equal(old.FontSize, now.FontSize, 0.1);
        Assert.Equal(old.Color, now.Color);
        Assert.Equal(old.Lines[1].Baseline - old.Lines[0].Baseline, now.Lines[1].Baseline - now.Lines[0].Baseline, 0.1);
        // Os outros blocos ficam onde estavam, e só uma vez.
        var title = BlockStarting(input, "Relatório");
        Assert.Equal(title.Y, BlockStarting(output, "Relatório").Y, 0.5);
        Assert.Single(PdfContentBlocks.Detect(output, 0), b => b.Text == "Coluna esquerda");
        Assert.Single(PdfContentBlocks.Detect(output, 0), b => b.Kind == BlockKind.Image);
        Assert.Equal(rotation, TestPdfs.Rotations(output)[0]);
    }

    [Fact]
    public void MovedTwice_AndOverwritingTheSameFile_StaysCorrect()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var old = BlockStarting(input, "Coluna direita");

        PdfTextEditor.Move(input, 0, old, 0, 100, input);
        var once = BlockStarting(input, "Coluna direita");
        PdfTextEditor.Move(input, 0, once, -50, 30, input);

        var now = BlockStarting(input, "Coluna direita");
        Assert.Equal(old.X - 50, now.X, 1.0);
        Assert.Equal(old.Y + 130, now.Y, 1.0);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(AllText(input), "direita"));
    }

    [Fact]
    public void BackgroundBox_StaysInPlace_OnlyTheTextMoves()
    {
        // Como num PDF feito por navegador: caixa colorida desenhada e o texto dentro de uma área de recorte.
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "caixa.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawRectangle(XBrushes.LightBlue, 50, 50, 300, 60);
            var state = gfx.Save();
            gfx.IntersectClip(new XRect(50, 50, 300, 60));
            gfx.DrawString("Dentro da caixa", new XFont("Arial", 12), XBrushes.Black, new XPoint(60, 85));
            gfx.Restore(state);
            gfx.DrawString("Fora da caixa", new XFont("Arial", 12), XBrushes.Black, new XPoint(60, 300));
            doc.Save(input);
        }
        var old = BlockStarting(input, "Dentro");
        string output = Path.Combine(dir, "saida.pdf");

        PdfTextEditor.Move(input, 0, old, 10, 20, output); // continua dentro do recorte, que vai junto

        var now = BlockStarting(output, "Dentro");
        Assert.Equal(old.Y + 20, now.Y, 1.0);
        Assert.Equal(old.X + 10, now.X, 1.0);
        Assert.Single(PdfContentBlocks.Detect(output, 0), b => b.Text == "Fora da caixa");
        // A caixa colorida não foi copiada: continua um só retângulo pintado na página.
        string content = PageContent(output);
        Assert.Equal(PageContent(input).Split(" re").Length, content.Split(" re").Length - CountInForm(output, " re"));
    }

    [Fact]
    public void Delete_RemovesOnlyThatBlock()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        string output = Path.Combine(dir, "apagado.pdf");

        PdfTextEditor.Delete(input, 0, BlockStarting(input, "Primeira"), output);

        string text = AllText(output);
        Assert.DoesNotContain("Primeira", text);
        Assert.DoesNotContain("terceira", text);
        Assert.Contains("Relatório", text);
        Assert.Contains("Coluna direita", text);
    }

    [Fact]
    public void MovedBlock_CanBeDeletedAfterwards()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        PdfTextEditor.Move(input, 0, BlockStarting(input, "Primeira"), 30, 150, input);

        var result = PdfTextEditor.Delete(input, 0, BlockStarting(input, "Primeira"), input);

        Assert.False(result.Covered); // saiu de verdade (não foi coberto com branco)
        Assert.DoesNotContain("Primeira", AllText(input));
        Assert.Contains("Coluna direita", AllText(input));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    public void SeveralBlocks_MoveTogether_KeepingTheDistanceBetweenThem(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir, rotation);
        var chosen = new[] { BlockStarting(input, "Relatório"), BlockStarting(input, "Primeira"), Image(input) };
        string output = Path.Combine(dir, "grupo.pdf");

        PdfTextEditor.Move(input, 0, chosen, 30, 250, output);

        var title = BlockStarting(output, "Relatório");
        var paragraph = BlockStarting(output, "Primeira");
        var image = Image(output);
        Assert.Equal(chosen[0].X + 30, title.X, 1.0);
        Assert.Equal(chosen[0].Y + 250, title.Y, 1.0);
        Assert.Equal(chosen[1].Y + 250, paragraph.Y, 1.0);
        Assert.Equal(chosen[2].Y + 250, image.Y, 1.0);
        Assert.Equal(chosen[1].Lines[2].Baseline - chosen[1].Lines[0].Baseline, paragraph.Lines[2].Baseline - paragraph.Lines[0].Baseline, 0.1);
        Assert.Equal(chosen[0].FontName, title.FontName);
        Assert.True(title.Bold);
        Assert.Equal(chosen[0].Color, title.Color);
        // O que não foi escolhido fica parado.
        Assert.Equal(BlockStarting(input, "Coluna direita").Y, BlockStarting(output, "Coluna direita").Y, 0.5);
    }

    [Fact]
    public void GroupMovedBefore_OneOfThemCanStillMoveAlone()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        PdfTextEditor.Move(input, 0, [BlockStarting(input, "Coluna esquerda"), BlockStarting(input, "Coluna direita")], 0, 200, input);
        var left = BlockStarting(input, "Coluna esquerda");
        var right = BlockStarting(input, "Coluna direita");

        PdfTextEditor.Move(input, 0, left, 0, 60, input); // só uma das duas, de um formulário que tem as duas

        Assert.Equal(left.Y + 60, BlockStarting(input, "Coluna esquerda").Y, 1.0);
        Assert.Equal(right.Y, BlockStarting(input, "Coluna direita").Y, 0.5);
        Assert.Equal(1, System.Text.RegularExpressions.Regex.Count(AllText(input), "esquerda"));
    }

    [Fact]
    public void SeveralBlocks_DeletedTogether()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);

        var result = PdfTextEditor.Delete(input, 0, [BlockStarting(input, "Relatório"), BlockStarting(input, "Coluna direita"), Image(input)], input);

        Assert.False(result.Covered);
        string text = AllText(input);
        Assert.DoesNotContain("Relatório", text);
        Assert.DoesNotContain("direita", text);
        Assert.Contains("Coluna esquerda", text);
        Assert.Contains("Primeira", text);
        Assert.DoesNotContain(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
    }

    /// <summary>Página com um título, uma linha azul embaixo dele e uma caixa (preenchida e com contorno).</summary>
    private static string WithShapes(string dir, int rotation = 0)
    {
        string input = Path.Combine(dir, "desenhos.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using (var gfx = XGraphics.FromPdfPage(page))
            {
                gfx.DrawString("Tipo de contrato", new XFont("Arial", 12), XBrushes.Black, new XPoint(60, 100));
                gfx.DrawLine(new XPen(XColors.RoyalBlue, 1), 60, 106, 260, 106);
                gfx.DrawRectangle(new XPen(XColors.Black, 1), XBrushes.LightYellow, 60, 200, 150, 60);
            }
            doc.Save(input);
        }
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation)], input);
        return input;
    }

    private static List<ContentBlock> ShapesOf(string path) =>
        PdfTextEditor.Shapes(path, 0).OrderBy(b => b.Y).ToList();

    [Fact]
    public void Shapes_AreFound_LineAndBox()
    {
        var shapes = ShapesOf(WithShapes(TestPdfs.TempDir()));
        Assert.Equal(2, shapes.Count); // a caixa (preenchimento + contorno) conta uma vez só
        Assert.InRange(shapes[0].Width, 199, 201); // a linha
        Assert.InRange(shapes[0].Height, 0, 1);
        Assert.InRange(shapes[1].Width, 149, 151); // a caixa
        Assert.InRange(shapes[1].Height, 59, 61);
    }

    [Fact]
    public void LineAndText_MoveTogether()
    {
        string dir = TestPdfs.TempDir();
        string input = WithShapes(dir);
        var text = BlockStarting(input, "Tipo");
        var line = ShapesOf(input).OrderBy(b => b.Height).First();
        string output = Path.Combine(dir, "saida.pdf");

        PdfTextEditor.Move(input, 0, [text, line], 25, 40, output);

        var movedText = BlockStarting(output, "Tipo");
        var movedLine = PdfTextEditor.Shapes(output, 0).OrderBy(b => Math.Min(b.Width, b.Height)).First();
        Assert.Equal(text.X + 25, movedText.X, 1.0);
        Assert.Equal(line.X + 25, movedLine.X, 1.0);
        Assert.Equal(line.Y + 40, movedLine.Y, 1.0);
        Assert.Equal(line.Y - text.Y, movedLine.Y - movedText.Y, 1.0); // a distância entre a linha e o texto não muda
        Assert.Equal(2, PdfTextEditor.Shapes(output, 0).Count); // a caixa continua lá, uma só
    }

    [Theory]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Shapes_MoveOnRotatedPage(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = WithShapes(dir, rotation);
        var shapes = ShapesOf(input);
        Assert.Equal(2, shapes.Count);
        var box = shapes.OrderByDescending(b => b.Width * b.Height).First();

        PdfTextEditor.Move(input, 0, box, 30, 50, input);

        var now = PdfTextEditor.Shapes(input, 0).OrderByDescending(b => b.Width * b.Height).First();
        Assert.Equal(box.X + 30, now.X, 1.0);
        Assert.Equal(box.Y + 50, now.Y, 1.0);
        Assert.Equal(rotation, TestPdfs.Rotations(input)[0]);
    }

    [Fact]
    public void Line_CanBeDeleted_TextStays()
    {
        string dir = TestPdfs.TempDir();
        string input = WithShapes(dir);
        var line = ShapesOf(input)[0];

        PdfTextEditor.Delete(input, 0, line, input);

        var left = Assert.Single(PdfTextEditor.Shapes(input, 0));
        Assert.InRange(left.Height, 59, 61); // sobrou só a caixa
        Assert.Contains("Tipo de contrato", AllText(input));
    }

    [Fact]
    public void Box_MovedTwice()
    {
        string dir = TestPdfs.TempDir();
        string input = WithShapes(dir);
        var box = ShapesOf(input)[1];

        PdfTextEditor.Move(input, 0, box, 100, 0, input);
        PdfTextEditor.Move(input, 0, ShapesOf(input)[1], 0, 300, input);

        var now = ShapesOf(input)[1];
        Assert.Equal(box.X + 100, now.X, 1.0);
        Assert.Equal(box.Y + 300, now.Y, 1.0);
        Assert.Equal(box.Width, now.Width, 0.5);
    }

    private static ContentBlock Image(string path) => Assert.Single(PdfContentBlocks.Detect(path, 0), b => b.Kind == BlockKind.Image);

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void Image_MovesWithTheSameSize_OnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir, rotation);
        var old = Image(input);
        string output = Path.Combine(dir, "movida.pdf");

        PdfTextEditor.Move(input, 0, old, 120, 90, output);

        var now = Image(output);
        Assert.Equal(old.X + 120, now.X, 1.0);
        Assert.Equal(old.Y + 90, now.Y, 1.0);
        Assert.Equal(old.Width, now.Width, 0.5);
        Assert.Equal(old.Height, now.Height, 0.5);
        Assert.Equal(AllText(input), AllText(output)); // os textos não mudam
        Assert.Equal(rotation, TestPdfs.Rotations(output)[0]);
    }

    [Fact]
    public void Image_MovedTwice_ThenDeleted()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var old = Image(input);

        PdfTextEditor.Move(input, 0, old, 0, 100, input);
        PdfTextEditor.Move(input, 0, Image(input), 50, -20, input);
        var now = Image(input);
        Assert.Equal(old.X + 50, now.X, 1.0);
        Assert.Equal(old.Y + 80, now.Y, 1.0);

        PdfTextEditor.Delete(input, 0, now, input);
        Assert.DoesNotContain(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        Assert.Contains("Relatório", AllText(input));
    }

    [Fact]
    public void SameImageTwice_OnlyTheChosenOneMoves()
    {
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "duas.pdf");
        string png = TestImages.Png(Path.Combine(dir, "foto.png"), 40, 20);
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            using var img = XImage.FromFile(png);
            gfx.DrawImage(img, 50, 50, 100, 50);
            gfx.DrawImage(img, 50, 300, 100, 50); // o mesmo objeto de imagem, desenhado duas vezes
            doc.Save(input);
        }
        var images = PdfContentBlocks.Detect(input, 0).Where(b => b.Kind == BlockKind.Image).OrderBy(b => b.Y).ToList();
        Assert.Equal(2, images.Count);

        PdfTextEditor.Move(input, 0, images[1], 200, 0, input);

        var now = PdfContentBlocks.Detect(input, 0).Where(b => b.Kind == BlockKind.Image).OrderBy(b => b.Y).ToList();
        Assert.Equal(images[0].X, now[0].X, 0.5);
        Assert.Equal(images[1].X + 200, now[1].X, 1.0);
    }

    /// <summary>Todo o conteúdo da página e dos formulários dela, sem compactação.</summary>
    private static string PageContent(string path)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < page.Contents.Elements.Count; i++)
            if (page.Contents.Elements.GetDictionary(i)?.Stream is { } s) sb.Append(System.Text.Encoding.Latin1.GetString(s.UnfilteredValue));
        if (page.Resources.Elements.GetDictionary("/XObject") is { } xobjects)
            foreach (var key in xobjects.Elements.Keys)
                if (xobjects.Elements.GetDictionary(key) is { Stream: { } st } x && x.Elements.GetName("/Subtype") == "/Form")
                    sb.Append(System.Text.Encoding.Latin1.GetString(st.UnfilteredValue));
        return sb.ToString();
    }

    /// <summary>Quantas vezes <paramref name="word"/> aparece nos formulários de mover (eles têm o retângulo do recorte).</summary>
    private static int CountInForm(string path, string word)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(path, PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        var page = doc.Pages[0];
        int n = 0;
        if (page.Resources.Elements.GetDictionary("/XObject") is { } xobjects)
            foreach (var key in xobjects.Elements.Keys.Where(k => k.StartsWith("/PSMove")))
                if (xobjects.Elements.GetDictionary(key)?.Stream is { } st)
                    n += System.Text.Encoding.Latin1.GetString(st.UnfilteredValue).Split(word).Length - 1;
        return n;
    }
}
