using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 3.5: mudar o tamanho das páginas (A4, Carta, personalizado...).</summary>
public sealed partial class ResizePagesDialog : ContentDialog
{
    private const string Custom = "Personalizado…";
    private readonly int _pageCount;

    /// <param name="currentWidthPt">Tamanho da página atual na tela, em pontos (só para informar).</param>
    public ResizePagesDialog(int pageCount, int currentPage, bool isSigned, double currentWidthPt, double currentHeightPt)
    {
        InitializeComponent();
        _pageCount = pageCount;
        SignedWarning.IsOpen = isSigned;
        RangesBox.Text = currentPage.ToString();
        CurrentSizeText.Text = $"Página {currentPage} hoje: {currentWidthPt / PageMargins.PointsPerCm:0.0} x {currentHeightPt / PageMargins.PointsPerCm:0.0} cm";

        foreach (var (name, _) in PdfOperations.BlankPageSizes) SizeBox.Items.Add(name);
        SizeBox.Items.Add(Custom);
        SizeBox.SelectedIndex = 0;
    }

    // ---------------- Resultado ----------------

    private bool IsCustom => SizeBox.SelectedIndex == PdfOperations.BlankPageSizes.Count;

    /// <summary>Largura e altura escolhidas, em pontos.</summary>
    public (double Width, double Height) SizePt
    {
        get
        {
            if (IsCustom) return (WidthBox.Value * PageMargins.PointsPerCm, HeightBox.Value * PageMargins.PointsPerCm);
            var s = PdfSharp.PageSizeConverter.ToSize(PdfOperations.BlankPageSizes[Math.Max(0, SizeBox.SelectedIndex)].Size);
            return (s.Width, s.Height);
        }
    }

    public PageOrientationChoice Orientation => OrientationBox.SelectedIndex switch
    {
        1 => PageOrientationChoice.Portrait,
        2 => PageOrientationChoice.Landscape,
        _ => PageOrientationChoice.Auto,
    };

    public bool ScaleContent => ContentChoice.SelectedIndex == 0;

    /// <summary>null = todas as páginas.</summary>
    public string? Ranges => PagesChoice.SelectedIndex == 0 ? null : RangesBox.Text.Trim();

    public bool ReplaceOriginal => TargetChoice.SelectedIndex == 1;

    // ---------------- Interação ----------------

    private void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (CustomPanel is not null) CustomPanel.Visibility = IsCustom ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PagesChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangesBox is not null) RangesBox.IsEnabled = PagesChoice.SelectedIndex == 1;
    }

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = null;
        if (IsCustom && (double.IsNaN(WidthBox.Value) || double.IsNaN(HeightBox.Value)))
            error = "Informe a largura e a altura.";
        else if (Ranges is { } ranges)
        {
            try { PageRanges.Parse(ranges, _pageCount); }
            catch (FormatException ex) { error = ex.Message; }
        }

        if (error is null) return;
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        args.Cancel = true;
    }
}
