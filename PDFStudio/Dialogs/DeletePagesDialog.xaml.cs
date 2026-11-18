using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;

namespace PDFStudio.Dialogs;

/// <summary>Etapa 3.1: escolher quais páginas excluir.</summary>
public sealed partial class DeletePagesDialog : ContentDialog
{
    private readonly int _pageCount;

    public DeletePagesDialog(int pageCount, int currentPage, bool isSigned)
    {
        InitializeComponent();
        _pageCount = pageCount;
        RangesBox.Text = currentPage.ToString();
        HintText.Text = $"Use vírgulas e traços. O documento tem {pageCount} páginas; ao menos uma precisa ficar.";
        SignedWarning.IsOpen = isSigned;
        Opened += (_, _) => { RangesBox.Focus(FocusState.Programmatic); RangesBox.SelectAll(); };
    }

    /// <summary>Intervalos digitados, ex.: "2, 5-7".</summary>
    public string Ranges => RangesBox.Text.Trim();

    /// <summary>true = gravar por cima do arquivo aberto.</summary>
    public bool ReplaceOriginal => TargetChoice.SelectedIndex == 1;

    private void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        try
        {
            int count = PageRanges.Parse(Ranges, _pageCount).SelectMany(g => g).Distinct().Count();
            if (count >= _pageCount) throw new FormatException("Não é possível excluir todas as páginas.");
        }
        catch (FormatException ex)
        {
            ErrorText.Text = ex.Message;
            ErrorText.Visibility = Visibility.Visible;
            args.Cancel = true;
        }
    }
}
