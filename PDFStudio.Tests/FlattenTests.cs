using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 6.3: achatar comentários e campos de formulário.</summary>
public class FlattenTests
{
    private static string PageText(string path, int page = 0)
    {
        using var pig = UglyToad.PdfPig.PdfDocument.Open(path);
        return string.Concat(pig.GetPage(page + 1).Letters.Select(l => l.Value));
    }

    private static string Content(string path, int page = 0)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        var items = doc.Pages[page].Contents.Elements;
        return string.Concat(Enumerable.Range(0, items.Count).Select(i => Encoding.Latin1.GetString(items.GetDictionary(i)!.Stream.UnfilteredValue)));
    }

    /// <summary>Acrescenta um campo de texto (com aparência mostrando <paramref name="value"/>) ou de assinatura.</summary>
    private static void AddField(string path, string name, string value, bool signature = false)
    {
        PdfAnnotations.Edit(path, path, doc =>
        {
            var page = doc.Pages[0];
            var font = new PdfDictionary(doc);
            font.Elements.SetName("/Type", "/Font");
            font.Elements.SetName("/Subtype", "/Type1");
            font.Elements.SetName("/BaseFont", "/Helvetica");
            var resources = new PdfDictionary(doc);
            var fonts = new PdfDictionary(doc);
            fonts.Elements["/Helv"] = font;
            resources.Elements["/Font"] = fonts;
            var ap = new PdfDictionary(doc);
            ap.Elements.SetName("/Subtype", "/Form");
            ap.Elements["/BBox"] = new PdfArray(doc, new PdfReal(0), new PdfReal(0), new PdfReal(150), new PdfReal(20));
            ap.Elements["/Resources"] = resources;
            ap.CreateStream(Encoding.ASCII.GetBytes($"BT /Helv 12 Tf 2 5 Td ({value}) Tj ET"));
            doc.Internals.AddObject(ap);
            var normal = new PdfDictionary(doc);
            normal.Elements["/N"] = ap.Reference;

            var widget = new PdfDictionary(doc);
            widget.Elements.SetName("/Type", "/Annot");
            widget.Elements.SetName("/Subtype", "/Widget");
            widget.Elements.SetName("/FT", signature ? "/Sig" : "/Tx");
            widget.Elements["/T"] = new PdfString(name);
            if (!signature) widget.Elements["/V"] = new PdfString(value);
            widget.Elements["/Rect"] = new PdfArray(doc, new PdfReal(100), new PdfReal(500), new PdfReal(250), new PdfReal(520));
            widget.Elements["/AP"] = normal;
            widget.Elements["/P"] = page.Reference;
            doc.Internals.AddObject(widget);

            var annots = page.Elements.GetArray("/Annots") ?? new PdfArray(doc);
            annots.Elements.Add(widget.Reference);
            page.Elements["/Annots"] = annots;
            var form = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (form is null)
            {
                form = new PdfDictionary(doc);
                form.Elements["/Fields"] = new PdfArray(doc);
                doc.Internals.Catalog.Elements["/AcroForm"] = form;
            }
            form.Elements.GetArray("/Fields")!.Elements.Add(widget.Reference);
        });
    }

    [Fact]
    public void Comments_BecomePartOfThePage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Rectangle, 50, 50, 100, 60, new AnnotationStyle(new AnnotationColor(255, 0, 0)), "Eu", input);
        PdfAnnotations.AddTextBox(input, 1, 50, 300, 200, 40, "Texto do comentario", 12, new AnnotationStyle(new AnnotationColor(0, 0, 255), 0), "Eu", input);
        Assert.Equal((2, 0), PdfFlatten.Count(input));
        string output = Path.Combine(dir, "achatado.pdf");

        var result = PdfFlatten.Apply(input, output);

        Assert.Equal(2, result.Comments);
        Assert.Equal(0, result.WithoutAppearance);
        Assert.Empty(TestPdfs.AnnotationSubtypes(output));
        Assert.Contains("/Flat1 Do", Content(output, 0));
        Assert.Contains("Textodocomentario", PageText(output, 1).Replace(" ", "")); // o texto agora é da página
        Assert.Equal(["Página 1", "Página 2"], TestPdfs.Labels(output));             // o conteúdo antigo continua
    }

    [Fact]
    public void FormFields_AreFlattened_ButSignatureFieldsAndLinksStay()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        AddField(input, "nome", "Maria Silva");
        AddField(input, "assinatura", "", signature: true);
        PdfAnnotations.AddLink(input, 0, 10, 10, 50, 20, LinkTarget.ToUrl("https://example.com"), input);
        Assert.Equal((0, 1), PdfFlatten.Count(input));

        var result = PdfFlatten.Apply(input, input);

        Assert.Equal(1, result.Fields);
        Assert.Contains("MariaSilva", PageText(input).Replace(" ", ""));
        Assert.Equal(["/Widget", "/Link"], TestPdfs.AnnotationSubtypes(input).OrderByDescending(s => s).ToList());
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        var fields = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")!.Elements.GetArray("/Fields")!;
        Assert.Equal(1, fields.Elements.Count); // só a assinatura
    }

    [Fact]
    public void OnlyComments_KeepsTheFormFields()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        AddField(input, "nome", "Ana");
        PdfAnnotations.AddShape(input, 0, AnnotationKind.Oval, 50, 50, 40, 40, new AnnotationStyle(new AnnotationColor(0, 0, 0)), "Eu", input);

        var result = PdfFlatten.Apply(input, input, comments: true, fields: false);

        Assert.Equal((1, 0), (result.Comments, result.Fields));
        Assert.Equal(["/Widget"], TestPdfs.AnnotationSubtypes(input));
        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        Assert.NotNull(doc.Internals.Catalog.Elements.GetDictionary("/AcroForm"));
    }

    [Fact]
    public void AllFieldsFlattened_RemovesTheForm()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        AddField(input, "nome", "Ana");

        PdfFlatten.Apply(input, input, comments: false, fields: true);

        using var doc = PdfReader.Open(input, PdfDocumentOpenMode.Import);
        Assert.Null(doc.Internals.Catalog.Elements.GetDictionary("/AcroForm"));
    }

    [Fact]
    public void NothingChosen_IsRejected()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentException>(() => PdfFlatten.Apply(input, input, comments: false, fields: false));
    }
}
