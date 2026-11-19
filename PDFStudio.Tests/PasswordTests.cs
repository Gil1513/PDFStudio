using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Etapa 6.1: colocar e remover senha.</summary>
public class PasswordTests
{
    private static string Doc(out string dir)
    {
        dir = TestPdfs.TempDir();
        return TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 2);
    }

    [Fact]
    public void OpenPassword_IsRequired_AndTheContentStaysTheSame()
    {
        string input = Doc(out string dir);
        string output = Path.Combine(dir, "protegido.pdf");

        PdfPasswords.Protect(input, new PasswordOptions("abc123"), output);

        Assert.Equal(PasswordState.OpenPassword, PdfPasswords.Inspect(output));
        // AES-128 (/AESV2): a de 256 bits (/AESV3) não abre no visualizador do Windows usado pelo app.
        string raw = System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(output));
        Assert.Contains("/AESV2", raw);
        Assert.DoesNotContain("/AESV3", raw);
        Assert.ThrowsAny<Exception>(() => PdfReader.Open(output, PdfDocumentOpenMode.Import).Dispose());
        Assert.Throws<PdfPasswordException>(() => PdfPasswords.Remove(output, "errada", Path.Combine(dir, "x.pdf")));
        using (var doc = PdfReader.Open(output, "abc123", PdfDocumentOpenMode.Import))
            Assert.Equal(2, doc.PageCount);
        // PdfPig também abre com a senha (outro leitor).
        using var pig = UglyToad.PdfPig.PdfDocument.Open(output, new UglyToad.PdfPig.ParsingOptions { Password = "abc123" });
        Assert.Contains("Página 1", pig.GetPage(1).Text);
    }

    [Fact]
    public void Remove_WithTheOnlyPassword_GivesBackAnOpenPdf()
    {
        string input = Doc(out string dir);
        string output = Path.Combine(dir, "protegido.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("abc123"), output);

        PdfPasswords.Remove(output, "abc123", output);

        Assert.Equal(PasswordState.None, PdfPasswords.Inspect(output));
        Assert.Equal(["Página 1", "Página 2"], TestPdfs.Labels(output));
    }

    [Fact]
    public void Restrictions_OpenWithoutPassword_AndNeedThePermissionsPassword()
    {
        string input = Doc(out string dir);
        string output = Path.Combine(dir, "restrito.pdf");

        PdfPasswords.Protect(input, new PasswordOptions("", "dono", AllowPrint: false, AllowCopy: false), output);

        Assert.Equal(PasswordState.Restricted, PdfPasswords.Inspect(output));
        using (var doc = PdfReader.Open(output, PdfDocumentOpenMode.Import))
        {
            Assert.False(doc.SecuritySettings.PermitPrint);
            Assert.False(doc.SecuritySettings.PermitExtractContent);
            Assert.True(doc.SecuritySettings.PermitAnnotations);
        }
        Assert.Throws<PdfPasswordException>(() => PdfPasswords.Remove(output, "", output));
        PdfPasswords.Remove(output, "dono", output);
        Assert.Equal(PasswordState.None, PdfPasswords.Inspect(output));
    }

    [Fact]
    public void OpenPassword_IsNotEnough_ToRemoveWhenThereIsAPermissionsPassword()
    {
        string input = Doc(out string dir);
        string output = Path.Combine(dir, "duas.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("abrir", "dono", AllowEdit: false), output);

        var ex = Assert.Throws<PdfPasswordException>(() => PdfPasswords.Remove(output, "abrir", Path.Combine(dir, "x.pdf")));
        Assert.Contains("permissões", ex.Message);
        PdfPasswords.Remove(output, "dono", output);
        Assert.Equal(PasswordState.None, PdfPasswords.Inspect(output));
    }

    [Fact]
    public void ChangingThePassword_NeedsTheCurrentOne()
    {
        string input = Doc(out string dir);
        string output = Path.Combine(dir, "p.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("velha"), output);

        PdfPasswords.Protect(output, new PasswordOptions("nova"), output, currentPassword: "velha");

        Assert.Throws<PdfPasswordException>(() => PdfPasswords.Remove(output, "velha", output));
        PdfPasswords.Remove(output, "nova", output);
        Assert.Equal(PasswordState.None, PdfPasswords.Inspect(output));
    }

    [Theory]
    [InlineData("", "")]
    [InlineData("igual", "igual")] // restrição com a mesma senha não protegeria nada
    [InlineData("abrir", "")]
    public void InvalidOptions_AreRejected(string open, string owner)
    {
        string input = Doc(out string dir);
        Assert.Throws<ArgumentException>(() =>
            PdfPasswords.Protect(input, new PasswordOptions(open, owner, AllowCopy: false), Path.Combine(dir, "x.pdf")));
    }
}
