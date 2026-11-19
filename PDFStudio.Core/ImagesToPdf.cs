using System.Buffers.Binary;
using PdfSharp.Drawing;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

/// <summary>Tamanho das páginas do PDF criado a partir de imagens.</summary>
public enum ImagePageSize
{
    /// <summary>Cada página do tamanho da sua imagem.</summary>
    SameAsImage,
    A4,
    /// <summary>Carta (EUA), 8,5 × 11 pol.</summary>
    Letter,
}

/// <summary>Margem em volta da imagem.</summary>
public enum ImageMargin
{
    None,
    /// <summary>1 cm.</summary>
    Small,
    /// <summary>2 cm.</summary>
    Large,
}

/// <summary>
/// Etapa 7.5 (imagem→PDF): cada imagem (PNG ou JPG) vira uma página, na ordem da lista. A imagem é ampliada
/// ou reduzida para caber na página (sem deformar) e fica centralizada. Em A4/Carta a página fica deitada
/// quando a imagem é mais larga que alta. Fotos de celular "deitadas" (marcação EXIF) ficam de pé.
/// O JPG entra no PDF sem ser recomprimido (não perde qualidade).
/// </summary>
public static class ImagesToPdf
{
    /// <summary>Formatos aceitos.</summary>
    public static string[] Extensions => PdfPageContent.ImageExtensions;

    private const double Cm = 72 / 2.54;

    /// <summary>Cria o PDF <paramref name="output"/>. <paramref name="progress"/> recebe quantas imagens já entraram.</summary>
    public static void Create(IReadOnlyList<string> images, ImagePageSize size, ImageMargin margin, string output, IProgress<int>? progress = null)
    {
        if (images.Count == 0) throw new ArgumentException("Escolha ao menos uma imagem.", nameof(images));
        double m = margin switch { ImageMargin.Small => Cm, ImageMargin.Large => 2 * Cm, _ => 0 };

        using var doc = new PdfDocument();
        doc.Info.Creator = "PDF Studio";
        for (int i = 0; i < images.Count; i++)
        {
            byte[] data = File.ReadAllBytes(images[i]);
            int turn = ExifRotation(data);
            using var image = PdfPageContent.OpenImage(images[i]);
            // Tamanho como a imagem deve aparecer (de pé): virada de lado troca largura e altura.
            bool sideways = turn is 90 or 270;
            double imageW = sideways ? image.PointHeight : image.PointWidth;
            double imageH = sideways ? image.PointWidth : image.PointHeight;

            var (pageW, pageH) = PageSize(size, imageW, imageH, m);
            var page = doc.AddPage();
            page.Width = XUnit.FromPoint(pageW);
            page.Height = XUnit.FromPoint(pageH);

            double scale = Math.Min((pageW - 2 * m) / imageW, (pageH - 2 * m) / imageH);
            double w = imageW * scale, h = imageH * scale;
            using var gfx = XGraphics.FromPdfPage(page);
            gfx.TranslateTransform(pageW / 2, pageH / 2); // centro da página
            gfx.RotateTransform(turn);                    // gira a foto para ficar de pé
            // Depois de girar, a imagem é desenhada com as medidas originais (largura e altura trocadas se de lado).
            double drawW = sideways ? h : w, drawH = sideways ? w : h;
            gfx.DrawImage(image, -drawW / 2, -drawH / 2, drawW, drawH);
            progress?.Report(i + 1);
        }
        doc.Save(output);
    }

    /// <summary>Tamanho da página (pontos) para uma imagem de <paramref name="imageW"/> × <paramref name="imageH"/> pontos.</summary>
    public static (double Width, double Height) PageSize(ImagePageSize size, double imageW, double imageH, double margin)
    {
        (double w, double h) = size switch
        {
            ImagePageSize.A4 => (595.28, 841.89),
            ImagePageSize.Letter => (612.0, 792.0),
            _ => (imageW + 2 * margin, imageH + 2 * margin),
        };
        // Papel padrão: deitado para imagens mais largas que altas.
        if (size != ImagePageSize.SameAsImage && imageW > imageH) (w, h) = (h, w);
        return (w, h);
    }

    /// <summary>
    /// Quanto a foto precisa girar (em graus, sentido horário) para ficar de pé, pela marcação EXIF "Orientation"
    /// que celulares e câmeras gravam no JPG. 0 quando não há marcação (ou não é JPG). Espelhamentos são ignorados.
    /// </summary>
    public static int ExifRotation(byte[] data)
    {
        try
        {
            if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return 0; // não é JPG
            int pos = 2;
            while (pos + 4 <= data.Length && data[pos] == 0xFF)
            {
                byte marker = data[pos + 1];
                if (marker is 0xDA or 0xD9) break; // começou a imagem em si: não há mais cabeçalhos
                int length = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos + 2));
                if (marker == 0xE1 && length >= 8 && data.AsSpan(pos + 4, 6).SequenceEqual("Exif\0\0"u8))
                    return RotationOf(Orientation(data.AsSpan(pos + 10, length - 8)));
                pos += 2 + length;
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            // Cabeçalho estragado: trata como foto sem marcação.
        }
        return 0;
    }

    private static int RotationOf(int orientation) => orientation switch
    {
        3 or 4 => 180,
        6 or 5 => 90,
        8 or 7 => 270,
        _ => 0,
    };

    /// <summary>Lê a marcação 0x0112 (Orientation) do primeiro diretório do bloco TIFF do EXIF.</summary>
    private static int Orientation(ReadOnlySpan<byte> tiff)
    {
        bool little = tiff[0] == (byte)'I';
        ushort U16(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt16LittleEndian(s) : BinaryPrimitives.ReadUInt16BigEndian(s);
        uint U32(ReadOnlySpan<byte> s) => little ? BinaryPrimitives.ReadUInt32LittleEndian(s) : BinaryPrimitives.ReadUInt32BigEndian(s);
        int ifd = (int)U32(tiff[4..]);
        int count = U16(tiff[ifd..]);
        for (int i = 0; i < count; i++)
        {
            var entry = tiff.Slice(ifd + 2 + i * 12, 12);
            if (U16(entry) == 0x0112) return U16(entry[8..]);
        }
        return 1;
    }
}
