using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 7.3: imprimir. Desde a tela de impressão própria, o trabalho fica em Services/PdfPrintJob e
// Dialogs/PrintDialog (também usados pela impressão em lote); aqui só as conferências do documento aberto.
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
        if (!(await PermissionsAsync()).Print)
        {
            ShowStatus(InfoBarSeverity.Warning, "Impressão não permitida",
                "Quem criou este PDF não permite imprimi-lo (restrição de permissões). Com a senha de permissões, use Proteger → Remover senha.");
            return;
        }
        if (_textEditor is not null) await FinishTextEditorAsync(commit: true);

        _printing = true;
        var job = new PdfPrintJob(path, _password, Path.GetFileNameWithoutExtension(FileName ?? "Documento"), DispatcherQueue,
            (severity, title, message) => ShowStatus(severity, title, message),
            _slots.Count > 0 ? _slots[CurrentIndex].Index + 1 : 0);
        job.Ended += () => _printing = false;
        await job.ShowAsync();
    }
}
