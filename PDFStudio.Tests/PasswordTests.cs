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

    [Fact]
    public void Permissions_AreReadFromTheFile_AndFailClosed()
    {
        string input = Doc(out string dir);
        Assert.Equal(PdfPermissions.All, PdfPasswords.Permissions(input));

        string restricted = Path.Combine(dir, "restrito.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("", "dono123", AllowPrint: true, AllowCopy: false, AllowEdit: false, AllowComments: true), restricted);
        var p = PdfPasswords.Permissions(restricted);
        Assert.True(p.Print);
        Assert.False(p.Copy);
        Assert.False(p.Edit);
        Assert.True(p.Comments);
        Assert.Equal(PdfPermissions.All, PdfPasswords.Permissions(restricted, "dono123")); // a senha de permissões libera tudo

        string locked = Path.Combine(dir, "fechado.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("abrir", "dono123", AllowPrint: false, AllowCopy: false), locked);
        Assert.False(PdfPasswords.Permissions(locked, "abrir").Copy);
        Assert.Equal(PdfPermissions.Nothing, PdfPasswords.Permissions(locked, "errada")); // não deu para conferir: nada
        Assert.Equal(PdfPermissions.Nothing, PdfPasswords.Permissions(locked));
        Assert.False(PdfPasswords.AllowsPrinting(locked, "errada"));
    }

    [Fact]
    public void IsEncrypted_LooksAtTheTrailer_NotAtTextInsideThePage()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1, prefix: "/Encrypt aparece no texto");
        // Uma string "/Encrypt" solta no arquivo (aqui, no título) não torna o arquivo protegido.
        var meta = DocumentPropertiesService.Read(input).Metadata;
        DocumentPropertiesService.Save(input, meta with { Title = "/Encrypt no titulo" }, input);
        Assert.Contains("/Encrypt", System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(input)));
        Assert.False(PdfPasswords.IsEncrypted(input));
        Assert.Equal(PasswordState.None, PdfPasswords.Inspect(input));

        string locked = Path.Combine(dir, "fechado.pdf");
        PdfPasswords.Protect(input, new PasswordOptions("abrir"), locked);
        Assert.True(PdfPasswords.IsEncrypted(locked));
    }

    [Theory]
    [InlineData(1024 * 1024 - 3, true)]  // "/Encrypt" cortado entre dois pedaços da leitura
    [InlineData(10, true)]
    [InlineData(-1, false)]              // a palavra não aparece
    public void IsEncrypted_UnknownStructure_SearchesTheWholeFile(int at, bool expected)
    {
        string path = Path.Combine(TestPdfs.TempDir(), "estranho.bin");
        var bytes = new byte[2 * 1024 * 1024];
        Array.Fill(bytes, (byte)'x');
        if (at >= 0) "/Encrypt"u8.CopyTo(bytes.AsSpan(at));
        File.WriteAllBytes(path, bytes);
        Assert.Equal(expected, PdfPasswords.IsEncrypted(path));
    }

    [Fact]
    public void ForgottenWorkFolders_AreRemoved_UnlessInUse()
    {
        string root = TestPdfs.TempDir();
        string input = Doc(out _);
        using var open = new WorkingCopy(input, root);         // em uso por "outra instância"
        string orphan = Path.Combine(root, "abandonada");       // app fechado à força: sem o arquivo de "em uso"
        Directory.CreateDirectory(orphan);
        File.WriteAllText(Path.Combine(orphan, "v1.pdf"), "sem senha");
        Directory.SetCreationTimeUtc(orphan, DateTime.UtcNow.AddMinutes(-5));
        Directory.SetCreationTimeUtc(Directory.GetDirectories(root).First(d => d != orphan), DateTime.UtcNow.AddMinutes(-5));

        WorkingCopy.CleanupStale(root);

        Assert.False(Directory.Exists(orphan));
        Assert.True(File.Exists(open.CurrentPath));
    }

    [Fact]
    public void LooseTemporaryCopies_AreRemoved()
    {
        string root = TestPdfs.TempDir();
        Directory.CreateDirectory(Path.Combine(root, "impressao"));
        string old = Path.Combine(root, "senha-abc.pdf"), print = Path.Combine(root, "impressao", "x.pdf"), fresh = Path.Combine(root, "teste-senha-novo.pdf");
        foreach (var f in new[] { old, print, fresh }) File.WriteAllText(f, "x");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddHours(-1));
        File.SetLastWriteTimeUtc(print, DateTime.UtcNow.AddHours(-1));

        Assert.Equal(2, WorkingCopy.CleanupLooseCopies(root));
        Assert.True(File.Exists(fresh)); // recente: pode estar sendo usado agora
    }
}
