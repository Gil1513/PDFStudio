using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Dialogs;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 5.7: fundo das páginas (vai para a cópia de trabalho: tem desfazer e precisa salvar).
public sealed partial class ViewerPage
{
    private async Task BackgroundAsync()
    {
        if (_path is null || _document is null || DisplayPath is not { } path) return;
        var slot = _slots[CurrentIndex];
        Microsoft.UI.Xaml.Media.ImageSource? preview = null;
        try { preview = await PdfRenderer.RenderAsync(_document, slot.Index, 300 / Math.Max(slot.Width, slot.Height), transparent: true); }
        catch { /* sem prévia, a janela continua funcionando */ }

        int existing = 0;
        try { existing = await Task.Run(() => PdfBackground.Count(path)); }
        catch { /* só serve para o aviso */ }

        var d = Prepare(new BackgroundDialog(_slots.Count, slot.Number, preview, slot.Width * PointsPerDip, slot.Height * PointsPerDip, existing));
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;

        string? ranges = d.Ranges;
        int pages = 0;
        if (d.Remove)
        {
            if (!await ApplyEditAsync((input, output) => pages = PdfBackground.Remove(input, ranges, output), label: "Remover fundo")) return;
            if (pages == 0)
                ShowStatus(InfoBarSeverity.Informational, "Nenhum fundo para remover", "Só o fundo colocado pelo PDF Studio pode ser removido.");
            else
                ShowStatus(InfoBarSeverity.Success, "Fundo removido", $"{Plural(pages, "página", "páginas")} (salve para gravar).");
            return;
        }

        var options = d.Options;
        if (await ApplyEditAsync((input, output) => pages = PdfBackground.Add(input, options, ranges, output), label: "Adicionar fundo"))
            ShowStatus(InfoBarSeverity.Success, "Fundo aplicado",
                $"{Plural(pages, "página", "páginas")}. Use Desfazer (Ctrl+Z) para voltar, ou salve para gravar.");
    }
}
