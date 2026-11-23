using PDFStudio.Core;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Tests;

/// <summary>
/// Depois de muitas edições a página ficava com vários pedaços de conteúdo e o leitor do Windows a mostrava em
/// branco. Agora a página editada fica com um pedaço só, desenhando a mesma coisa.
/// </summary>
public class MergeContentTests
{
    private static int Pieces(string path, int page)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        return doc.Pages[page].Contents.Elements.Count;
    }

    [Fact]
    public void ManyEdits_PageKeepsOnePiece_AndAllTheText()
    {
        string dir = TestPdfs.TempDir();
        string input = ContentBlockTests.Sample(dir);
        PdfOperations.InsertBlankPages(input, 1, 1, input);
        var texts = PdfContentBlocks.Detect(input, 0).Where(b => b.Kind == BlockKind.Text).ToList();

        var column = texts.Single(b => b.Text == "Coluna esquerda");
        PdfTextEditor.Move(input, 0, [column], 0, 30, input);
        PdfTextEditor.CopyToPage(input, 0, [texts.Single(b => b.Text == "Coluna direita")], 0, 0, 40, input);
        var title = PdfContentBlocks.Detect(input, 0).Single(b => b.Text == "Relatório anual");
        PdfTextEditor.Replace(input, 0, title, "Relatório novo", input);
        PdfPageContent.AddText(input, 0, 60, 500, "Mais um texto", new PageTextStyle("Arial", 10, new AnnotationColor(0, 0, 0)), input);

        Assert.Equal(1, Pieces(input, 0));
        var now = PdfContentBlocks.Detect(input, 0).Where(b => b.Kind == BlockKind.Text).Select(b => b.Text).ToList();
        Assert.Contains("Relatório novo", now);
        Assert.Contains("Coluna esquerda", now);
        Assert.Equal(2, now.Count(t => t == "Coluna direita"));
        Assert.Contains("Mais um texto", now);
        Assert.Contains(now, t => t.StartsWith("Primeira linha"));
        // A página que não foi mexida fica como estava.
        Assert.True(Pieces(input, 1) <= 1);
    }
}
