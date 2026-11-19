using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 5.5: colocar ou remover marca d'água de texto, com prévia da página atual.</summary>
public sealed partial class WatermarkDialog : ContentDialog
{
    private const double PreviewMaxSide = 260;
    private readonly int _pageCount;
    private readonly double _pageWidthPt, _pageHeightPt, _previewWidth, _previewHeight;
    private readonly bool _ready;

    /// <param name="fonts">Fontes instaladas (a escolhida é conferida antes de aplicar).</param>
    /// <param name="existing">Quantas páginas já têm marca d'água do PDF Studio.</param>
    public WatermarkDialog(int pageCount, int currentPage, ImageSource? preview, double pageWidthPt, double pageHeightPt,
        IEnumerable<string> fonts, string font, int existing)
    {
        InitializeComponent();
        _pageCount = pageCount;
        _pageWidthPt = pageWidthPt;
        _pageHeightPt = pageHeightPt;
        foreach (var name in fonts) FontBox.Items.Add(name);
        if (FontBox.Items.Count == 0) FontBox.Items.Add("Arial");
        FontBox.SelectedItem = FontBox.Items.Contains(font) ? font : FontBox.Items.Contains("Arial") ? "Arial" : FontBox.Items[0];
        RangesBox.Text = currentPage.ToString();
        SizeBox.Value = double.NaN;
        RemoveNote.Text = existing == 0
            ? "Este documento ainda não tem marca d'água colocada pelo PDF Studio."
            : $"{existing} página(s) já têm marca d'água do PDF Studio; ao colocar outra, ela substitui a anterior nas páginas escolhidas.";

        double scale = PreviewMaxSide / Math.Max(pageWidthPt, pageHeightPt);
        _previewWidth = pageWidthPt * scale;
        _previewHeight = pageHeightPt * scale;
        PreviewHost.Width = _previewWidth;
        PreviewHost.Height = _previewHeight;
        PreviewOverlay.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, _previewWidth, _previewHeight) };
        PreviewImage.Source = preview;
        PreviewTitle.Text = $"Prévia da página {currentPage}";
        _ready = true;
        UpdatePreview();
    }

    // ---------------- Resultado ----------------

    public bool Remove => ModeChoice.SelectedIndex == 1;

    /// <summary>null = todas as páginas.</summary>
    public string? Ranges => PagesChoice.SelectedIndex == 0 ? null : RangesBox.Text.Trim();

    public string FontFamily => FontBox.SelectedItem as string ?? "Arial";

    public WatermarkOptions Options => new(
        TextInput.Text.Trim(),
        FontFamily,
        AnnotationColor.FromHex((ColorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "#808080"),
        Opacity: Math.Clamp(1 - OpacitySlider.Value / 100, 0.1, 1),
        Angle: AngleChoice.SelectedIndex == 1 ? 0 : null,
        FontSize: double.IsNaN(SizeBox.Value) ? 0 : SizeBox.Value,
        Bold: BoldToggle.IsChecked == true,
        Layer: LayerChoice.SelectedIndex == 1 ? WatermarkLayer.Behind : WatermarkLayer.Front);

    // ---------------- Interação ----------------

    private void ModeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        OptionsPanel.Visibility = PreviewOverlay.Visibility = Remove ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButtonText = Remove ? "Remover" : "Aplicar";
        RemoveNote.Text = Remove
            ? "Só são removidas as marcas d'água colocadas pelo PDF Studio; as de outros programas fazem parte do conteúdo da página."
            : RemoveNote.Text;
    }

    private void PagesChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangesBox is not null) RangesBox.IsEnabled = PagesChoice.SelectedIndex == 1;
    }

    private void Option_Changed(object sender, object e) => UpdatePreview();
    private void Size_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();
    private void Opacity_ValueChanged(object sender, RangeBaseValueChangedEventArgs e) => UpdatePreview();

    /// <summary>Mostra o texto girado e transparente sobre a imagem da página, na mesma escala.</summary>
    private void UpdatePreview()
    {
        if (!_ready || _pageWidthPt <= 0) return;
        var options = Options;
        if (options.Text.Length == 0)
        {
            PreviewText.Text = "";
            return;
        }
        double sizePt;
        try { sizePt = options.FontSize > 0 ? options.FontSize : PdfWatermark.AutoFontSize(_pageWidthPt, _pageHeightPt, options); }
        catch { return; } // fonte que não pode ser usada: o aviso aparece ao aplicar
        double scale = _previewWidth / _pageWidthPt;
        var c = options.Color;
        PreviewText.Text = options.Text;
        PreviewText.FontFamily = new FontFamily(options.FontFamily);
        PreviewText.FontWeight = options.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
        PreviewText.FontSize = Math.Max(1, sizePt * scale);
        PreviewText.Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)Math.Round(options.Opacity * 255), c.R, c.G, c.B));
        PreviewText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        double w = PreviewText.DesiredSize.Width, h = PreviewText.DesiredSize.Height;
        double angle = options.Angle ?? Math.Atan2(_pageHeightPt, _pageWidthPt) * 180 / Math.PI;
        Canvas.SetLeft(PreviewText, (_previewWidth - w) / 2);
        Canvas.SetTop(PreviewText, (_previewHeight - h) / 2);
        PreviewText.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
        PreviewText.RenderTransform = new RotateTransform { Angle = -angle };
    }

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = null;
        if (Ranges is { } ranges)
        {
            try { PageRanges.Parse(ranges, _pageCount); }
            catch (FormatException ex) { error = ex.Message; }
        }
        if (error is null && !Remove)
        {
            if (TextInput.Text.Trim().Length == 0) error = "Digite o texto da marca d'água.";
            else if (!PdfPageContent.CanUseFont(FontFamily)) error = $"A fonte \"{FontFamily}\" não pode ser usada no PDF. Escolha outra.";
        }
        if (error is null) return;
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        args.Cancel = true;
    }
}
