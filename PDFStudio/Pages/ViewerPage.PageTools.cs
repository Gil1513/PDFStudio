using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Dialogs;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 3: ferramentas de página da categoria "Organizar" (excluir, extrair, inserir, recortar, redimensionar).
public sealed partial class ViewerPage
{
    private T Prepare<T>(T dialog) where T : ContentDialog
    {
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = App.Settings.Theme;
        return dialog;
    }

    private async Task<bool> IsSignedAsync(string path)
    {
        try { return await Task.Run(() => DocumentPropertiesService.HasSignatures(path)); }
        catch { return false; } // na dúvida, não bloqueia a ferramenta
    }

    // ---------------- 3.1 Excluir ----------------

    private async Task DeletePagesAsync()
    {
        if (_path is null) return;
        var dialog = Prepare(new DeletePagesDialog(_slots.Count, CurrentIndex + 1, await IsSignedAsync(_path)));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string ranges = dialog.Ranges;
        await ApplyPageToolAsync("Páginas excluídas", "-sem-paginas", dialog.ReplaceOriginal,
            (input, output) => PdfOperations.DeletePages(input, ranges, output));
    }

    // ---------------- Fluxo comum ----------------

    /// <summary>
    /// Executa uma operação que gera um novo PDF a partir do arquivo aberto: pergunta onde salvar
    /// (se não for substituir o original), roda a operação fora da tela e reabre o resultado.
    /// </summary>
    private async Task ApplyPageToolAsync(string successTitle, string suffix, bool replaceOriginal, Action<string, string> operation)
    {
        if (_path is null) return;
        string input = _path;
        string? output = replaceOriginal
            ? input
            : await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(input) + suffix);
        if (output is null) return;

        int page = CurrentIndex;
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            await Task.Run(() => operation(input, output));
        }
        catch (Exception ex)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            ShowStatus(InfoBarSeverity.Error, "Não foi possível concluir", ex.Message);
            return;
        }

        await ReopenAsync(output, page);
        bool replaced = string.Equals(Path.GetFullPath(output), Path.GetFullPath(input), StringComparison.OrdinalIgnoreCase);
        ShowStatus(InfoBarSeverity.Success, successTitle,
            replaced ? "O arquivo aberto foi atualizado." : $"Novo arquivo salvo e aberto: {output}");
    }

    /// <summary>Abre (ou recarrega) um arquivo e volta para a página em que o usuário estava.</summary>
    private async Task ReopenAsync(string path, int pageIndex)
    {
        _path = null; // força recarregar mesmo que seja o mesmo arquivo
        await OpenAsync(path);
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low,
            () => GoToPage(Math.Min(pageIndex, _slots.Count - 1)));
    }
}
