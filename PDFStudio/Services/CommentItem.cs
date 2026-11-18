using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;

namespace PDFStudio.Services;

/// <summary>Um comentário como aparece no painel "Comentários" (lista agrupada por página).</summary>
public sealed class CommentItem
{
    public CommentItem(AnnotationInfo info, bool firstOfPage)
    {
        Info = info;
        HeaderVisibility = firstOfPage ? Visibility.Visible : Visibility.Collapsed;
        var c = info.Color ?? new AnnotationColor(120, 120, 120);
        ColorBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
        string when = info.Modified is { } date ? date.ToString("dd/MM/yyyy HH:mm") : "";
        Meta = string.Join(" · ", new[] { info.Author, when }.Where(s => !string.IsNullOrWhiteSpace(s)));
        Text = info.Contents.Trim();
        TextVisibility = Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        AutomationName = $"{KindName} na página {info.PageIndex + 1}" + (Text.Length > 0 ? $": {Text}" : "");
    }

    public AnnotationInfo Info { get; }
    public string PageHeader => $"Página {Info.PageIndex + 1}";
    public Visibility HeaderVisibility { get; }
    public Brush ColorBrush { get; }
    public string Meta { get; }
    public string Text { get; }
    public Visibility TextVisibility { get; }
    public string AutomationName { get; }

    public string KindName => NameOf(Info.Kind);

    /// <summary>Nome lido pelos leitores de tela (e pela automação de interface) para o item da lista.</summary>
    public override string ToString() => AutomationName;

    public string Glyph => Info.Kind switch
    {
        AnnotationKind.Rectangle => "",
        AnnotationKind.Oval => "",
        AnnotationKind.Line => "",
        AnnotationKind.Ink => "",
        AnnotationKind.TextBox => "",
        AnnotationKind.Note => "",
        AnnotationKind.Highlight => "",
        AnnotationKind.Underline => "",
        AnnotationKind.StrikeOut => "",
        AnnotationKind.Stamp => "",
        _ => "",
    };

    public static string NameOf(AnnotationKind kind) => kind switch
    {
        AnnotationKind.Rectangle => "Retângulo",
        AnnotationKind.Oval => "Oval",
        AnnotationKind.Line => "Linha",
        AnnotationKind.Ink => "Desenho livre",
        AnnotationKind.TextBox => "Caixa de texto",
        AnnotationKind.Note => "Nota",
        AnnotationKind.Highlight => "Realce",
        AnnotationKind.Underline => "Sublinhado",
        AnnotationKind.StrikeOut => "Riscado",
        AnnotationKind.Stamp => "Carimbo",
        _ => "Comentário",
    };
}
