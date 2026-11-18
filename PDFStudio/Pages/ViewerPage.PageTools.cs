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

    // ---------------- 3.2 Extrair ----------------

    private async Task ExtractPagesAsync()
    {
        if (_path is null) return;
        string input = _path;
        var dialog = Prepare(new ExtractPagesDialog(_slots.Count, CurrentIndex + 1, await IsSignedAsync(input)));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string ranges = dialog.Ranges;

        string? target = dialog.Separately
            ? await FilePickers.PickFolderAsync()
            : await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(input) + "-extraido");
        if (target is null) return;
        if (!dialog.Separately && SamePath(target, input))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escolha outro nome", "O arquivo extraído não pode substituir o arquivo aberto.");
            return;
        }

        int page = CurrentIndex;
        bool separately = dialog.Separately, delete = dialog.DeleteFromOriginal;
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            string message = await Task.Run(() =>
            {
                string result = separately
                    ? $"{PdfOperations.ExtractPagesSeparately(input, ranges, target).Count} arquivos criados em {target}."
                    : $"{PdfOperations.ExtractPages(input, ranges, target)} página(s) salvas em {target}.";
                if (delete) PdfOperations.DeletePages(input, ranges, input);
                return result;
            });

            if (delete) await ReopenAsync(input, page);
            else LoadingBar.Visibility = Visibility.Collapsed;

            if (separately)
                ShowStatus(InfoBarSeverity.Success, "Páginas extraídas", message, "Abrir pasta",
                    () => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true }));
            else
                ShowStatus(InfoBarSeverity.Success, "Páginas extraídas", message, "Abrir o arquivo extraído", async () => await OpenAsync(target));
        }
        catch (Exception ex)
        {
            LoadingBar.Visibility = Visibility.Collapsed;
            ShowStatus(InfoBarSeverity.Error, "Não foi possível extrair", ex.Message);
        }
    }

    // ---------------- 3.3 Inserir ----------------

    private async Task InsertPagesAsync()
    {
        if (_path is null) return;
        var d = Prepare(new InsertPagesDialog(_slots.Count, CurrentIndex + 1, await IsSignedAsync(_path)));
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;

        // Lê as opções da janela agora (na thread da tela), antes de rodar em segundo plano.
        int position = d.Position;
        Action<string, string> operation;
        if (d.FromPdf)
        {
            string other = d.OtherPath!; string? ranges = d.OtherRanges;
            operation = (input, output) => PdfOperations.InsertPagesFromPdf(input, other, ranges, position, output);
        }
        else
        {
            int count = d.Count; var size = d.Size; bool landscape = d.Landscape;
            operation = (input, output) => PdfOperations.InsertBlankPages(input, position, count, output, size, landscape);
        }

        // Depois de salvar, mostra a primeira página inserida.
        await ApplyPageToolAsync("Páginas inseridas", "-com-paginas", d.ReplaceOriginal, operation, goToPage: position);
    }

    // ---------------- 3.4 Recortar ----------------

    private async Task CropPagesAsync()
    {
        if (_path is null || _document is null) return;
        var slot = _slots[CurrentIndex];
        Microsoft.UI.Xaml.Media.ImageSource? preview = null;
        try { preview = await PdfRenderer.RenderAsync(_document, slot.Index, 300 / Math.Max(slot.Width, slot.Height)); }
        catch { /* sem prévia, a janela continua funcionando */ }

        const double PointsPerDip = 72.0 / 96;
        var d = Prepare(new CropPagesDialog(_slots.Count, slot.Number, await IsSignedAsync(_path),
            preview, slot.Width * PointsPerDip, slot.Height * PointsPerDip));
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;

        string? ranges = d.Ranges;
        var margins = d.Margins;
        if (d.RemoveCrop)
            await ApplyPageToolAsync("Recortes desfeitos", "-sem-recorte", d.ReplaceOriginal,
                (input, output) => PdfOperations.RemoveCrop(input, ranges, output));
        else
            await ApplyPageToolAsync("Páginas recortadas", "-recortado", d.ReplaceOriginal,
                (input, output) => PdfOperations.CropPages(input, ranges, margins, output));
    }

    // ---------------- Fluxo comum ----------------

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executa uma operação que gera um novo PDF a partir do arquivo aberto: pergunta onde salvar
    /// (se não for substituir o original), roda a operação fora da tela e reabre o resultado.
    /// </summary>
    private async Task ApplyPageToolAsync(string successTitle, string suffix, bool replaceOriginal,
        Action<string, string> operation, int? goToPage = null)
    {
        if (_path is null) return;
        string input = _path;
        string? output = replaceOriginal
            ? input
            : await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(input) + suffix);
        if (output is null) return;

        int page = goToPage ?? CurrentIndex;
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
        bool replaced = SamePath(output, input);
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
