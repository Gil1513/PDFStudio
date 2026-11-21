using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

/// <summary>Etapa 6.2: redigir. O usuário marca áreas (várias, em várias páginas) e aplica tudo de uma vez.</summary>
public sealed partial class ViewerPage
{
    /// <summary>Área marcada (em DIPs da página na tela) e o retângulo que a mostra.</summary>
    private sealed record RedactMark(PageSlot Slot, Rect Box, Shapes.Rectangle Shape)
    {
        public DrawingSurface? Surface { get; set; }
    }

    private readonly List<RedactMark> _redactMarks = [];

    private static readonly SolidColorBrush RedactStroke = new(Windows.UI.Color.FromArgb(255, 211, 47, 47));

    private void AddRedactMark(DrawingSurface surface, PageSlot slot, Rect box)
    {
        var shape = new Shapes.Rectangle
        {
            Width = box.Width,
            Height = box.Height,
            Stroke = RedactStroke,
            StrokeThickness = 2,
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(110, 0, 0, 0)),
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(shape, box.X);
        Canvas.SetTop(shape, box.Y);
        surface.Children.Add(shape);
        _redactMarks.Add(new RedactMark(slot, box, shape) { Surface = surface });
        UpdateRedactHint();
    }

    private RedactMark? RedactMarkAt(PageSlot slot, Point p) =>
        _redactMarks.LastOrDefault(m => m.Slot == slot && m.Box.Contains(p));

    private void RemoveRedactMark(RedactMark mark)
    {
        mark.Surface?.Children.Remove(mark.Shape);
        _redactMarks.Remove(mark);
        UpdateRedactHint();
    }

    private void ClearRedactMarks()
    {
        foreach (var mark in _redactMarks) mark.Surface?.Children.Remove(mark.Shape);
        _redactMarks.Clear();
    }

    /// <summary>As páginas na tela são reaproveitadas ao rolar: põe em cada uma só as marcas da página que ela mostra.</summary>
    private void ShowRedactMarks(DrawingSurface surface)
    {
        var slot = SlotOf(surface);
        foreach (var mark in _redactMarks)
        {
            bool belongs = mark.Slot == slot;
            if (ReferenceEquals(mark.Surface, surface) && !belongs)
            {
                surface.Children.Remove(mark.Shape);
                mark.Surface = null;
            }
            else if (belongs && !ReferenceEquals(mark.Surface, surface))
            {
                mark.Surface?.Children.Remove(mark.Shape);
                surface.Children.Add(mark.Shape);
                mark.Surface = surface;
            }
        }
    }

    private void UpdateRedactHint()
    {
        if (_tool?.Mode != DrawMode.Redact) return;
        int pages = _redactMarks.Select(m => m.Slot.Index).Distinct().Count();
        ToolHintText.Text = _redactMarks.Count == 0
            ? CommentTools["redigir"].Hint
            : $"{Plural(_redactMarks.Count, "área marcada", "áreas marcadas")} em {Plural(pages, "página", "páginas")}. " +
              "Clique numa marca para desmarcar; quando terminar, clique em Aplicar redação.";
        RedactApplyButton.IsEnabled = _redactMarks.Count > 0;
    }

    private async void RedactApply_Click(object sender, RoutedEventArgs e) => await ApplyRedactionAsync();

    private async Task ApplyRedactionAsync()
    {
        if (_redactMarks.Count == 0) return;
        int pages = _redactMarks.Select(m => m.Slot.Index).Distinct().Count();
        var dialog = Prepare(new ContentDialog
        {
            Title = "Aplicar redação",
            Content = new TextBlock
            {
                Text = $"{Plural(_redactMarks.Count, "área", "áreas")} em {Plural(pages, "página", "páginas")} vão ser cobertas com tarja preta, " +
                       "e o que estiver embaixo será apagado do arquivo: letras, imagens (a parte embaixo da área), desenhos inteiros " +
                       "dentro da área, comentários, links e campos com o valor guardado.\n\n" +
                       "Se alguma coisa embaixo da área não puder ser apagada com segurança (ex.: imagem num formato especial), " +
                       "o app avisa e não altera nada.\n\n" +
                       "Até salvar, Desfazer (Ctrl+Z) volta atrás. Depois de salvar, o que foi apagado não pode ser recuperado.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 460,
            },
            PrimaryButtonText = "Redigir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var areas = _redactMarks
            .Select(m => new RedactionArea(m.Slot.Index, m.Box.X / DipsPerPoint, m.Box.Y / DipsPerPoint, m.Box.Width / DipsPerPoint, m.Box.Height / DipsPerPoint))
            .ToList();
        RedactionResult? result = null;
        if (!await ApplyEditAsync((input, output) => result = PdfRedaction.Apply(input, areas, output, new AnnotationColor(0, 0, 0), ImageShrinker.DecodeJpegPixels), label: "Redigir")) return;
        ClearRedactMarks();
        UpdateRedactHint();
        if (result is null) return;

        var parts = new List<string>();
        if (result.Letters > 0) parts.Add(Plural(result.Letters, "letra apagada", "letras apagadas"));
        if (result.Images > 0) parts.Add(Plural(result.Images, "imagem apagada", "imagens apagadas") + " (inteira ou só a parte embaixo da tarja)");
        if (result.Drawings > 0) parts.Add(Plural(result.Drawings, "desenho apagado", "desenhos apagados"));
        if (result.Annotations > 0) parts.Add(Plural(result.Annotations, "comentário/link/campo apagado", "comentários/links/campos apagados"));
        string summary = parts.Count > 0 ? string.Join(", ", parts) + "." : "Nada para apagar embaixo das áreas; ficaram só as tarjas.";
        if (result.CoveredOnly > 0)
            ShowStatus(InfoBarSeverity.Warning, "Redação aplicada, com ressalva",
                $"{summary} {Plural(result.CoveredOnly, "item ficou", "itens ficaram")} só coberto(s) pela tarja (texto com fonte desenhada, que não dá para apagar letra por letra). Salve com Ctrl+S.");
        else
            ShowStatus(InfoBarSeverity.Success, "Redação aplicada", $"{summary} Salve com Ctrl+S para gravar no arquivo; Ctrl+Z desfaz.");
    }
}
