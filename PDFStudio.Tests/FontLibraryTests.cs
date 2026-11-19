using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 5.10: fontes gêmeas (vêm com o app) e Google Fonts.</summary>
public class FontLibraryTests
{
    [Theory]
    [InlineData("Calibri", "Carlito", FontSource.Twin)]
    [InlineData("Cambria", "Caladea", FontSource.Twin)]
    [InlineData("Helvetica", "Liberation Sans", FontSource.Twin)]
    [InlineData("Times", "Liberation Serif", FontSource.Twin)]
    [InlineData("Courier", "Liberation Mono", FontSource.Twin)]
    [InlineData("Georgia", "Georgia", FontSource.Original)]
    [InlineData("Helvetica Neue", "Liberation Sans", FontSource.Twin)]   // parecida = Arial, que aqui "não existe": usa a gêmea dela
    [InlineData("Roboto", "Liberation Sans", FontSource.Fallback)]
    public void Resolve_PrefersOriginal_ThenTwin_ThenSimilar(string original, string use, FontSource source)
    {
        // Computador "sem Office": sem Calibri/Cambria/Arial/Times/Courier, mas com Georgia e as gêmeas do app.
        string[] available = ["Georgia", "Carlito", "Caladea", "Liberation Sans", "Liberation Serif", "Liberation Mono"];
        var choice = FontLibrary.Resolve(original, f => available.Contains(f, StringComparer.OrdinalIgnoreCase));
        Assert.Equal((use, source), (choice.Use, choice.Source));
    }

    [Fact]
    public void CanTryDownload_OnlyWhenThereIsNoOriginalNorTwin()
    {
        bool Has(string f) => f is "Arial" or "Carlito";
        Assert.True(FontLibrary.Resolve("Roboto", Has).CanTryDownload);
        Assert.False(FontLibrary.Resolve("Calibri", Has).CanTryDownload);
        Assert.False(FontLibrary.Resolve("Arial", Has).CanTryDownload);
        Assert.False(FontLibrary.Resolve("", Has).CanTryDownload);
    }

    [Theory]
    [InlineData("Carlito")]
    [InlineData("Caladea")]
    [InlineData("Liberation Sans")]
    [InlineData("Liberation Serif")]
    [InlineData("Liberation Mono")]
    public void BundledTwins_AreRegisteredAndWrittenIntoThePdf(string family)
    {
        Assert.True(FontLibrary.IsAvailable(family));
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        PdfPageContent.AddText(input, 0, 50, 300, "Fonte gemea", new PageTextStyle(family, 14, new AnnotationColor(0, 0, 0), Bold: true), input);

        using var pig = UglyToad.PdfPig.PdfDocument.Open(input);
        var letter = pig.GetPage(1).Letters.First(l => l.Value == "F");
        Assert.Contains(family.Replace(" ", ""), letter.FontName.Replace(" ", ""));
    }

    [Fact]
    public void TextEditor_UsesTheTwin_WhenTheOriginalIsMissing()
    {
        // Sem a Helvetica (finge que falta, mesmo que alguém tenha adicionado a fonte): usa a gêmea.
        static bool SemHelvetica(string name) => name != "Helvetica" && FontLibrary.IsAvailable(name);
        Assert.Equal("Liberation Sans", FontLibrary.Resolve("Helvetica", SemHelvetica).Use);
        Assert.Equal(PdfTextEditor.ChooseFont("FonteQueNaoExiste"), FontLibrary.Resolve("FonteQueNaoExiste").Use);
    }

    [Fact]
    public void GoogleCss_KeepsOnlyTrueTypeFromTheOfficialServer()
    {
        const string css = """
            /* latin */
            @font-face {
              font-family: 'Roboto';
              font-style: normal;
              font-weight: 400;
              src: url(https://fonts.gstatic.com/s/roboto/v30/KFOmCnqEu92Fr1Me5Q.ttf) format('truetype');
            }
            @font-face {
              font-family: 'Roboto';
              font-style: italic;
              font-weight: 700;
              src: url(https://fonts.gstatic.com/s/roboto/v30/KFOjCnqEu92Fr1Mu51TzBic6CsE.ttf) format('truetype');
            }
            @font-face {
              font-family: 'Roboto';
              font-style: normal;
              font-weight: 700;
              src: url(https://outro-site.example/roboto.ttf) format('truetype');
            }
            @font-face {
              font-family: 'Roboto';
              font-style: normal;
              font-weight: 400;
              src: url(https://fonts.gstatic.com/s/roboto/v30/abc.woff2) format('woff2');
            }
            """;
        var faces = GoogleFonts.ParseCss(css);
        Assert.Equal(2, faces.Count);
        Assert.Equal(("Roboto", "Regular"), (faces[0].Family, faces[0].Style));
        Assert.Equal("BoldItalic", faces[1].Style);
    }

    [Theory]
    [InlineData("")]
    [InlineData("../../Windows")]
    [InlineData("Fonte<script>")]
    public async Task Download_IgnoresStrangeNames_WithoutTouchingTheNetwork(string family)
    {
        using var http = new HttpClient(new NoNetwork());
        Assert.Empty(await GoogleFonts.DownloadAsync(family, http));
    }

    private sealed class NoNetwork : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("O teste não deveria acessar a internet.");
    }
}
