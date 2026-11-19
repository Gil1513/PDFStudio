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

    /// <summary>Escolhe vários arquivos de uma vez (ex.: imagens para virar PDF).</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(params string[] extensions)
    {
        var picker = new FileOpenPicker(WindowId) { SuggestedStartLocation = PickerLocationId.PicturesLibrary };
        foreach (var ext in extensions) picker.FileTypeFilter.Add(ext);
        var results = await picker.PickMultipleFilesAsync();
        return results?.Select(r => r.Path).ToList() ?? [];
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

    /// <summary>Escolhe onde salvar um arquivo qualquer (o tipo vem da extensão do nome sugerido).</summary>
    public static async Task<string?> SaveFileAsync(string suggestedName)
    {
        string ext = Path.GetExtension(suggestedName);
        var picker = new FileSavePicker(WindowId)
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName),
        };
        picker.FileTypeChoices.Add(ext.Length > 1 ? $"Arquivo {ext.ToUpperInvariant().TrimStart('.')}" : "Arquivo", [ext.Length > 1 ? ext : ".bin"]);
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
