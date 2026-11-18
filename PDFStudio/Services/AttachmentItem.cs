using Microsoft.UI.Xaml;
using PDFStudio.Core;

namespace PDFStudio.Services;

/// <summary>Um anexo como aparece no painel "Anexos".</summary>
public sealed class AttachmentItem
{
    public AttachmentItem(AttachmentInfo info)
    {
        Info = info;
        var parts = new List<string> { FormatSize(info.Size) };
        if (info.Modified is { } date) parts.Add(date.ToString("dd/MM/yyyy HH:mm"));
        if (info.PageIndex is { } page) parts.Add($"na página {page + 1}");
        Meta = string.Join(" · ", parts);
        Description = info.Description.Trim();
        DescriptionVisibility = Description.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationName = $"{info.Name}, {Meta}" + (Description.Length > 0 ? $": {Description}" : "");
    }

    public AttachmentInfo Info { get; }
    public string Name => Info.Name;
    public string Meta { get; }
    public string Description { get; }
    public Visibility DescriptionVisibility { get; }
    public string AutomationName { get; }

    public override string ToString() => AutomationName;

    public string Glyph => Path.GetExtension(Info.Name).ToLowerInvariant() switch
    {
        ".pdf" => "",
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".bmp" or ".webp" or ".tif" or ".tiff" => "",
        ".doc" or ".docx" or ".txt" or ".rtf" or ".odt" or ".md" => "",
        ".xls" or ".xlsx" or ".csv" or ".ods" => "",
        ".zip" or ".rar" or ".7z" => "",
        ".mp3" or ".wav" or ".m4a" => "",
        ".mp4" or ".mov" or ".avi" or ".mkv" => "",
        _ when PdfAttachments.IsRisky(Info.Name) => "", // aviso: programa ou script
        _ => "",
    };

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB",
    };
}
