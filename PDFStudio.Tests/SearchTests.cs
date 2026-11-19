using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.1: pesquisar no documento.</summary>
public class SearchTests
{
    /// <summary>Uma página com as palavras em linhas (cada palavra com 10 pontos por letra, linhas a cada 20 pontos).</summary>
    private static List<TextWord> Page(params string[] lines)
    {
        var words = new List<TextWord>();
        for (int line = 0; line < lines.Length; line++)
        {
            double x = 50;
            foreach (string w in lines[line].Split(' '))
            {
                words.Add(new TextWord(w, x, 100 + line * 20, w.Length * 10, 14, line));
                x += w.Length * 10 + 5;
            }
        }
        return words;
    }

    [Fact]
    public void IgnoresCaseAndAccents_ByDefault()
    {
        var pages = new List<IReadOnlyList<TextWord>> { Page("A Ação do contrato"), Page("acao final", "CONTRATO assinado") };

        var hits = PdfSearch.Find(pages, "acao");

        Assert.Equal([0, 1], hits.Select(h => h.Page));
        Assert.Equal("Ação", hits[0].Match); // devolve o texto como está no PDF
        Assert.Equal(2, PdfSearch.Find(pages, "contrato").Count);
    }

    [Fact]
    public void Options_MatchCase_Accents_AndWholeWord()
    {
        var pages = new List<IReadOnlyList<TextWord>> { Page("Contrato contratos contrato Ação acao") };

        Assert.Single(PdfSearch.Find(pages, "Contrato", new SearchOptions(MatchCase: true)));
        Assert.Equal(3, PdfSearch.Find(pages, "contrato").Count);
        Assert.Equal(2, PdfSearch.Find(pages, "contrato", new SearchOptions(WholeWord: true)).Count);
        Assert.Single(PdfSearch.Find(pages, "ação", new SearchOptions(MatchAccents: true)));
    }

    [Fact]
    public void PartOfAWord_GetsAPartialBox()
    {
        var pages = new List<IReadOnlyList<TextWord>> { Page("contrato") };

        var hit = Assert.Single(PdfSearch.Find(pages, "trat"));

        var box = Assert.Single(hit.Boxes);
        Assert.Equal(50 + 3 * 10, box.X, 3); // "con" fica de fora
        Assert.Equal(4 * 10, box.Width, 3);
        Assert.Equal(100, box.Y, 3);
    }

    [Fact]
    public void Phrase_AcrossTwoLines_IsFound_WithOneBoxPerLine()
    {
        var pages = new List<IReadOnlyList<TextWord>> { Page("assinado pelo", "cliente hoje") };

        var hit = Assert.Single(PdfSearch.Find(pages, "pelo   cliente")); // espaços repetidos contam como um

        Assert.Equal(2, hit.Boxes.Count);
        Assert.Equal("assinado ", hit.Before);
        Assert.Equal(" hoje", hit.After);
    }

    [Fact]
    public void Context_IsCutWithEllipsis()
    {
        string longText = string.Join(' ', Enumerable.Repeat("palavra", 20));
        var pages = new List<IReadOnlyList<TextWord>> { Page(longText + " alvo " + longText) };

        var hit = Assert.Single(PdfSearch.Find(pages, "alvo"));

        Assert.StartsWith("…", hit.Before);
        Assert.EndsWith("…", hit.After);
    }

    [Fact]
    public void EmptyQuery_FindsNothing()
    {
        var pages = new List<IReadOnlyList<TextWord>> { Page("texto") };
        Assert.Empty(PdfSearch.Find(pages, "   "));
    }

    [Fact]
    public void RealPdf_FindsTheLabelOfEachPage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);

        var hits = PdfSearch.Find(input, "pagina");

        Assert.Equal([0, 1, 2], hits.Select(h => h.Page));
        Assert.Equal(["Página 2"], PdfSearch.Find(input, "página 2").Select(h => h.Match));
        var box = hits[0].Boxes[0];
        Assert.InRange(box.Y, 300, 500); // o rótulo fica no meio da página A4 (842 pt de altura)
    }
}
