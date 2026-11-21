using System.Runtime.InteropServices.WindowsRuntime;
using PDFStudio.Core;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace PDFStudio.Services;

/// <summary>
/// Etapa 7.6: reduz uma imagem do PDF com o motor de imagens do Windows (Windows.Graphics.Imaging, já vem com o
/// sistema): diminui para caber em <c>maxSide</c> pixels e regrava em JPG. Imagem cinza continua cinza (1 cor por
/// pixel), porque o PDF diz quantas cores a imagem tem.
/// </summary>
public static class ImageShrinker
{
    /// <summary>Versão síncrona para o Core (que roda em segundo plano, fora da thread da tela).</summary>
    public static ShrunkImage? Shrink(PdfImageData image, int maxSide, int quality) =>
        ShrinkAsync(image, maxSide, quality).GetAwaiter().GetResult();

    private static async Task<ShrunkImage?> ShrinkAsync(PdfImageData image, int maxSide, int quality)
    {
        var format = image.Components == 1 ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Bgra8;
        using var source = image.IsJpeg ? await DecodeJpegAsync(image.Data, format) : FromPixels(image, format);
        int width = source.PixelWidth, height = source.PixelHeight;
        double scale = Math.Min(1.0, (double)maxSide / Math.Max(width, height));
        uint newWidth = (uint)Math.Max(1, Math.Round(width * scale)), newHeight = (uint)Math.Max(1, Math.Round(height * scale));

        using var output = new InMemoryRandomAccessStream();
        var options = new BitmapPropertySet { { "ImageQuality", new BitmapTypedValue(quality / 100f, PropertyType.Single) } };
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, output, options);
        encoder.SetSoftwareBitmap(source);
        encoder.BitmapTransform.ScaledWidth = newWidth;
        encoder.BitmapTransform.ScaledHeight = newHeight;
        encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant; // boa qualidade ao reduzir
        await encoder.FlushAsync();

        var bytes = new byte[output.Size];
        output.Seek(0);
        using (var reader = new DataReader(output.GetInputStreamAt(0)))
        {
            await reader.LoadAsync((uint)output.Size);
            reader.ReadBytes(bytes);
        }
        return new ShrunkImage(bytes, (int)newWidth, (int)newHeight);
    }

    /// <summary>
    /// JPG do PDF → pixels crus (cinza: 1 byte por pixel; cor: R, G, B), linha a linha de cima para baixo. Usado
    /// pela redação para apagar só a parte de uma foto que fica embaixo da tarja. null = não conseguiu.
    /// </summary>
    public static byte[]? DecodeJpegPixels(byte[] jpeg, int components)
    {
        try
        {
            return Task.Run(() => DecodeJpegPixelsAsync(jpeg, components)).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return null;
        }
    }

    private static async Task<byte[]?> DecodeJpegPixelsAsync(byte[] jpeg, int components)
    {
        if (components is not (1 or 3)) return null;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream);
        var format = components == 1 ? BitmapPixelFormat.Gray8 : BitmapPixelFormat.Bgra8;
        var data = await decoder.GetPixelDataAsync(format, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
        byte[] pixels = data.DetachPixelData();
        if (components == 1) return pixels;
        var rgb = new byte[pixels.Length / 4 * 3];
        for (int i = 0, o = 0; i + 3 < pixels.Length; i += 4, o += 3)
        {
            rgb[o] = pixels[i + 2];
            rgb[o + 1] = pixels[i + 1];
            rgb[o + 2] = pixels[i];
        }
        return rgb;
    }

    private static async Task<SoftwareBitmap> DecodeJpegAsync(byte[] jpeg, BitmapPixelFormat format)
    {
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(jpeg.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(BitmapDecoder.JpegDecoderId, stream);
        // No PDF a imagem é desenhada como está gravada: não aplica giro EXIF nem perfil de cor.
        return await decoder.GetSoftwareBitmapAsync(format, BitmapAlphaMode.Ignore, new BitmapTransform(),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
    }

    /// <summary>Pixels crus do PDF (cinza ou RGB, 8 bits) para o formato do Windows (cinza ou BGRA).</summary>
    private static SoftwareBitmap FromPixels(PdfImageData image, BitmapPixelFormat format)
    {
        int count = image.Width * image.Height;
        byte[] buffer;
        if (format == BitmapPixelFormat.Gray8)
        {
            buffer = image.Data;
        }
        else
        {
            buffer = new byte[count * 4];
            for (int i = 0, j = 0; i < count; i++, j += 3)
            {
                buffer[i * 4] = image.Data[j + 2];     // B
                buffer[i * 4 + 1] = image.Data[j + 1]; // G
                buffer[i * 4 + 2] = image.Data[j];     // R
                buffer[i * 4 + 3] = 255;
            }
        }
        return SoftwareBitmap.CreateCopyFromBuffer(buffer.AsBuffer(), format, image.Width, image.Height, BitmapAlphaMode.Ignore);
    }
}
