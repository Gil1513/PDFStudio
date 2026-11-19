using System.IO.Compression;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>"Adicionar fonte…" e a lista de fontes do documento.</summary>
public class FontFilesTests
{
    private static string AppFont(string name) => Path.Combine(FontLibrary.AppFontsFolder, name);

    [Fact]
    public void Inspect_ReadsFamilyAndStyle()
    {
        var info = FontFiles.Inspect(File.ReadAllBytes(AppFont("LiberationSans-Bold.ttf")));
        Assert.Equal(new FontFileInfo(FontFileKind.TrueType, "Liberation Sans", "Bold"), info);
    }

    [Fact]
    public void Inspect_RecognizesWhatCannotBeUsed()
    {
        byte[] cff = [(byte)'O', (byte)'T', (byte)'T', (byte)'O', 0, 0, 0, 0, 0, 0, 0, 0];
        Assert.Equal(FontFileKind.PostScript, FontFiles.Inspect(cff).Kind);
        Assert.Equal(FontFileKind.Invalid, FontFiles.Inspect("isto não é uma fonte"u8.ToArray()).Kind);
        byte[] broken = File.ReadAllBytes(AppFont("LiberationSans-Regular.ttf"))[..40]; // cortada: não pode quebrar
        Assert.Equal(FontFileKind.Invalid, FontFiles.Inspect(broken).Kind);
    }

    [Fact]
    public void Add_CopiesWithTheFamilyName_AndDoesNotDuplicate()
    {
        string folder = Path.Combine(TestPdfs.TempDir(), "fontes");
        string source = Path.Combine(TestPdfs.TempDir(), "qualquer-nome-123.ttf");
        File.Copy(AppFont("LiberationSans-Regular.ttf"), source);

        var result = FontFiles.Add(source, folder);
        Assert.Equal(["Liberation Sans"], result.Families);
        Assert.Empty(result.Problems);
        Assert.True(File.Exists(Path.Combine(folder, "LiberationSans-Regular.ttf"))); // a caixa de edição acha pelo nome

        FontFiles.Add(source, folder); // de novo: a mesma fonte não vira outro arquivo
        Assert.Single(Directory.GetFiles(folder));
    }

    [Fact]
    public void Add_FromZip_TakesTheFonts_AndExplainsTheRest()
    {
        string dir = TestPdfs.TempDir();
        string zipPath = Path.Combine(dir, "fontes.zip");
        using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
        {
            zip.CreateEntryFromFile(AppFont("LiberationSerif-Bold.ttf"), "pasta/../../Serif-Bold.ttf"); // nome estranho: ignorado
            using (var w = zip.CreateEntry("Postscript.otf").Open()) w.Write("OTTO\0\0\0\0\0\0\0\0"u8);
            using (var w = zip.CreateEntry("leia-me.txt").Open()) w.Write("licença"u8);
        }
        string folder = Path.Combine(dir, "fontes");

        var result = FontFiles.Add(zipPath, folder);

        Assert.Equal(["Liberation Serif"], result.Families);
        Assert.Contains(result.Problems, p => p.StartsWith("Postscript.otf") && p.Contains(".ttf"));
        Assert.Equal(["LiberationSerif-Bold.ttf"], Directory.GetFiles(folder).Select(Path.GetFileName));
        Assert.False(File.Exists(Path.Combine(dir, "..", "Serif-Bold.ttf")));
    }

    [Fact]
    public void DocumentFonts_ListsEachFont_WithWhatTheAppWouldUse()
    {
        string input = ContentBlockTests.Sample(TestPdfs.TempDir());

        var fonts = PdfDocumentFonts.List(input, isAvailable: name => name is "Arial");

        var arialBold = Assert.Single(fonts, f => f.Family == "Arial" && f.Bold);
        Assert.True(arialBold.IsExact);
        Assert.True(arialBold.Embedded); // o PDF Studio embute as fontes que escreve
        Assert.True(arialBold.Letters >= "Relatórioanual".Length);
        var times = Assert.Single(fonts, f => f.Family == "Times New Roman");
        Assert.False(times.IsExact); // "não temos": o app avisa que vai usar outra
        Assert.Equal("Arial", times.Choice.Use);
    }

    [Fact]
    public void SubsetPrefix_IsRemoved()
    {
        Assert.Equal("Arial-BoldMT", PdfDocumentFonts.StripSubset("ABCDEF+Arial-BoldMT"));
        Assert.Equal("Helvetica", PdfDocumentFonts.StripSubset("Helvetica"));
        Assert.Equal("Abcdef+X", PdfDocumentFonts.StripSubset("Abcdef+X"));
    }
}
