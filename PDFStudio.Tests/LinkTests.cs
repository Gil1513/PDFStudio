using PDFStudio.Core;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.3: links para sites, e-mails e páginas do próprio documento.</summary>
public class LinkTests
{
    [Theory]
    [InlineData("https://exemplo.com.br/a?b=1", "https://exemplo.com.br/a?b=1")]
    [InlineData("exemplo.com.br", "https://exemplo.com.br")]
    [InlineData("  www.site.com/pagina  ", "https://www.site.com/pagina")]
    [InlineData("ana@empresa.com", "mailto:ana@empresa.com")]
    [InlineData("mailto:ana@empresa.com", "mailto:ana@empresa.com")]
    [InlineData("http://localhost:8080", "http://localhost:8080")]
    [InlineData("", null)]
    [InlineData("com espaço.com", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("file:///C:/Windows/notepad.exe", null)]
    [InlineData("palavra", null)]
    public void NormalizeUrl_AcceptsOnlySitesAndEmails(string typed, string? expected) =>
        Assert.Equal(expected, PdfAnnotations.NormalizeUrl(typed));

    [Theory]
    [InlineData(0)]
    [InlineData(90)]
    [InlineData(180)]
    [InlineData(270)]
    public void AddLink_KeepsScreenArea_AndTarget_OnEveryRotation(int rotation)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        if (rotation != 0) PdfOperations.Reorganize(input, [new(0, rotation), new(1), new(2)], input);

        PdfAnnotations.AddLink(input, 0, 100, 150, 200, 30, LinkTarget.ToUrl("exemplo.com"), input);
        PdfAnnotations.AddLink(input, 0, 50, 300, 80, 20, LinkTarget.ToPage(2), input);

        var links = PdfAnnotations.ListLinks(input);
        Assert.Equal(2, links.Count);
        var site = links[0];
        Assert.Equal((100, 150, 200, 30), (Math.Round(site.X, 3), Math.Round(site.Y, 3), Math.Round(site.Width, 3), Math.Round(site.Height, 3)));
        Assert.Equal(LinkTarget.ToUrl("https://exemplo.com"), site.Target);
        Assert.Equal(LinkTarget.ToPage(2), links[1].Target);
        Assert.Empty(PdfAnnotations.List(input)); // link não é comentário
    }

    [Fact]
    public void SetLinkTarget_AndDelete_Work()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
        PdfAnnotations.AddNote(input, 0, 10, 10, "nota", new AnnotationColor(255, 200, 0), "", input); // índice 0
        PdfAnnotations.AddLink(input, 0, 100, 100, 50, 20, LinkTarget.ToPage(1), input);           // índice 1

        var link = Assert.Single(PdfAnnotations.ListLinks(input, 0));
        Assert.Equal(1, link.Index);
        PdfAnnotations.SetLinkTarget(input, 0, link.Index, LinkTarget.ToUrl("ana@x.com"), input);
        Assert.Equal(LinkTarget.ToUrl("mailto:ana@x.com"), Assert.Single(PdfAnnotations.ListLinks(input)).Target);

        PdfAnnotations.Delete(input, 0, link.Index, input);
        Assert.Empty(PdfAnnotations.ListLinks(input));
        Assert.Single(PdfAnnotations.List(input)); // a nota continua
    }

    [Fact]
    public void AddLink_RejectsBadAddressAndMissingPage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        Assert.Throws<ArgumentException>(() => PdfAnnotations.AddLink(input, 0, 0, 0, 50, 20, LinkTarget.ToUrl("javascript:x"), input));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfAnnotations.AddLink(input, 0, 0, 0, 50, 20, LinkTarget.ToPage(5), input));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfAnnotations.AddLink(input, 0, 0, 0, 0, 20, LinkTarget.ToPage(0), input));
    }

    [Fact]
    public void ListLinks_ReadsLinksMadeByOtherPrograms_IncludingNamedDestinations()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        TestPdfs.AddLink(input, 10, 10, 60, 30); // link de site feito pelo PDFsharp

        // Link com destino por nome (/Names /Dests), como fazem o Word e outros programas.
        using (var doc = PdfReader.Open(input, PdfDocumentOpenMode.Modify))
        {
            var target = doc.Pages[2];
            var destArray = new PdfArray(doc);
            destArray.Elements.Add(target.Reference!);
            destArray.Elements.Add(new PdfName("/Fit"));
            var names = new PdfArray(doc);
            names.Elements.Add(new PdfString("capitulo2"));
            names.Elements.Add(destArray);
            var tree = new PdfDictionary(doc);
            tree.Elements["/Names"] = names;
            var nameDict = new PdfDictionary(doc);
            nameDict.Elements["/Dests"] = tree;
            doc.Internals.Catalog.Elements["/Names"] = nameDict;

            var link = new PdfDictionary(doc);
            link.Elements["/Type"] = new PdfName("/Annot");
            link.Elements["/Subtype"] = new PdfName("/Link");
            link.Elements["/Rect"] = new PdfRectangle(new XPoint(100, 100), new XPoint(200, 120));
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/GoTo");
            action.Elements["/D"] = new PdfString("capitulo2");
            link.Elements["/A"] = action;
            doc.Internals.AddObject(link);
            doc.Pages[0].Elements.GetArray("/Annots")!.Elements.Add(link.Reference!);
            doc.Save(input + ".tmp");
        }
        File.Move(input + ".tmp", input, overwrite: true);

        var links = PdfAnnotations.ListLinks(input);
        Assert.Equal([LinkTarget.ToUrl("https://example.com"), LinkTarget.ToPage(2)], links.Select(l => l.Target));
    }
}
