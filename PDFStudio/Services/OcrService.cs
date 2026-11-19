using PDFStudio.Core;
using Windows.Data.Pdf;
using Windows.Globalization;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;
using OcrWord = PDFStudio.Core.OcrWord;

namespace PDFStudio.Services;

/// <summary>
/// Etapa 7.7: reconhece o texto de uma página com o OCR que já vem no Windows (Windows.Media.Ocr).
/// Tudo acontece no computador: nada é enviado para a internet.
/// </summary>
public static class OcrService
{
    /// <summary>Idiomas de OCR instalados no Windows (ex.: Português (Brasil), Inglês).</summary>
    public static IReadOnlyList<Language> Languages => OcrEngine.AvailableRecognizerLanguages;

    /// <summary>O idioma sugerido: português do Brasil, se instalado; senão o primeiro da lista.</summary>
    public static Language? DefaultLanguage =>
        Languages.FirstOrDefault(l => l.LanguageTag.Equals("pt-BR", StringComparison.OrdinalIgnoreCase))
        ?? Languages.FirstOrDefault(l => l.LanguageTag.StartsWith("pt", StringComparison.OrdinalIgnoreCase))
        ?? Languages.FirstOrDefault();

    /// <summary>
    /// Palavras da página <paramref name="index"/>, com a posição em pontos na página como aparece na tela.
    /// A página é desenhada a cerca de 300 dpi (boa leitura sem passar do limite de tamanho do OCR).
    /// </summary>
    public static async Task<List<OcrWord>> RecognizeAsync(PdfDocument doc, int index, OcrEngine engine)
    {
        using var page = doc.GetPage((uint)index);
        double widthDips = page.Size.Width, heightDips = page.Size.Height;
        double scale = Math.Min(300 / 96.0, Math.Min(OcrEngine.MaxImageDimension, 4200) / Math.Max(widthDips, heightDips));
        uint pixelsW = (uint)Math.Max(1, Math.Round(widthDips * scale)), pixelsH = (uint)Math.Max(1, Math.Round(heightDips * scale));

        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, new PdfPageRenderOptions { DestinationWidth = pixelsW, DestinationHeight = pixelsH });
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        var result = await engine.RecognizeAsync(bitmap);

        // Pixels → pontos (1 DIP = 0,75 ponto). Usa o tamanho que a imagem saiu de verdade, não o pedido: com a
        // escala de tela do Windows (ex.: 125%) o desenho da página sai maior que o pedido.
        double kx = widthDips * 0.75 / bitmap.PixelWidth, ky = heightDips * 0.75 / bitmap.PixelHeight;
        var words = new List<OcrWord>();
        foreach (var line in result.Lines)
            foreach (var word in line.Words)
            {
                var r = word.BoundingRect;
                words.Add(new OcrWord(word.Text, r.X * kx, r.Y * ky, r.Width * kx, r.Height * ky));
            }
        return words;
    }
}
