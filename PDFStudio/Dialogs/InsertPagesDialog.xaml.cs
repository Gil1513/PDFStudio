using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 3.3: inserir páginas em branco ou de outro PDF.</summary>
public sealed partial class InsertPagesDialog : ContentDialog
{
    private const string SameAsNeighbor = "Igual à página vizinha";
    private readonly int _pageCount;
    private int _otherPageCount;

    public InsertPagesDialog(int pageCount, int currentPage, bool isSigned)
    {
        InitializeComponent();
        _pageCount = pageCount;
        SignedWarning.IsOpen = isSigned;

        SizeBox.Items.Add(SameAsNeighbor);
        foreach (var (name, _) in PdfOperations.BlankPageSizes) SizeBox.Items.Add(name);
        SizeBox.SelectedIndex = 0;

        PageBox.Maximum = pageCount;
        PageBox.Value = currentPage;
        PageHint.Text = $"O documento tem {pageCount} páginas. Para colocar no início, escolha \"Antes da página 1\".";
    }

    // ---------------- Resultado ----------------

    public bool FromPdf => SourceChoice.SelectedIndex == 1;
    public int Count => (int)CountBox.Value;

    /// <summary>null = copiar o tamanho da página vizinha.</summary>
    public PdfSharp.PageSize? Size => SizeBox.SelectedIndex <= 0 ? null : PdfOperations.BlankPageSizes[SizeBox.SelectedIndex - 1].Size;
    public bool Landscape => OrientationBox.SelectedIndex == 1;

    public string? OtherPath { get; private set; }
    public string? OtherRanges => string.IsNullOrWhiteSpace(OtherRangesBox.Text) ? null : OtherRangesBox.Text.Trim();

    /// <summary>Quantas páginas ficam antes das inseridas (0 = início; total = fim).</summary>
    public int Position => WhereBox.SelectedIndex == 0 ? (int)PageBox.Value - 1 : (int)PageBox.Value;

    public bool ReplaceOriginal => TargetChoice.SelectedIndex == 1;

    // ---------------- Interação ----------------

    private void SourceChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BlankPanel is null || PdfPanel is null) return; // pode disparar antes de a janela terminar de montar
        BlankPanel.Visibility = FromPdf ? Visibility.Collapsed : Visibility.Visible;
        PdfPanel.Visibility = FromPdf ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (OrientationBox is not null) OrientationBox.IsEnabled = SizeBox.SelectedIndex > 0;
    }

    private async void PickPdf_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is null) return;
        try
        {
            _otherPageCount = await Task.Run(() => PdfOperations.GetPageCount(path));
            OtherPath = path;
            OtherFileText.Text = $"{Path.GetFileName(path)} ({_otherPageCount} páginas)";
            ToolTipService.SetToolTip(OtherFileText, path);
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = null;
        if (double.IsNaN(PageBox.Value) || PageBox.Value < 1 || PageBox.Value > _pageCount)
            error = $"Escolha uma página de 1 a {_pageCount}.";
        else if (FromPdf && OtherPath is null)
            error = "Escolha o PDF de onde virão as páginas.";
        else if (FromPdf && OtherRanges is not null)
        {
            try { PageRanges.Parse(OtherRanges, _otherPageCount); }
            catch (FormatException ex) { error = ex.Message; }
        }
        else if (!FromPdf && (double.IsNaN(CountBox.Value) || CountBox.Value < 1))
            error = "Informe quantas páginas em branco inserir.";

        if (error is null) return;
        ShowError(error);
        args.Cancel = true;
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
    }
}
