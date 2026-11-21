using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 6.2: redigir (apagar de verdade o que está na área e pôr tarja).</summary>
public class RedactionTests
{
    private static List<TextWord> Words(string path, int page = 0) => PdfTextLayout.Words(path, page);

    private static string AllLetters(string path, int page = 0)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Concat(pig.GetPage(page + 1).Letters.Select(l => l.Value));
    }

    /// <summary>Uma linha "Nome: Maria CPF: 123.456.789-00 fim" e devolve a área que cobre só o CPF.</summary>
    private static (string Path, RedactionArea Cpf, TextWord First) Sample(string dir, PdfFontEncoding encoding, int rotation = 0)
    {
        string path = Path.Combine(dir, $"doc-{encoding}-{rotation}.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            if (rotation == 0)
            {
                using var gfx = XGraphics.FromPdfPage(page);
                var font = new XFont("Arial", 14, XFontStyleEx.Regular, new XPdfFontOptions(encoding));
                gfx.DrawString("Nome: Maria CPF: 123.456.789-00 fim", font, XBrushes.Black, 60, 100);
                gfx.DrawString("Outra linha que fica", font, XBrushes.Black, 60, 140);
            }
            page.Rotate = rotation;
            doc.Save(path);
        }
        if (rotation != 0)
        {
            // Página girada: o texto é escrito "de pé" como a página aparece na tela.
            var style = new PageTextStyle("Arial", 14, new AnnotationColor(0, 0, 0));
            PdfPageContent.AddText(path, 0, 60, 90, "Nome: Maria CPF: 123.456.789-00 fim", style, path);
            PdfPageContent.AddText(path, 0, 60, 130, "Outra linha que fica", style, path);
        }
        var words = Words(path);
        var cpf = words.Single(w => w.Text.StartsWith("123"));
        return (path, new RedactionArea(0, cpf.X - 1, cpf.Y - 1, cpf.Width + 2, cpf.Height + 2), words[0]);
    }

    [Theory]
    [InlineData(PdfFontEncoding.WinAnsi, 0)]
    [InlineData(PdfFontEncoding.Unicode, 0)]
    [InlineData(PdfFontEncoding.WinAnsi, 90)]
    [InlineData(PdfFontEncoding.Unicode, 180)]
    public void LettersInTheArea_LeaveTheFile_AndTheRestStaysInPlace(PdfFontEncoding encoding, int rotation)
    {
        string dir = TestPdfs.TempDir();
        var (input, area, _) = Sample(dir, encoding, rotation);
        var before = Words(input);
        string output = Path.Combine(dir, "redigido.pdf");

        var result = PdfRedaction.Apply(input, [area], output);

        Assert.Equal("123.456.789-00".Length, result.Letters);
        Assert.Equal(0, result.CoveredOnly);
        string letters = AllLetters(output);
        Assert.DoesNotContain("123", letters);
        Assert.DoesNotContain("789", letters);
        var after = Words(output);
        // As palavras de fora continuam no mesmo lugar (a linha não "andou").
        foreach (string text in new[] { "Nome:", "Maria", "CPF:", "fim", "Outra" })
        {
            var a = before.First(w => w.Text == text);
            var b = after.First(w => w.Text == text);
            Assert.Equal(a.X, b.X, 0.5);
            Assert.Equal(a.Y, b.Y, 0.5);
        }
        Assert.Equal(rotation, TestPdfs.Rotations(output)[0]);
    }

    [Fact]
    public void ImageInsideTheArea_IsRemoved_AndAnnotationsToo()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Rectangle, image.X + 2, image.Y + 2, 10, 10,
            new AnnotationStyle(new AnnotationColor(255, 0, 0), 1), "Teste", input);
        string output = Path.Combine(dir, "sem-imagem.pdf");

        var result = PdfRedaction.Apply(input, [new RedactionArea(0, image.X - 2, image.Y - 2, image.Width + 4, image.Height + 4)], output);

        Assert.Equal(1, result.Images);
        Assert.Equal(1, result.Annotations);
        Assert.DoesNotContain(PdfContentBlocks.Detect(output, 0), b => b.Kind == BlockKind.Image);
        Assert.Empty(TestPdfs.AnnotationSubtypes(output));
    }

    /// <summary>"Segredo" dentro de um formulário interno, desenhado na página 1 e também na página 2.</summary>
    private static string FormOnTwoPages(string dir)
    {
        string input = Path.Combine(dir, "form.pdf");
        using var doc = new PdfDocument();
        var form = new XForm(doc, XUnit.FromPoint(300), XUnit.FromPoint(40));
        using (var fg = XGraphics.FromForm(form))
            fg.DrawString("Segredo", new XFont("Arial", 14), XBrushes.Black, new XPoint(0, 20));
        for (int i = 0; i < 2; i++)
        {
            var page = doc.AddPage();
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.DrawImage(form, 100, 100);
        }
        doc.Save(input);
        return input;
    }

    [Fact]
    public void TextInsideAForm_IsErased_AndOtherPagesUsingTheSameFormKeepIt()
    {
        string dir = TestPdfs.TempDir();
        string input = FormOnTwoPages(dir);
        var word = Words(input).Single();
        string output = Path.Combine(dir, "redigido.pdf");

        var result = PdfRedaction.Apply(input, [new RedactionArea(0, word.X - 2, word.Y - 2, word.Width + 4, word.Height + 4)], output);

        Assert.Equal("Segredo".Length, result.Letters);
        Assert.Equal(0, result.CoveredOnly);
        Assert.DoesNotContain("Segredo", AllLetters(output, 0));
        Assert.Contains("Segredo", AllLetters(output, 1)); // a página 2 não foi redigida
    }

    [Fact]
    public void ImageInsideTheArea_LeavesTheFile()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        string output = Path.Combine(dir, "sem-imagem.pdf");

        byte[] imageData;
        using (var original = PdfReader.Open(input, PdfDocumentOpenMode.Import))
            imageData = original.Internals.GetAllObjects().OfType<PdfDictionary>().Single(d => d.Elements.GetName("/Subtype") == "/Image").Stream.Value;

        PdfRedaction.Apply(input, [new RedactionArea(0, image.X - 2, image.Y - 2, image.Width + 4, image.Height + 4)], output);

        // Os bytes da imagem não estão mais em lugar nenhum do arquivo.
        Assert.Equal(-1, File.ReadAllBytes(output).AsSpan().IndexOf(imageData));
        // Nenhuma imagem sobra no arquivo (antes o desenho saía, mas os dados da imagem continuavam lá).
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        Assert.DoesNotContain(doc.Internals.GetAllObjects().OfType<PdfDictionary>(), d => d.Elements.GetName("/Subtype") == "/Image");
    }

    [Fact]
    public void ImagePartlyInTheArea_HasThosePixelsErased_AndTheRestKept()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        var image = Assert.Single(PdfContentBlocks.Detect(input, 0), b => b.Kind == BlockKind.Image);
        string output = Path.Combine(dir, "meia-imagem.pdf");

        // Tarja sobre a metade esquerda da imagem (e um pouco para fora, à esquerda e em cima/embaixo).
        var result = PdfRedaction.Apply(input, [new RedactionArea(0, image.X - 5, image.Y - 5, image.Width / 2 + 5, image.Height + 10)], output);

        Assert.Equal(1, result.Images);
        Assert.Equal(0, result.CoveredOnly);
        using var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        var xobjects = doc.Pages[0].Elements.GetDictionary("/Resources")!.Elements.GetDictionary("/XObject")!;
        var img = xobjects.Elements.Keys.Select(k => xobjects.Elements.GetDictionary(k)!).Single(d => d.Elements.GetName("/Subtype") == "/Image");
        int w = img.Elements.GetInteger("/Width"), h = img.Elements.GetInteger("/Height");
        byte[] pixels = img.Stream.UnfilteredValue;
        Assert.Equal(w * h * 3, pixels.Length);
        for (int row = 0; row < h; row++)
        {
            Assert.All(Enumerable.Range(0, w / 2 - 1), col => Assert.Equal(0, pixels[(row * w + col) * 3]));       // esquerda apagada
            Assert.All(Enumerable.Range(w / 2 + 1, w / 2 - 1), col => Assert.NotEqual(0, pixels[(row * w + col) * 3])); // direita intacta
        }
    }

    [Fact]
    public void JpegPartlyInTheArea_WithoutADecoder_IsRefused_AndNothingChanges()
    {
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "jpg.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            var image = new PdfDictionary(doc);
            image.Elements["/Type"] = new PdfName("/XObject");
            image.Elements["/Subtype"] = new PdfName("/Image");
            image.Elements["/Width"] = new PdfInteger(10);
            image.Elements["/Height"] = new PdfInteger(10);
            image.Elements["/ColorSpace"] = new PdfName("/DeviceGray");
            image.Elements["/BitsPerComponent"] = new PdfInteger(8);
            image.Elements["/Filter"] = new PdfName("/DCTDecode");
            image.CreateStream(new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
            doc.Internals.AddObject(image);
            var xo = new PdfDictionary(doc);
            xo.Elements["/Im1"] = image.Reference!;
            var res = new PdfDictionary(doc);
            res.Elements["/XObject"] = xo;
            page.Elements["/Resources"] = res;
            var content = new PdfDictionary(doc);
            content.CreateStream(System.Text.Encoding.ASCII.GetBytes("q 100 0 0 100 100 500 cm /Im1 Do Q"));
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference!;
            doc.Save(input);
        }
        string output = Path.Combine(dir, "saida.pdf");
        // Página A4 (842 de altura): a imagem vai de y=242 a 342 na tela; a tarja pega só a metade de cima.
        var ex = Assert.Throws<RedactionException>(() => PdfRedaction.Apply(input, [new RedactionArea(0, 90, 230, 130, 60)], output));
        Assert.Contains("Nada foi alterado", ex.Message);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void InlineImage_AndDrawingInsideTheArea_AreErased()
    {
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "inline.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            var content = new PdfDictionary(doc);
            byte[] bytes = System.Text.Encoding.Latin1.GetBytes(
                "q 50 0 0 50 100 600 cm BI /W 2 /H 2 /CS /G /BPC 8 ID \u00C0\u0011\u0022\u0033 EI Q\n" +
                "q 0 0 1 rg 300 600 40 40 re f Q\n");
            content.CreateStream(bytes);
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference!;
            doc.Save(input);
        }
        string output = Path.Combine(dir, "saida.pdf");
        // Tela: imagem de x 100–150, y 192–242; quadrado de x 300–340, y 202–242.
        var result = PdfRedaction.Apply(input, [new RedactionArea(0, 90, 180, 270, 80)], output);

        Assert.Equal(1, result.Images);
        Assert.Equal(1, result.Drawings);
        using var check = PdfReader.Open(output, PdfDocumentOpenMode.Import);
        string all = string.Concat(Enumerable.Range(0, check.Pages[0].Contents.Elements.Count)
            .Select(i => System.Text.Encoding.Latin1.GetString(check.Pages[0].Contents.Elements.GetDictionary(i)!.Stream.UnfilteredValue)));
        Assert.DoesNotContain("BI", all);
        Assert.DoesNotContain("300 600 40 40 re", all);
    }

    [Fact]
    public void UnreadablePageContent_IsRefused_OrDoesNotSurvive()
    {
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "quebrado.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            var content = new PdfDictionary(doc);
            content.Elements["/Filter"] = new PdfName("/FlateDecode");
            content.CreateStream(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }); // "compactado", mas não é
            doc.Internals.AddObject(content);
            page.Elements["/Contents"] = content.Reference!;
            doc.Save(input);
        }
        // Ou a redação recusa, ou o conteúdo que não dá para ler não sobra no arquivo (a biblioteca o descarta ao abrir).
        string output = Path.Combine(dir, "saida.pdf");
        try { PdfRedaction.Apply(input, [new RedactionArea(0, 10, 10, 100, 100)], output); }
        catch (RedactionException) { return; }
        Assert.DoesNotContain("\u0001\u0002\u0003\u0004\u0005\u0006\u0007\u0008", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(output)), StringComparison.Ordinal);
    }

    [Fact]
    public void FormFieldInTheArea_LosesItsStoredValue()
    {
        string dir = TestPdfs.TempDir();
        string input = Path.Combine(dir, "campo.pdf");
        using (var doc = new PdfDocument())
        {
            var page = doc.AddPage();
            var field = new PdfDictionary(doc);
            field.Elements["/FT"] = new PdfName("/Tx");
            field.Elements["/T"] = new PdfString("cpf");
            field.Elements["/V"] = new PdfString("SEGREDO-123");
            doc.Internals.AddObject(field);
            var widget = new PdfDictionary(doc);
            widget.Elements["/Type"] = new PdfName("/Annot");
            widget.Elements["/Subtype"] = new PdfName("/Widget");
            widget.Elements["/Rect"] = new PdfRectangle(new XPoint(100, 700), new XPoint(200, 720));
            widget.Elements["/Parent"] = field.Reference!;
            doc.Internals.AddObject(widget);
            var kids = new PdfArray(doc);
            kids.Elements.Add(widget.Reference!);
            field.Elements["/Kids"] = kids;
            var annots = new PdfArray(doc);
            annots.Elements.Add(widget.Reference!);
            page.Elements["/Annots"] = annots;
            var fields = new PdfArray(doc);
            fields.Elements.Add(field.Reference!);
            var acro = new PdfDictionary(doc);
            acro.Elements["/Fields"] = fields;
            doc.Internals.Catalog.Elements["/AcroForm"] = acro;
            doc.Save(input);
        }
        string output = Path.Combine(dir, "saida.pdf");
        // Tela: campo de x 100–200, y 122–142.
        var result = PdfRedaction.Apply(input, [new RedactionArea(0, 90, 110, 130, 40)], output);

        Assert.Equal(1, result.Annotations);
        Assert.DoesNotContain("SEGREDO", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(output)));
    }

    [Fact]
    public void BlackBox_IsDrawnOverTheArea()
    {
        string dir = TestPdfs.TempDir();
        var (input, area, _) = Sample(dir, PdfFontEncoding.WinAnsi);
        PdfRedaction.Apply(input, [area], input);

        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        string content = string.Concat(Enumerable.Range(0, doc.Pages[0].Contents.Elements.Count)
            .Select(i => System.Text.Encoding.Latin1.GetString(doc.Pages[0].Contents.Elements.GetDictionary(i)!.Stream.UnfilteredValue)));
        Assert.Contains(" re", content); // retângulo
        Assert.Contains("f", content);   // preenchido
    }

    [Fact]
    public void NoArea_IsRejected()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentException>(() => PdfRedaction.Apply(input, [], input));
    }
}
