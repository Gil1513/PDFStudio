using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 3.4: recortar margens (ou desfazer recortes), com prévia da página atual.</summary>
public sealed partial class CropPagesDialog : ContentDialog
{
    private const double PreviewMaxSide = 240;
    private readonly int _pageCount;
    private readonly double _pageWidthPt, _pageHeightPt, _previewWidth, _previewHeight;

    /// <param name="preview">Imagem da página atual (pode ser nula).</param>
    /// <param name="pageWidthPt">Largura da página atual na tela, em pontos.</param>
    public CropPagesDialog(int pageCount, int currentPage, bool isSigned,
        ImageSource? preview, double pageWidthPt, double pageHeightPt)
    {
        InitializeComponent();
        _pageCount = pageCount;
        _pageWidthPt = pageWidthPt;
        _pageHeightPt = pageHeightPt;
        SignedWarning.IsOpen = isSigned;
        RangesBox.Text = currentPage.ToString();

        double scale = PreviewMaxSide / Math.Max(pageWidthPt, pageHeightPt);
        _previewWidth = pageWidthPt * scale;
        _previewHeight = pageHeightPt * scale;
        PreviewHost.Width = _previewWidth;
        PreviewHost.Height = _previewHeight;
        PreviewImage.Source = preview;
        PreviewTitle.Text = $"Prévia da página {currentPage}";
        UpdatePreview();
    }

    // ---------------- Resultado ----------------

    public bool RemoveCrop => ModeChoice.SelectedIndex == 1;

    /// <summary>null = todas as páginas.</summary>
    public string? Ranges => PagesChoice.SelectedIndex == 0 ? null : RangesBox.Text.Trim();

    public PageMargins Margins => PageMargins.FromCentimeters(Cm(TopBox), Cm(RightBox), Cm(BottomBox), Cm(LeftBox));

    public bool ReplaceOriginal => TargetChoice.SelectedIndex == 1;

    private static double Cm(NumberBox box) => double.IsNaN(box.Value) ? 0 : Math.Max(0, box.Value);

    // ---------------- Interação ----------------

    // Os eventos abaixo podem disparar durante o InitializeComponent, antes de todos os controles existirem.
    private void ModeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (MarginsPanel is null) return;
        MarginsPanel.Visibility = RemoveCrop ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButtonText = RemoveCrop ? "Desfazer recortes" : "Recortar";
        UpdatePreview();
    }

    private void PagesChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangesBox is not null) RangesBox.IsEnabled = PagesChoice.SelectedIndex == 1;
    }

    private void Margin_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();

    /// <summary>Escurece na prévia as faixas que serão cortadas.</summary>
    private void UpdatePreview()
    {
        if (PreviewOverlay is null || _pageWidthPt <= 0 || _pageHeightPt <= 0) return;
        var m = RemoveCrop ? default : Margins;
        double sx = _previewWidth / _pageWidthPt, sy = _previewHeight / _pageHeightPt;
        double top = Math.Min(m.Top * sy, _previewHeight), bottom = Math.Min(m.Bottom * sy, _previewHeight - top);
        double left = Math.Min(m.Left * sx, _previewWidth), right = Math.Min(m.Right * sx, _previewWidth - left);

        Place(CutTop, 0, 0, _previewWidth, top);
        Place(CutBottom, 0, _previewHeight - bottom, _previewWidth, bottom);
        Place(CutLeft, 0, top, left, _previewHeight - top - bottom);
        Place(CutRight, _previewWidth - right, top, right, _previewHeight - top - bottom);
        Place(KeptArea, left, top, _previewWidth - left - right, _previewHeight - top - bottom);
    }

    private static void Place(FrameworkElement e, double x, double y, double w, double h)
    {
        Canvas.SetLeft(e, x);
        Canvas.SetTop(e, y);
        e.Width = Math.Max(0, w);
        e.Height = Math.Max(0, h);
    }

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = null;
        if (Ranges is { } ranges)
        {
            try { PageRanges.Parse(ranges, _pageCount); }
            catch (FormatException ex) { error = ex.Message; }
        }
        if (error is null && !RemoveCrop)
        {
            var m = Margins;
            if (m.Top + m.Right + m.Bottom + m.Left <= 0)
                error = "Informe ao menos uma margem para cortar.";
            else if (_pageWidthPt - m.Left - m.Right < PdfOperations.MinCroppedSide || _pageHeightPt - m.Top - m.Bottom < PdfOperations.MinCroppedSide)
                error = "As margens são grandes demais: sobraria quase nada da página.";
        }

        if (error is null) return;
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        args.Cancel = true;
    }
}
