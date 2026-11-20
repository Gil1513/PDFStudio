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
        if (_path is null || DisplayPath is not { } input) return;
        var dialog = Prepare(new ExtractPagesDialog(_slots.Count, CurrentIndex + 1, await IsSignedAsync(input)));
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        string ranges = dialog.Ranges;

        string? target = dialog.Separately
            ? await FilePickers.PickFolderAsync()
            : await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_path) + "-extraido");
        if (target is null) return;
        if (!dialog.Separately && SamePath(target, _path))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escolha outro nome", "O arquivo extraído não pode substituir o arquivo aberto.");
            return;
        }

        bool separately = dialog.Separately, delete = dialog.DeleteFromOriginal;
        LoadingBar.Visibility = Visibility.Visible;
        try
        {
            string message = await Task.Run(() => separately
                ? $"{PdfOperations.ExtractPagesSeparately(input, ranges, target).Count} arquivos criados em {target}."
                : $"{PdfOperations.ExtractPages(input, ranges, target)} página(s) salvas em {target}.");
            LoadingBar.Visibility = Visibility.Collapsed;

            // Tirar as páginas do documento aberto é uma alteração como as outras (dá para desfazer; grava ao salvar).
            if (delete)
            {
                if (!await ApplyEditAsync((i, o) => PdfOperations.DeletePages(i, ranges, o), label: "Extrair e excluir páginas")) return;
                message += " Elas saíram deste documento: salve para gravar (ou desfaça com Ctrl+Z).";
            }

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

    // ---------------- 3.5 Redimensionar ----------------

    private async Task ResizePagesAsync()
    {
        if (_path is null) return;
        var slot = _slots[CurrentIndex];
        var d = Prepare(new ResizePagesDialog(_slots.Count, slot.Number, await IsSignedAsync(_path),
            slot.Width * PointsPerDip, slot.Height * PointsPerDip));
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;

        string? ranges = d.Ranges;
        var (width, height) = d.SizePt;
        var orientation = d.Orientation;
        bool scale = d.ScaleContent;
        await ApplyPageToolAsync("Páginas redimensionadas", "-redimensionado", d.ReplaceOriginal,
            (input, output) => PdfOperations.ResizePages(input, ranges, width, height, orientation, scale, output));
    }

    // ---------------- Fluxo comum ----------------

    /// <summary>O visualizador mede em DIPs (1/96 pol.); o PDF, em pontos (1/72 pol.).</summary>
    private const double PointsPerDip = 72.0 / 96;

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Executa uma ferramenta de página. "Alterar este documento" (Etapa 7.10): vira uma alteração como as outras,
    /// com Desfazer/Refazer, e o arquivo só muda ao salvar. "Novo arquivo": pergunta onde salvar, grava a partir
    /// do que está na tela (com as alterações ainda não salvas) e abre o resultado.
    /// </summary>
    private async Task ApplyPageToolAsync(string successTitle, string suffix, bool replaceOriginal,
        Action<string, string> operation, int? goToPage = null)
    {
        if (_path is null || DisplayPath is not { } input) return;
        if (replaceOriginal)
        {
            int before = _slots.Count;
            if (!await ApplyEditAsync(operation, label: successTitle)) return;
            if (goToPage is { } target && _document is { } doc) RebuildPages(doc, target);
            int diff = _slots.Count - before;
            string detail = diff < 0 ? $"{Plural(-diff, "página a menos", "páginas a menos")}. "
                          : diff > 0 ? $"{Plural(diff, "página a mais", "páginas a mais")}. " : "";
            ShowStatus(InfoBarSeverity.Success, successTitle,
                detail + "Dá para desfazer (Ctrl+Z). Salve (Ctrl+S) para gravar no arquivo.");
            return;
        }
        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_path) + suffix);
        if (output is null) return;
        if (SamePath(output, _path))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escolha outro nome",
                "Para alterar o arquivo aberto, use a opção \"Alterar este documento\" (dá para desfazer).");
            return;
        }

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
        if (SamePath(_path ?? "", output))
            ShowStatus(InfoBarSeverity.Success, successTitle, $"Novo arquivo salvo e aberto: {output}");
    }

    /// <summary>Abre (ou recarrega) um arquivo e volta para a página em que o usuário estava.</summary>
    private async Task ReopenAsync(string path, int pageIndex)
    {
        // Abre de novo (mesmo que seja o mesmo arquivo) já na página certa: uma só volta-à-página, sem
        // disputar com a da última página vista.
        await OpenAsync(path, startPage: pageIndex);
    }
}
