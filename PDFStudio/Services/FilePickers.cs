using Microsoft.Windows.Storage.Pickers;

namespace PDFStudio.Services;

/// <summary>
/// Seletores de arquivo do Windows App SDK. Funcionam em apps não empacotados porque
/// recebem o WindowId da janela principal.
/// </summary>
public static class FilePickers
{
    private static Microsoft.UI.WindowId WindowId => App.MainWindow!.AppWindow.Id;

    public static async Task<string?> PickPdfAsync()
    {
        var picker = new FileOpenPicker(WindowId) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<IReadOnlyList<string>> PickPdfsAsync()
    {
        var picker = new FileOpenPicker(WindowId) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        picker.FileTypeFilter.Add(".pdf");
        var results = await picker.PickMultipleFilesAsync();
        return results?.Select(r => r.Path).ToList() ?? [];
    }

    public static async Task<string?> PickFileAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker(WindowId) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        var result = await picker.PickSingleFileAsync();
        return result?.Path;
    }

    public static async Task<string?> SavePdfAsync(string suggestedName)
    {
        var picker = new FileSavePicker(WindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add("Documento PDF", [".pdf"]);
        var result = await picker.PickSaveFileAsync();
        return result?.Path;
    }

    public static async Task<string?> PickFolderAsync()
    {
        var picker = new FolderPicker(WindowId) { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        var result = await picker.PickSingleFolderAsync();
        return result?.Path;
    }
}
