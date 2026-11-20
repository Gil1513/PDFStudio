using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Dialogs;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 5.5: marca d'água (vai para a cópia de trabalho: tem desfazer e precisa salvar).
public sealed partial class ViewerPage
{
    private static List<string>? _installedFonts;

    private async Task WatermarkAsync()
    {
        if (_path is null || _document is null || DisplayPath is not { } path) return;
        var slot = _slots[CurrentIndex];
        Microsoft.UI.Xaml.Media.ImageSource? preview = null;
        try { preview = await PdfRenderer.RenderAsync(_document, slot.Index, 300 / Math.Max(slot.Width, slot.Height)); }
        catch { /* sem prévia, a janela continua funcionando */ }

        _installedFonts ??= await Task.Run(SystemFonts.Installed);
        int existing = 0;
        try { existing = await Task.Run(() => PdfWatermark.Count(path)); }
        catch { /* só serve para o aviso */ }

        var d = Prepare(new WatermarkDialog(_slots.Count, slot.Number, preview, slot.Width * PointsPerDip, slot.Height * PointsPerDip,
            _installedFonts, App.Settings.WatermarkFont, existing));
        if (await d.ShowAsync() != ContentDialogResult.Primary) return;

        string? ranges = d.Ranges;
        int pages = 0;
        if (d.Remove)
        {
            if (!await ApplyEditAsync((input, output) => pages = PdfWatermark.Remove(input, ranges, output), label: "Remover marca d'água")) return;
            if (pages == 0)
                ShowStatus(InfoBarSeverity.Informational, "Nenhuma marca d'água para remover",
                    "Só as marcas colocadas pelo PDF Studio podem ser removidas.");
            else
                ShowStatus(InfoBarSeverity.Success, "Marca d'água removida", $"{Plural(pages, "página", "páginas")} (salve para gravar).");
            return;
        }

        var options = d.Options;
        App.Settings.WatermarkFont = options.FontFamily;
        App.Settings.Save();
        if (await ApplyEditAsync((input, output) => pages = PdfWatermark.Add(input, options, ranges, output), label: "Adicionar marca d'água"))
            ShowStatus(InfoBarSeverity.Success, "Marca d'água aplicada",
                $"{Plural(pages, "página", "páginas")}. Use Desfazer (Ctrl+Z) para voltar, ou salve para gravar.");
    }
}
