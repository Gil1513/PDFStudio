using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Copiar páginas: quais, quantas vezes e onde as cópias ficam.</summary>
public sealed partial class CopyPagesDialog : ContentDialog
{
    private readonly int _pageCount;

    public CopyPagesDialog(int pageCount, int currentPage, bool isSigned)
    {
        InitializeComponent();
        _pageCount = pageCount;
        SignedWarning.IsOpen = isSigned;
        RangesBox.Text = currentPage.ToString();
        RangesHint.Text = $"Use vírgulas e traços. O documento tem {pageCount} páginas.";
        CopiesBox.Maximum = PdfOperations.MaxPageCopies;
        PageBox.Maximum = pageCount;
        PageBox.Value = currentPage;
        PageHint.Text = "Para colocar no início, escolha \"Antes da página 1\"; no fim, " +
                        $"\"Depois da página {pageCount}\".";
        Opened += (_, _) => { RangesBox.Focus(FocusState.Programmatic); RangesBox.SelectAll(); };
    }

    public string Ranges => RangesBox.Text.Trim();
    public int Copies => (int)CopiesBox.Value;

    /// <summary>Quantas páginas ficam antes das cópias (0 = início; total = fim).</summary>
    public int Position => WhereBox.SelectedIndex == 0 ? (int)PageBox.Value - 1 : (int)PageBox.Value;

    public bool ReplaceOriginal => TargetChoice.SelectedIndex == 0;

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        string? error = null;
        if (double.IsNaN(PageBox.Value) || PageBox.Value < 1 || PageBox.Value > _pageCount)
            error = $"Escolha uma página de 1 a {_pageCount}.";
        else if (double.IsNaN(CopiesBox.Value) || CopiesBox.Value < 1)
            error = "Informe quantas cópias fazer.";
        else
        {
            try
            {
                int pages = PageRanges.Parse(Ranges, _pageCount).SelectMany(g => g).Count();
                if (pages * Copies > 2000) error = "Cópias demais de uma vez (máximo de 2000 páginas).";
            }
            catch (FormatException ex) { error = ex.Message; }
        }
        if (error is null) return;
        ErrorText.Text = error;
        ErrorText.Visibility = Visibility.Visible;
        args.Cancel = true;
    }
}
