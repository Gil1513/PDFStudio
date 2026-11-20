using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Graphics.Printing;

namespace PDFStudio.Pages;

// Etapa 7.3: imprimir pela janela de impressão do Windows. O trabalho fica em Services/PdfPrintJob (também usado
// pela impressão em lote); aqui só as conferências do documento aberto.
public sealed partial class ViewerPage
{
    private bool _printing;

    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();
    private async void Accel_Print(Microsoft.UI.Xaml.Input.KeyboardAccelerator s, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        await PrintAsync();
    }

    private async Task PrintAsync()
    {
        if (DisplayPath is not { } path || _document is null || _printing) return;
        if (!PrintManager.IsSupported())
        {
            ShowStatus(InfoBarSeverity.Error, "Impressão indisponível", "Este Windows não oferece a janela de impressão para o app.");
            return;
        }
        if (!await Task.Run(() => PdfPasswords.AllowsPrinting(path, _password)))
        {
            ShowStatus(InfoBarSeverity.Warning, "Impressão não permitida",
                "Quem criou este PDF não permite imprimi-lo (restrição de permissões). Com a senha de permissões, use Proteger → Remover senha.");
            return;
        }
        if (_textEditor is not null) await FinishTextEditorAsync(commit: true);

        _printing = true;
        var job = new PdfPrintJob(path, _password, Path.GetFileNameWithoutExtension(FileName ?? "Documento"), DispatcherQueue,
            (severity, title, message) => ShowStatus(severity, title, message));
        job.Ended += () => _printing = false;
        await job.ShowAsync();
    }
}
