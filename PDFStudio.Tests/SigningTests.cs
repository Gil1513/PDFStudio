using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;
using PDFStudio.Core;

namespace PDFStudio.Tests;

public class SigningTests
{
    private const string Password = "senha123";

    /// <summary>Cria um certificado autoassinado .pfx de teste.</summary>
    public static string CreatePfx(string dir, string name = "Fulano de Tal", string password = Password, int validDays = 365)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={name}, O=PDF Studio Testes, C=BR", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.NonRepudiation, true));
        using var cert = req.CreateSelfSigned(DateTimeOffset.Now.AddDays(-1), DateTimeOffset.Now.AddDays(validDays));
        string path = Path.Combine(dir, "teste.pfx");
        File.WriteAllBytes(path, cert.Export(X509ContentType.Pfx, password));
        return path;
    }

    /// <summary>
    /// Valida a assinatura como um leitor de PDF faria: o /ByteRange deve cobrir o arquivo inteiro
    /// (exceto o próprio /Contents) e o CMS deve conferir com esses bytes.
    /// </summary>
    public static SignedCms VerifySignature(string pdfPath)
    {
        byte[] bytes = File.ReadAllBytes(pdfPath);
        string text = Encoding.Latin1.GetString(bytes);

        var br = Regex.Matches(text, @"/ByteRange\s*\[\s*(\d+)\s+(\d+)\s+(\d+)\s+(\d+)\s*\]");
        Assert.Single(br);
        long a = long.Parse(br[0].Groups[1].Value), b = long.Parse(br[0].Groups[2].Value),
             c = long.Parse(br[0].Groups[3].Value), d = long.Parse(br[0].Groups[4].Value);
        Assert.Equal(0, a);
        Assert.Equal(bytes.Length, c + d); // cobre até o fim do arquivo

        // O trecho excluído é exatamente o valor hexadecimal de /Contents.
        string hole = text[(int)b..(int)c];
        Assert.StartsWith("<", hole);
        Assert.EndsWith(">", hole);
        byte[] cms = TrimDer(Convert.FromHexString(hole[1..^1]));

        byte[] signedData = [.. bytes.AsSpan(0, (int)b), .. bytes.AsSpan((int)c, (int)d)];
        var signed = new SignedCms(new ContentInfo(signedData), detached: true);
        signed.Decode(cms);
        signed.CheckSignature(verifySignatureOnly: true); // lança exceção se não conferir
        return signed;
    }

    /// <summary>Remove o preenchimento de zeros após a estrutura DER (SEQUENCE).</summary>
    private static byte[] TrimDer(byte[] data)
    {
        Assert.Equal(0x30, data[0]);
        int lenByte = data[1], header = 2, length = lenByte;
        if (lenByte > 0x80)
        {
            int n = lenByte & 0x7F;
            length = 0;
            for (int i = 0; i < n; i++) length = (length << 8) | data[2 + i];
            header = 2 + n;
        }
        return data[..(header + length)];
    }

    /// <summary>Conteúdo (descomprimido) do desenho /AP /N do widget de assinatura.</summary>
    private static string AppearanceStream(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        foreach (PdfPage page in doc.Pages)
        {
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var n = annots.Elements.GetDictionary(i)?.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
                if (n?.Stream is { } s)
                {
                    s.TryUncompress();
                    return Encoding.Latin1.GetString(s.Value);
                }
            }
        }
        throw new Xunit.Sdk.XunitException("Aparência da assinatura não encontrada.");
    }

    /// <summary>Retorna (índice da página, /Rect) do widget da assinatura.</summary>
    private static (int Page, double[] Rect) FindSignatureWidget(string pdfPath)
    {
        using var doc = PdfReader.Open(pdfPath, PdfDocumentOpenMode.Import);
        for (int p = 0; p < doc.PageCount; p++)
        {
            var annots = doc.Pages[p].Elements.GetArray("/Annots");
            if (annots is null) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var annot = annots.Elements.GetDictionary(i);
                if (annot is null) continue;
                bool isSig = annot.Elements.GetName("/FT") == "/Sig"
                             || annot.Elements.GetDictionary("/Parent")?.Elements.GetName("/FT") == "/Sig";
                if (!isSig) continue;
                var rect = annot.Elements.GetArray("/Rect")!;
                return (p, Enumerable.Range(0, 4).Select(k => rect.Elements.GetReal(k)).ToArray());
            }
        }
        throw new Xunit.Sdk.XunitException("Widget de assinatura não encontrado.");
    }

    [Fact]
    public async Task Sign_Invisible_ProducesValidSignature()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        string output = Path.Combine(dir, "assinado.pdf");

        var summary = await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = output, PfxPath = CreatePfx(dir), PfxPassword = Password,
            Visible = false, Reason = "Aprovação", Location = "São Paulo",
        });

        Assert.Equal("Fulano de Tal", summary.Name);
        var cms = VerifySignature(output);
        Assert.Contains("Fulano de Tal", cms.SignerInfos[0].Certificate!.Subject);
        Assert.True(PdfSigning.HasSignatures(output));
        Assert.False(PdfSigning.HasSignatures(input));
        Assert.Equal(["Página 1", "Página 2", "Página 3"], TestPdfs.Labels(output));

        var (_, rect) = FindSignatureWidget(output);
        Assert.Equal(0, rect[2] - rect[0]); // invisível: retângulo de tamanho zero
        Assert.Equal(0, rect[3] - rect[1]);
    }

    [Theory]
    [InlineData(SignaturePosition.BottomRight)]
    [InlineData(SignaturePosition.TopLeft)]
    public async Task Sign_Visible_PlacesStampOnChosenPageAndCorner(SignaturePosition position)
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 3);
        string output = Path.Combine(dir, "assinado.pdf");

        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = output, PfxPath = CreatePfx(dir), PfxPassword = Password,
            Visible = true, PageNumber = 2, Position = position, Reason = "Teste",
        });

        VerifySignature(output);
        var (page, r) = FindSignatureWidget(output);
        Assert.Equal(1, page);

        // O retângulo desenhado no carimbo precisa estar dentro da BBox (0,0,largura,altura).
        string appearance = AppearanceStream(output);
        var re = Regex.Match(appearance, @"(-?[\d.]+) (-?[\d.]+) (-?[\d.]+) (-?[\d.]+) re");
        Assert.True(re.Success, "o carimbo não desenhou a moldura");
        double rx = double.Parse(re.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        double ry = double.Parse(re.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture);
        Assert.InRange(rx, 0, PdfSigning.StampWidth);
        Assert.InRange(ry, 0, PdfSigning.StampHeight);
        Assert.Contains("Fulano de Tal", appearance);
        double width = Math.Abs(r[2] - r[0]), height = Math.Abs(r[3] - r[1]);
        Assert.Equal(PdfSigning.StampWidth, width, 1);
        Assert.Equal(PdfSigning.StampHeight, height, 1);

        // Coordenadas do PDF: origem no canto INFERIOR esquerdo. Página A4 = 595 x 842 pt.
        double left = Math.Min(r[0], r[2]), bottom = Math.Min(r[1], r[3]);
        if (position == SignaturePosition.BottomRight)
        {
            Assert.Equal(595 - PdfSigning.StampMargin - PdfSigning.StampWidth, left, 0);
            Assert.Equal(PdfSigning.StampMargin, bottom, 0);
        }
        else
        {
            Assert.Equal(PdfSigning.StampMargin, left, 0);
            Assert.Equal(842 - PdfSigning.StampMargin - PdfSigning.StampHeight, bottom, 0);
        }
    }

    [Fact]
    public async Task Sign_CanOverwriteInput()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);

        await PdfSigning.SignAsync(new SignatureRequest
        {
            InputPath = input, OutputPath = input, PfxPath = CreatePfx(dir), PfxPassword = Password,
        });

        VerifySignature(input);
        Assert.False(File.Exists(input + ".tmp"));
    }

    [Fact]
    public async Task Sign_TamperedFile_FailsVerification()
    {
        string dir = TestPdfs.TempDir();
        string input = TestPdfs.Create(Path.Combine(dir, "doc.pdf"), 1);
        string output = Path.Combine(dir, "assinado.pdf");
        await PdfSigning.SignAsync(new SignatureRequest { InputPath = input, OutputPath = output, PfxPath = CreatePfx(dir), PfxPassword = Password });

        // Altera um byte coberto pela assinatura: o dígito da versão em "%PDF-1.x".
        byte[] bytes = File.ReadAllBytes(output);
        Assert.Equal("%PDF-1.", Encoding.ASCII.GetString(bytes, 0, 7));
        bytes[7] = bytes[7] == (byte)'9' ? (byte)'8' : (byte)(bytes[7] + 1);
        File.WriteAllBytes(output, bytes);

        Assert.ThrowsAny<CryptographicException>(() => VerifySignature(output));
    }

    [Fact]
    public void WrongPassword_GivesFriendlyError()
    {
        string dir = TestPdfs.TempDir();
        string pfx = CreatePfx(dir);
        var ex = Assert.Throws<InvalidOperationException>(() => PdfSigning.InspectCertificate(pfx, "errada"));
        Assert.Contains("senha", ex.Message);
    }

    [Fact]
    public void InspectCertificate_ReturnsDetails()
    {
        string dir = TestPdfs.TempDir();
        var info = PdfSigning.InspectCertificate(CreatePfx(dir, "Maria Teste"), Password);
        Assert.Equal("Maria Teste", info.Name);
        Assert.True(info.HasPrivateKey);
        Assert.True(info.IsValidNow);
    }
}
