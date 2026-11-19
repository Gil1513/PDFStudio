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
