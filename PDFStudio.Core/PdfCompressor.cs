using System.IO.Compression;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Quanto comprimir.</summary>
public enum CompressLevel
{
    /// <summary>Só limpeza: o conteúdo fica idêntico (nenhuma imagem muda).</summary>
    Light,
    /// <summary>Fotos grandes reduzidas para no máximo 2000 pixels, JPG de boa qualidade. Bom para enviar por e-mail.</summary>
    Medium,
    /// <summary>Fotos reduzidas para no máximo 1200 pixels, JPG mais leve. Arquivo bem menor; fotos perdem detalhe.</summary>
    Strong,
}

/// <summary>
/// Uma imagem do PDF entregue para ser reduzida. <paramref name="Data"/>: o arquivo JPG
/// (<paramref name="IsJpeg"/>) ou os pixels crus, 8 bits por cor (<paramref name="Components"/>: 1 = cinza, 3 = RGB).
/// </summary>
public sealed record PdfImageData(byte[] Data, bool IsJpeg, int Width, int Height, int Components);

/// <summary>A imagem reduzida, já em JPG.</summary>
public sealed record ShrunkImage(byte[] Jpeg, int Width, int Height);

/// <summary>Tamanho antes e depois, e quantas imagens foram reduzidas.</summary>
public sealed record CompressResult(long Before, long After, int ImagesReduced)
{
    /// <summary>Quanto diminuiu, de 0 a 1 (0 = nada).</summary>
    public double Saved => Before <= 0 ? 0 : Math.Max(0, 1 - (double)After / Before);
}

/// <summary>
/// Etapa 7.6: comprimir PDF. Grava sempre um arquivo novo. Sempre faz a limpeza sem perda (compacta o que está
/// sem compactar, tira miniaturas de página e dados privados de outros programas, descarta objetos que nada usa).
/// Nos níveis Médio e Forte, reduz as fotos grandes usando <c>shrink</c>, que faz a conta com o motor de imagens do
/// Windows no app (aqui no Core não há decodificador de JPG). A imagem só é trocada se ficar bem menor.
/// </summary>
public static class PdfCompressor
{
    /// <summary>Lado máximo e qualidade do JPG de cada nível.</summary>
    public static (int MaxSide, int Quality) Settings(CompressLevel level) => level switch
    {
        CompressLevel.Medium => (2000, 80),
        CompressLevel.Strong => (1200, 60),
        _ => (0, 0),
    };

    /// <summary>Imagem pequena (menos que isso em pixels) não compensa mexer.</summary>
    private const int MinPixels = 250_000;

