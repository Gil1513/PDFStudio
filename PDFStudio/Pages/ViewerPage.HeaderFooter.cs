using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Dialogs;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 5.6: número de página, cabeçalho e rodapé (vão para a cópia de trabalho: têm desfazer e precisam salvar).
public sealed partial class ViewerPage
{
    /// <param name="pageNumberOnly">true = botão "Número de página" (a janela já vem com o rodapé numerado).</param>
    private async Task HeaderFooterAsync(bool pageNumberOnly)
    {
        if (_path is null || _document is null || DisplayPath is not { } path) return;
        var slot = _slots[CurrentIndex];
        Microsoft.UI.Xaml.Media.ImageSource? preview = null;
        try { preview = await PdfRenderer.RenderAsync(_document, slot.Index, 300 / Math.Max(slot.Width, slot.Height)); }
        catch { /* sem prévia, a janela continua funcionando */ }

        _installedFonts ??= await Task.Run(SystemFonts.Installed);
        int existing = 0;
        try { existing = await Task.Run(() => PdfHeaderFooter.Count(path)); }
        catch { /* só serve para o aviso */ }

        var d = Prepare(new HeaderFooterDialog(_slots.Count, slot.Number, preview, slot.Width * PointsPerDip, slot.Height * PointsPerDip,
            _installedFonts, App.Settings.HeaderFooterFont, existing, pageNumberOnly));
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;

        string? ranges = d.Ranges;
        int pages = 0;
        if (d.Remove)
        {
            if (!await ApplyEditAsync((input, output) => pages = PdfHeaderFooter.Remove(input, ranges, output), label: "Remover cabeçalho/rodapé")) return;
            if (pages == 0)
                ShowStatus(InfoBarSeverity.Informational, "Nada para remover",
                    "Só cabeçalhos, rodapés e números colocados pelo PDF Studio podem ser removidos.");
            else
                ShowStatus(InfoBarSeverity.Success, "Cabeçalho e rodapé removidos", $"{Plural(pages, "página", "páginas")} (salve para gravar).");
            return;
        }

        var options = d.Options;
        App.Settings.HeaderFooterFont = options.FontFamily;
        App.Settings.Save();
        if (await ApplyEditAsync((input, output) => pages = PdfHeaderFooter.Add(input, options, ranges, output), label: "Adicionar cabeçalho/rodapé"))
            ShowStatus(InfoBarSeverity.Success, pageNumberOnly ? "Páginas numeradas" : "Cabeçalho e rodapé aplicados",
                $"{Plural(pages, "página", "páginas")}. Use Desfazer (Ctrl+Z) para voltar, ou salve para gravar.");
    }
}
