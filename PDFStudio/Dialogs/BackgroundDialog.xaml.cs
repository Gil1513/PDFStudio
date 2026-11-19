using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 5.7: colocar ou remover o fundo das páginas (cor ou imagem), com prévia da página atual.</summary>
public sealed partial class BackgroundDialog : ContentDialog
{
    private const double PreviewMaxSide = 260;
    private readonly int _pageCount;
    private readonly double _pageWidthPt, _pageHeightPt, _previewWidth, _previewHeight;
    private readonly bool _ready;
    private readonly string _existingNote;
    private string? _imagePath;
    private double _imageWidthPt, _imageHeightPt;

    /// <param name="preview">Página atual desenhada sem o papel branco (o fundo aparece por trás).</param>
    public BackgroundDialog(int pageCount, int currentPage, ImageSource? preview, double pageWidthPt, double pageHeightPt, int existing)
    {
        InitializeComponent();
        _pageCount = pageCount;
        _pageWidthPt = pageWidthPt;
        _pageHeightPt = pageHeightPt;
        RangesBox.Text = currentPage.ToString();
        _existingNote = (existing == 0 ? "" : $"{existing} página(s) já têm fundo do PDF Studio; o novo substitui o anterior. ")
            + "Se o PDF pintar a própria página de branco, o fundo fica escondido atrás dela (a prévia mostra isso).";
        NoteText.Text = _existingNote;

        double scale = PreviewMaxSide / Math.Max(pageWidthPt, pageHeightPt);
        _previewWidth = pageWidthPt * scale;
        _previewHeight = pageHeightPt * scale;
        PreviewHost.Width = _previewWidth;
        PreviewHost.Height = _previewHeight;
        PreviewImageLayer.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, _previewWidth, _previewHeight) };
        PreviewVeil.Width = _previewWidth;
        PreviewVeil.Height = _previewHeight;
        PreviewImage.Source = preview;
        PreviewTitle.Text = $"Prévia da página {currentPage}";
        _ready = true;
        UpdatePreview();
    }

    // ---------------- Resultado ----------------

    public bool Remove => ModeChoice.SelectedIndex == 1;

    /// <summary>null = todas as páginas.</summary>
    public string? Ranges => PagesChoice.SelectedIndex == 0 ? null : RangesBox.Text.Trim();

    private bool UsesImage => KindChoice.SelectedIndex == 1;

    public BackgroundOptions Options => new(
        AnnotationColor.FromHex((ColorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "#FFF6D5"),
        UsesImage ? _imagePath : null,
        IntensitySlider.Value / 100,
        FitChoice.SelectedIndex switch { 1 => BackgroundFit.Fit, 2 => BackgroundFit.Stretch, _ => BackgroundFit.Cover });

    // ---------------- Interação ----------------

    private void ModeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        OptionsPanel.Visibility = Remove ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButtonText = Remove ? "Remover" : "Aplicar";
        NoteText.Text = Remove ? "Só é removido o fundo colocado pelo PDF Studio." : _existingNote;
        UpdatePreview();
    }

    private void KindChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        ColorBox.Visibility = UsesImage ? Visibility.Collapsed : Visibility.Visible;
        ImagePanel.Visibility = UsesImage ? Visibility.Visible : Visibility.Collapsed;
        UpdatePreview();
    }

    private void PagesChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangesBox is not null) RangesBox.IsEnabled = PagesChoice.SelectedIndex == 1;
    }

    private async void ChooseImage_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickFileAsync(PdfPageContent.ImageExtensions);
        if (path is null) return;
        try
        {
            var (w, h) = await Task.Run(() => PdfPageContent.ImageSize(path));
            if (w <= 0 || h <= 0) throw new InvalidOperationException("A imagem está vazia.");
            (_imagePath, _imageWidthPt, _imageHeightPt) = (path, w, h);
            ImageNameText.Text = Path.GetFileName(path);
            ToolTipService.SetToolTip(ImageNameText, path);
            PreviewBackgroundImage.Source = new BitmapImage(new Uri(path));
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ErrorText.Text = $"Não foi possível usar esta imagem: {ex.Message}";
            ErrorText.Visibility = Visibility.Visible;
        }
        UpdatePreview();
    }

    private void Option_Changed(object sender, object e) => UpdatePreview();
    private void Intensity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdatePreview();

    private void UpdatePreview()
    {
        if (!_ready) return;
        var o = Options;
        bool show = !Remove;
        bool image = show && UsesImage && _imagePath is not null;
        PreviewColor.Visibility = show && !UsesImage ? Visibility.Visible : Visibility.Collapsed;
        PreviewImageLayer.Visibility = image ? Visibility.Visible : Visibility.Collapsed;
        PreviewColor.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(o.Opacity * 255), o.Color.R, o.Color.G, o.Color.B));
        if (image)
        {
            // Mesma conta do núcleo, em pontos da página, convertida para a escala da prévia.
            var r = PdfBackground.Place(_imageWidthPt, _imageHeightPt, _pageWidthPt, _pageHeightPt, o.Fit);
            double s = _previewWidth / _pageWidthPt;
            Canvas.SetLeft(PreviewBackgroundImage, r.X * s);
            Canvas.SetTop(PreviewBackgroundImage, r.Y * s);
            PreviewBackgroundImage.Width = r.Width * s;
            PreviewBackgroundImage.Height = r.Height * s;
            PreviewVeil.Opacity = 1 - o.Opacity;
        }
    }

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = null;
        if (Ranges is { } ranges)
        {
            try { PageRanges.Parse(ranges, _pageCount); }
            catch (FormatException ex) { error = ex.Message; }
        }
        if (error is null && !Remove && UsesImage && _imagePath is null) error = "Escolha a imagem do fundo.";
        if (error is null) return;
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        args.Cancel = true;
    }
}
