using PDFStudio.Core;
using System.ComponentModel;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace PDFStudio.Services;

/// <summary>
/// Representa uma página do documento. O tamanho é conhecido logo ao abrir; a imagem
/// só é gerada quando a página aparece na tela (e é descartada quando sai), economizando memória.
/// </summary>
public sealed partial class PageSlot : INotifyPropertyChanged
{
    private BitmapImage? _image;

    public required int Index { get; init; }
    /// <summary>Tamanho da página em DIPs (1/96 pol.) na escala 100%.</summary>
    public required double Width { get; init; }
    public required double Height { get; init; }
    public int Number => Index + 1;
    public string Label => $"Página {Number}";
    internal bool IsRendering { get; set; }

    public BitmapImage? Image
    {
        get => _image;
        set { _image = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image))); }
    }

    // ---- Miniatura do painel lateral ----
    private BitmapImage? _thumbnail;
    public const double ThumbMaxWidth = 120;
    public double ThumbWidth => Width >= Height ? ThumbMaxWidth : ThumbMaxWidth * Width / Height;
    public double ThumbHeight => Width >= Height ? ThumbMaxWidth * Height / Width : ThumbMaxWidth;
    internal bool ThumbnailRequested { get; set; }

    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set { _thumbnail = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail))); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Renderiza páginas usando o motor de PDF nativo do Windows (Windows.Data.Pdf).
/// </summary>
public static class PdfRenderer
{
    /// <summary>
    /// Carrega o PDF a partir de uma cópia em memória, para não manter o arquivo travado
    /// (assim ele pode ser sobrescrito por juntar/organizar/assinar enquanto está aberto).
    /// </summary>
    public static async Task<PdfDocument> LoadAsync(string path, string? password = null)
    {
        byte[] bytes = await File.ReadAllBytesAsync(path);
        var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream.GetOutputStreamAt(0)))
        {
            writer.WriteBytes(bytes);
            await writer.StoreAsync();
            await writer.FlushAsync();
            writer.DetachStream();
        }
        stream.Seek(0);
        return string.IsNullOrEmpty(password)
            ? await PdfDocument.LoadFromStreamAsync(stream)
            : await PdfDocument.LoadFromStreamAsync(stream, password);
    }

    /// <summary>
    /// Etapa 7.4: desenha a página já no formato do arquivo (o motor do Windows codifica o PNG/JPG) e grava em
    /// <paramref name="path"/> (que não pode existir). Usado pelo visualizador e pelo lote.
    /// </summary>
    public static async Task SavePageImageAsync(PdfDocument doc, int index, int dpi, PageImageFormat format, string path)
    {
        using var page = doc.GetPage((uint)index);
        var (width, height) = PageImages.PixelSize(page.Size.Width, page.Size.Height, dpi);
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = width,
            DestinationHeight = height,
            BitmapEncoderId = format == PageImageFormat.Png ? Windows.Graphics.Imaging.BitmapEncoder.PngEncoderId : Windows.Graphics.Imaging.BitmapEncoder.JpegEncoderId,
        };
        using var stream = new InMemoryRandomAccessStream();
        await page.RenderToStreamAsync(stream, options);
        stream.Seek(0);
        using var input = stream.AsStreamForRead();
        await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write);
        await input.CopyToAsync(output);
    }

    /// <summary>
    /// Etapa 8.4: quanto de "tinta" cada página tem (0 a 1), para achar páginas em branco. Cada página é desenhada
    /// pequena (~40 dpi, sobre papel branco) e os pixels vão para <see cref="BlankPages.InkRatio"/>.
    /// </summary>
    public static async Task<IReadOnlyList<double>> InkRatiosAsync(string path, CancellationToken cancel)
    {
        var doc = await LoadAsync(path);
        var ratios = new List<double>();
        for (uint i = 0; i < doc.PageCount; i++)
        {
            cancel.ThrowIfCancellationRequested();
            using var page = doc.GetPage(i);
            double scale = 40 / 96.0;
            var options = new PdfPageRenderOptions
            {
                DestinationWidth = (uint)Math.Max(8, Math.Round(page.Size.Width * scale)),
                DestinationHeight = (uint)Math.Max(8, Math.Round(page.Size.Height * scale)),
                BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
            };
            using var stream = new InMemoryRandomAccessStream();
            await page.RenderToStreamAsync(stream, options);
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform(),
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            ratios.Add(BlankPages.InkRatio(pixels.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight));
        }
        return ratios;
    }

    public static List<PageSlot> CreateSlots(PdfDocument doc)
    {
        var slots = new List<PageSlot>((int)doc.PageCount);
        for (uint i = 0; i < doc.PageCount; i++)
        {
            using var page = doc.GetPage(i);
            slots.Add(new PageSlot { Index = (int)i, Width = page.Size.Width, Height = page.Size.Height });
        }
        return slots;
    }

    /// <param name="scale">Resolução relativa ao tamanho real (2 = nítido com zoom).</param>
    /// <param name="transparent">true = sem o papel branco por trás (para mostrar um fundo embaixo da página).</param>
    public static async Task<BitmapImage> RenderAsync(PdfDocument doc, int index, double scale, bool transparent = false)
    {
        using var page = doc.GetPage((uint)index);
        using var stream = new InMemoryRandomAccessStream();
        var options = new PdfPageRenderOptions
        {
            DestinationWidth = (uint)Math.Max(1, Math.Round(page.Size.Width * scale)),
            DestinationHeight = (uint)Math.Max(1, Math.Round(page.Size.Height * scale)),
        };
        if (transparent) options.BackgroundColor = Windows.UI.Color.FromArgb(0, 255, 255, 255);
        await page.RenderToStreamAsync(stream, options);

        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }
}
