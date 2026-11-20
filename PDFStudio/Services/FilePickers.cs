using PDFStudio.Controls;

namespace PDFStudio.Services;

/// <summary>
/// Abrir arquivos, salvar e escolher pasta. Desde a tela própria (Controls/FileBrowser), não usa mais as janelas
/// do Windows; os nomes e o jeito de chamar continuam os mesmos para o resto do app.
/// </summary>
public static class FilePickers
{
    private static readonly string[] Pdf = [".pdf"];

    public static async Task<string?> PickPdfAsync() =>
        (await FileBrowserHost.ShowAsync(new FileBrowserOptions(FileBrowserMode.Open, "Abrir PDF", Pdf, "Documento PDF")))?[0];

    public static async Task<IReadOnlyList<string>> PickPdfsAsync() =>
        await FileBrowserHost.ShowAsync(new FileBrowserOptions(FileBrowserMode.OpenMany, "Escolher PDFs", Pdf, "Documento PDF")) ?? [];

    /// <param name="extensions">".png", ".jpg"… ; "*" (ou nada) = qualquer arquivo.</param>
    public static async Task<string?> PickFileAsync(params string[] extensions) =>
        (await FileBrowserHost.ShowAsync(new FileBrowserOptions(FileBrowserMode.Open, "Escolher arquivo", Clean(extensions), TypeName(Clean(extensions)))))?[0];

    /// <summary>Escolhe vários arquivos de uma vez (ex.: imagens para virar PDF).</summary>
    public static async Task<IReadOnlyList<string>> PickFilesAsync(params string[] extensions) =>
        await FileBrowserHost.ShowAsync(new FileBrowserOptions(FileBrowserMode.OpenMany, "Escolher arquivos", Clean(extensions), TypeName(Clean(extensions)))) ?? [];

    public static async Task<string?> SavePdfAsync(string suggestedName)
    {
        string name = suggestedName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ? suggestedName : suggestedName + ".pdf";
        return (await FileBrowserHost.ShowAsync(new FileBrowserOptions(FileBrowserMode.Save, "Salvar como", Pdf, "Documento PDF", name)))?[0];
    }

    /// <summary>Escolhe onde salvar um arquivo qualquer (o tipo vem da extensão do nome sugerido).</summary>
    public static async Task<string?> SaveFileAsync(string suggestedName)
    {
        string ext = Path.GetExtension(suggestedName);
        string[] extensions = ext.Length > 1 ? [ext] : [];
        return (await FileBrowserHost.ShowAsync(new FileBrowserOptions(FileBrowserMode.Save, "Salvar como", extensions,
            TypeName(extensions), suggestedName)))?[0];
    }

    public static async Task<string?> PickFolderAsync() =>
        (await FileBrowserHost.ShowAsync(new FileBrowserOptions(FileBrowserMode.Folder, "Escolher pasta", [], "Pasta")))?[0];

    /// <summary>Tira o "*" (= qualquer arquivo) e garante o ponto: "png" → ".png".</summary>
    private static string[] Clean(string[] extensions) => extensions
        .Select(e => e.Trim().TrimStart('*'))
        .Where(e => e.Trim('.').Length > 0)
        .Select(e => e.StartsWith('.') ? e : "." + e)
        .ToArray();

    /// <summary>Nome do tipo na lista: "Documento PDF", "Imagens", "Arquivo DOCX"…</summary>
    private static string TypeName(IReadOnlyList<string> extensions)
    {
        if (extensions.Count == 0) return "Todos os arquivos";
        var set = extensions.Select(e => e.TrimStart('.').ToLowerInvariant()).ToHashSet();
        if (set.SetEquals(["pdf"])) return "Documento PDF";
        if (set.All(e => e is "png" or "jpg" or "jpeg" or "bmp" or "gif" or "tif" or "tiff" or "webp")) return "Imagens";
        if (set.All(e => e is "ttf" or "otf")) return "Fontes";
        if (set.All(e => e is "pfx" or "p12")) return "Certificado digital";
        return extensions.Count == 1 ? $"Arquivo {set.First().ToUpperInvariant()}" : "Arquivos";
    }
}