    public static CompressResult Compress(string input, string output, CompressLevel level,
        Func<PdfImageData, int, int, ShrunkImage?>? shrink = null, string? password = null, IProgress<double>? progress = null)
    {
        long before = new FileInfo(input).Length;
        using var doc = string.IsNullOrEmpty(password)
            ? PdfReader.Open(input, PdfDocumentOpenMode.Modify)
            : PdfReader.Open(input, password, PdfDocumentOpenMode.Modify);

        // Dados que só servem para outros programas (Illustrator etc.) e miniaturas antigas de página.
        doc.Internals.Catalog.Elements.Remove("/PieceInfo");
        foreach (var page in doc.Pages)
        {
            page.Elements.Remove("/Thumb");
            page.Elements.Remove("/PieceInfo");
        }

        var (maxSide, quality) = Settings(level);
        int reduced = 0;
        var objects = doc.Internals.GetAllObjects().OfType<PdfDictionary>().Where(d => d.Stream is not null).ToList();
        for (int i = 0; i < objects.Count; i++)
        {
            var dict = objects[i];
            if (dict.Elements.GetName("/Subtype") == "/Image")
            {
                if (shrink is not null && maxSide > 0 && TryShrink(dict, maxSide, quality, shrink)) reduced++;
            }
            else
            {
                Deflate(dict);
            }
            progress?.Report((i + 1.0) / objects.Count);
        }

        doc.Options.CompressContentStreams = true;
        doc.Options.NoCompression = false;
        doc.Save(output); // o PDFsharp só grava os objetos que alguma página ou o catálogo usam
        long after = new FileInfo(output).Length;
        // Arquivo que já estava compacto pode sair um pouco maior (o PDFsharp regrava a estrutura). Comprimir nunca
        // deve aumentar o arquivo: nesse caso o resultado é uma cópia idêntica do original.
        if (after >= before && !SameFile(input, output))
        {
            File.Copy(input, output, overwrite: true);
            return new CompressResult(before, before, 0);
        }
        return new CompressResult(before, after, reduced);
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>Compacta (Flate) um fluxo que está gravado sem compactação nenhuma.</summary>
    private static void Deflate(PdfDictionary dict)
    {
        if (dict.Elements.ContainsKey("/Filter") || dict.Elements.ContainsKey("/DecodeParms")) return;
        // O XMP fica legível de propósito (alguns programas leem sem descompactar).
        if (dict.Elements.GetName("/Type") == "/Metadata") return;
        byte[] data = dict.Stream!.Value;
        if (data.Length < 64) return;
        byte[] packed = Zlib(data);
        if (packed.Length >= data.Length) return;
        dict.Stream.Value = packed;
        dict.Elements.SetName("/Filter", "/FlateDecode");
    }

    internal static byte[] Zlib(byte[] data)
    {
        using var ms = new MemoryStream();
        using (var z = new ZLibStream(ms, CompressionLevel.SmallestSize, leaveOpen: true)) z.Write(data);
        return ms.ToArray();
    }

    /// <summary>
    /// Reduz uma imagem se for do tipo seguro de mexer: 8 bits, cinza ou RGB, JPG ou Flate sem truques
    /// (sem máscara de cor, sem "Decode", sem preditor). Devolve true se trocou.
    /// </summary>
    private static bool TryShrink(PdfDictionary image, int maxSide, int quality, Func<PdfImageData, int, int, ShrunkImage?> shrink)
    {
        var e = image.Elements;
        int width = e.GetInteger("/Width"), height = e.GetInteger("/Height");
        if ((long)width * height < MinPixels) return false;
        if (e.GetBoolean("/ImageMask") || e.ContainsKey("/Mask") || e.ContainsKey("/Decode")) return false;
        if (e.GetInteger("/BitsPerComponent") != 8) return false;
        if (e.GetValue("/SMask") is PdfReference { Value: PdfDictionary mask } && mask.Elements.ContainsKey("/Matte")) return false;
        int components = Components(e.GetValue("/ColorSpace"));
        if (components is not (1 or 3)) return false;

        string? filter = SingleFilter(e.GetValue("/Filter"));
        byte[] current = image.Stream!.Value;
        PdfImageData data;
        if (filter == "/DCTDecode")
        {
            data = new PdfImageData(current, true, width, height, components);
        }
        else if (filter is null or "/FlateDecode")
        {
            if (e.GetValue("/DecodeParms") is PdfDictionary parms && parms.Elements.GetInteger("/Predictor") > 1) return false;
            byte[] pixels = filter is null ? current : image.Stream.UnfilteredValue;
            if (pixels.Length != width * height * components) return false; // não deu para ler direito
            data = new PdfImageData(pixels, false, width, height, components);
        }
        else
        {
            return false; // JBIG2, CCITT, JPX…: formatos especiais, ficam como estão
        }

        ShrunkImage? result;
        try { result = shrink(data, maxSide, quality); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return false; } // imagem estranha: fica como está
        if (result is null || result.Jpeg.Length > current.Length * 0.9) return false; // não compensa

        image.Stream.Value = result.Jpeg;
        e.SetName("/Filter", "/DCTDecode");
        e.Remove("/DecodeParms");
        e.SetInteger("/Width", result.Width);
        e.SetInteger("/Height", result.Height);
        e.SetInteger("/BitsPerComponent", 8);
        return true;
    }

    private static string? SingleFilter(PdfItem? filter) => filter switch
    {
        null => null,
        PdfName name => name.Value,
        PdfArray { Elements.Count: 1 } array => (array.Elements[0] as PdfName)?.Value,
        PdfArray { Elements.Count: 0 } => null,
        _ => "?", // mais de um filtro: não mexe
    };

    /// <summary>Quantas cores por pixel o espaço de cor tem (0 = não sabemos ou não mexemos, ex.: CMYK, paleta).</summary>
    private static int Components(PdfItem? colorSpace)
    {
        if (colorSpace is PdfReference reference) colorSpace = reference.Value;
        switch (colorSpace)
        {
            case PdfName { Value: "/DeviceGray" or "/CalGray" }: return 1;
            case PdfName { Value: "/DeviceRGB" or "/CalRGB" }: return 3;
            case PdfArray { Elements.Count: >= 2 } array when (array.Elements[0] as PdfName)?.Value == "/ICCBased":
                var profile = array.Elements[1] is PdfReference r ? r.Value as PdfDictionary : array.Elements[1] as PdfDictionary;
                int n = profile?.Elements.GetInteger("/N") ?? 0;
                return n is 1 or 3 ? n : 0;
            case PdfArray { Elements.Count: >= 1 } calibrated when (calibrated.Elements[0] as PdfName)?.Value is "/CalRGB":
                return 3;
            case PdfArray { Elements.Count: >= 1 } calGray when (calGray.Elements[0] as PdfName)?.Value is "/CalGray":
                return 1;
            default: return 0;
        }
    }
}
