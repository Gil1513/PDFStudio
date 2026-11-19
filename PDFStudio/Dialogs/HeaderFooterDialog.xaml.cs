using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 5.6: colocar ou remover número de página, cabeçalho e rodapé, com prévia da página atual.</summary>
public sealed partial class HeaderFooterDialog : ContentDialog
{
    private const double PreviewMaxSide = 280;
    private const double PointsPerCm = 72 / 2.54;
    private readonly int _pageCount, _currentPage;
    private readonly double _pageWidthPt, _pageHeightPt, _previewWidth, _previewHeight;
    private readonly bool _ready;
    private readonly string _existingNote;
    private TextBox _lastBox;

    /// <param name="pageNumberOnly">true = aberto pelo botão "Número de página" (já vem com o rodapé numerado).</param>
    public HeaderFooterDialog(int pageCount, int currentPage, ImageSource? preview, double pageWidthPt, double pageHeightPt,
        IEnumerable<string> fonts, string font, int existing, bool pageNumberOnly)
    {
        InitializeComponent();
        _pageCount = pageCount;
        _currentPage = currentPage;
        _pageWidthPt = pageWidthPt;
        _pageHeightPt = pageHeightPt;
        _lastBox = FooterCenterBox;
        foreach (var name in fonts) FontBox.Items.Add(name);
        if (FontBox.Items.Count == 0) FontBox.Items.Add("Arial");
        FontBox.SelectedItem = FontBox.Items.Contains(font) ? font : FontBox.Items.Contains("Arial") ? "Arial" : FontBox.Items[0];
        RangesBox.Text = $"1-{pageCount}";
        if (pageNumberOnly)
        {
            Title = "Número de página";
            FooterCenterBox.Text = "{n}";
        }
        else FooterRightBox.Text = "Página {n} de {total}";
        foreach (var box in new[] { HeaderLeftBox, HeaderCenterBox, HeaderRightBox, FooterLeftBox, FooterCenterBox, FooterRightBox })
            box.GotFocus += (s, _) => _lastBox = (TextBox)s;

        _existingNote = existing == 0
            ? "Use os botões para inserir o número da página, o total ou a data no último campo em que você clicou."
            : $"{existing} página(s) já têm cabeçalho/rodapé do PDF Studio; o novo substitui o anterior nas páginas escolhidas.";
        NoteText.Text = _existingNote;

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

    public HeaderFooterOptions Options => new(
        HeaderLeftBox.Text, HeaderCenterBox.Text, HeaderRightBox.Text,
        FooterLeftBox.Text, FooterCenterBox.Text, FooterRightBox.Text,
        FontBox.SelectedItem as string ?? "Arial",
        double.IsNaN(SizeBox.Value) ? 10 : SizeBox.Value,
        AnnotationColor.FromHex((ColorBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "#000000"),
        (double.IsNaN(MarginBox.Value) ? 1 : MarginBox.Value) * PointsPerCm,
        double.IsNaN(StartBox.Value) ? 1 : (int)StartBox.Value);

    // ---------------- Interação ----------------

    private void ModeChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        OptionsPanel.Visibility = PreviewOverlay.Visibility = Remove ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButtonText = Remove ? "Remover" : "Aplicar";
        NoteText.Text = Remove
            ? "Só são removidos cabeçalhos, rodapés e números colocados pelo PDF Studio (a marca d'água fica)."
            : _existingNote;
    }

    private void PagesChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangesBox is not null) RangesBox.IsEnabled = PagesChoice.SelectedIndex == 1;
    }

    private void Insert_Click(object sender, RoutedEventArgs e)
    {
        string code = (string)((Button)sender).Tag;
        var box = _lastBox;
        int at = Math.Clamp(box.SelectionStart, 0, box.Text.Length);
        box.Text = box.Text.Insert(at, code);
        box.Focus(FocusState.Programmatic);
        box.SelectionStart = at + code.Length;
    }

    private void Option_Changed(object sender, object e) => UpdatePreview();
    private void Number_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args) => UpdatePreview();

    /// <summary>Desenha os seis textos (já com número e data) na prévia, na mesma escala da página.</summary>
    private void UpdatePreview()
    {
        if (!_ready || _pageWidthPt <= 0) return;
        var o = Options;
        double scale = _previewWidth / _pageWidthPt;
        double margin = o.MarginPt * scale, fontSize = Math.Max(1, o.FontSize * scale);
        var brush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, o.Color.R, o.Color.G, o.Color.B));
        var family = new FontFamily(o.FontFamily);
        int total = _pageCount + o.StartNumber - 1;
        string date = DateTime.Today.ToString("dd/MM/yyyy");

        void Show(TextBlock block, string text, int column, bool header)
        {
            block.Text = PdfHeaderFooter.Expand(text, _currentPage - 1 + o.StartNumber, total, date);
            block.FontFamily = family;
            block.FontSize = fontSize;
            block.Foreground = brush;
            block.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            double w = block.DesiredSize.Width, h = block.DesiredSize.Height;
            double x = column switch { 0 => margin, 1 => (_previewWidth - w) / 2, _ => _previewWidth - margin - w };
            Canvas.SetLeft(block, x);
            Canvas.SetTop(block, header ? margin : _previewHeight - margin - h);
        }
        Show(PreviewHL, o.HeaderLeft, 0, true);
        Show(PreviewHC, o.HeaderCenter, 1, true);
        Show(PreviewHR, o.HeaderRight, 2, true);
        Show(PreviewFL, o.FooterLeft, 0, false);
        Show(PreviewFC, o.FooterCenter, 1, false);
        Show(PreviewFR, o.FooterRight, 2, false);
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
            var o = Options;
            if (!o.HasHeader && !o.HasFooter) error = "Escreva ao menos um texto de cabeçalho ou rodapé.";
            else if (!PdfPageContent.CanUseFont(o.FontFamily)) error = $"A fonte \"{o.FontFamily}\" não pode ser usada no PDF. Escolha outra.";
        }
        if (error is null) return;
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        args.Cancel = true;
    }
}
