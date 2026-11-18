using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 3.2: escolher páginas para copiar para um novo PDF.</summary>
public sealed partial class ExtractPagesDialog : ContentDialog
{
    private readonly int _pageCount;

    public ExtractPagesDialog(int pageCount, int currentPage, bool isSigned)
    {
        InitializeComponent();
        _pageCount = pageCount;
        RangesBox.Text = currentPage.ToString();
        HintText.Text = $"Use vírgulas e traços. O documento tem {pageCount} páginas. O arquivo aberto não muda.";
        SignedWarning.IsOpen = isSigned;
        Opened += (_, _) => { RangesBox.Focus(FocusState.Programmatic); RangesBox.SelectAll(); };
    }

    public string Ranges => RangesBox.Text.Trim();

    /// <summary>true = um arquivo por página; false = um único arquivo.</summary>
    public bool Separately => ModeChoice.SelectedIndex == 1;

    /// <summary>true = remover do arquivo aberto as páginas extraídas.</summary>
    public bool DeleteFromOriginal => DeleteCheck.IsChecked == true;

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            int count = PageRanges.Parse(Ranges, _pageCount).SelectMany(g => g).Distinct().Count();
            if (DeleteFromOriginal && count >= _pageCount)
                throw new FormatException("Para excluir as páginas do original, ao menos uma precisa ficar nele.");
        }
        catch (FormatException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
    }
}
