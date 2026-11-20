using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 7.9: ler em voz alta (o texto dividido em frases, com a posição para o destaque).</summary>
public class ReadAloudTests
{
    /// <summary>Palavras de mentira: cada linha a 14 pontos da anterior (letra de 10), palavras lado a lado.</summary>
    private static List<TextWord> Lines(params string[] lines) => Lines(14, lines);

    private static List<TextWord> Lines(double pitch, string[] lines)
    {
        var words = new List<TextWord>();
        for (int l = 0; l < lines.Length; l++)
        {
            double x = 50;
            foreach (var t in lines[l].Split(' ', StringSplitOptions.RemoveEmptyEntries))
            {
                words.Add(new TextWord(t, x, 100 + l * pitch, t.Length * 5, 10, l));
                x += t.Length * 5 + 4;
            }
        }
        return words;
    }

    [Fact]
    public void Sentences_AreSplit_AtTheFullStop()
    {
        var chunks = ReadAloud.Chunks(Lines("Primeira frase aqui. Segunda", "frase começa e termina! Terceira?"), 3);

        Assert.Equal(["Primeira frase aqui.", "Segunda frase começa e termina!", "Terceira?"], chunks.Select(c => c.Text));
        Assert.All(chunks, c => Assert.Equal(3, c.Page));
        Assert.Equal(2, chunks[1].Boxes.Count); // a segunda frase passa por duas linhas: um destaque por linha
        Assert.Equal(100, chunks[1].Boxes[0].Y, 0.1);
        Assert.Equal(114, chunks[1].Boxes[1].Y, 0.1);
    }

    [Fact]
    public void Title_WithoutFullStop_IsReadApart()
    {
        // Título, linha em branco e o parágrafo: o título não se mistura com a primeira frase.
        var words = Lines(14, ["Relatório anual", "", "O ano foi bom para", "a empresa."]);
        Assert.Equal(["Relatório anual", "O ano foi bom para a empresa."], ReadAloud.Chunks(words, 0).Select(c => c.Text));
    }

    [Fact]
    public void HyphenatedWord_IsReadWhole()
    {
        var chunks = ReadAloud.Chunks(Lines("O compu-", "tador ligou."), 0);
        Assert.Equal("O computador ligou.", Assert.Single(chunks).Text);
    }

    [Fact]
    public void LongTextWithoutFullStop_IsCut()
    {
        string line = string.Join(" ", Enumerable.Repeat("palavra", 20));
        var chunks = ReadAloud.Chunks(Lines(line, line, line, line, line), 0); // 100 palavras, nenhum ponto
        Assert.Equal(3, chunks.Count);
        Assert.All(chunks, c => Assert.True(c.Text.Split(' ').Length <= ReadAloud.MaxWords));
    }

    [Theory]
    [InlineData("fim.", true)]
    [InlineData("certo?", true)]
    [InlineData("(nota.)", true)]
    [InlineData("disse…", true)]
    [InlineData("Sr.", false)]
    [InlineData("Dr.", false)]
    [InlineData("1.5", false)]
    [InlineData("palavra", false)]
    public void EndOfSentence_IsRecognized(string word, bool ends) => Assert.Equal(ends, ReadAloud.EndsSentence(word));

    [Fact]
    public void RealPdf_StartsAtTheChosenPage_AndSkipsEmptyPages()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "tres.pdf"), 3, prefix: "");
        PdfPageContent.AddText(input, 2, 60, 200, "Última página. Fim do texto.", new PageTextStyle("Arial", 12, new AnnotationColor(0, 0, 0)), input);
        var pages = PdfTextLayout.AllWords(input);

        var chunks = ReadAloud.Chunks(pages, firstPage: 2);

        Assert.All(chunks, c => Assert.Equal(2, c.Page)); // as páginas 1 e 2 ficam de fora
        Assert.Contains("Última página.", chunks.Select(c => c.Text));
        Assert.Contains("Fim do texto.", chunks.Select(c => c.Text));
        var last = chunks.Single(c => c.Text == "Fim do texto.");
        Assert.InRange(last.Boxes[0].Y, 195, 215); // o destaque fica onde o texto está
    }
}
