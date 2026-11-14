namespace PDFStudio.Tests;

/// <summary>
/// Gera arquivos de exemplo em %TEMP%\PDFStudioSamples para testes manuais da interface.
/// Arquivos já existentes são mantidos (podem estar abertos no app).
/// </summary>
public class SampleFiles
{
    public static readonly string Folder = Path.Combine(Path.GetTempPath(), "PDFStudioSamples");

    [Fact]
    public void GenerateSamples()
    {
        Directory.CreateDirectory(Folder);
        CreateIfMissing("exemplo-12-paginas.pdf", 12, "Página", landscapeEvery: 5);
        CreateIfMissing("documento-A.pdf", 3, "A");
        CreateIfMissing("documento-B.pdf", 2, "B");

        // Certificado autoassinado de TESTE (senha: senha123). Não use em documentos reais.
        string pfx = Path.Combine(Folder, "certificado-teste.pfx");
        if (!File.Exists(pfx))
        {
            string tmp = TestPdfs.TempDir();
            File.Copy(SigningTests.CreatePfx(tmp, "Usuário de Teste PDF Studio", "senha123", 3650), pfx);
        }
        Assert.True(File.Exists(Path.Combine(Folder, "exemplo-12-paginas.pdf")));
    }

    private static void CreateIfMissing(string name, int pages, string prefix, int landscapeEvery = 0)
    {
        string path = Path.Combine(Folder, name);
        if (!File.Exists(path)) TestPdfs.Create(path, pages, prefix, landscapeEvery);
    }
}
