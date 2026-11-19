using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Etapa 5.8: modo "Editar tudo" — mostra os blocos da página (parágrafos e imagens) e permite selecionar um.
public sealed partial class ViewerPage
{
    // ---------------- Blocos de cada página (lidos uma vez por versão do arquivo) ----------------

    private readonly Dictionary<int, Task<List<ContentBlock>>> _blockCache = [];
    private string? _blockCachePath;

    private Task<List<ContentBlock>> BlocksOfPage(int page)
    {
        string path = DisplayPath!;
        if (_blockCachePath != path) { _blockCache.Clear(); _blockCachePath = path; }
        if (!_blockCache.TryGetValue(page, out var task))
            _blockCache[page] = task = Task.Run(() => PdfContentBlocks.Detect(path, page));
        return task;
    }

    /// <summary>Bloco sob o ponto (DIPs da superfície), se os blocos já foram lidos. Texto ganha de imagem (fica por cima).</summary>
    private ContentBlock? BlockAt(PageSlot slot, Point p)
    {
        if (DisplayPath is null) return null;
        var task = BlocksOfPage(slot.Index);
        if (!task.IsCompletedSuccessfully) return null;
        double x = p.X / DipsPerPoint, y = p.Y / DipsPerPoint;
        return task.Result.Where(b => b.Contains(x, y)).OrderBy(b => b.Kind == BlockKind.Text ? 0 : 1).ThenBy(b => b.Width * b.Height).FirstOrDefault();
    }

    // ---------------- Contornos ----------------

    private readonly List<(ContentBlock Block, Shapes.Rectangle Box)> _blockOutlines = [];
    private DrawingSurface? _blockSurface;
    private ContentBlock? _hoveredBlock, _selectedBlock;

    private async Task ShowBlockOutlinesAsync(DrawingSurface surface, bool force = false)
    {
        if (!force && ReferenceEquals(surface, _blockSurface)) return;
        if (SlotOf(surface) is not { } slot || DisplayPath is null) return;
        RemoveBlockOutlines();
        _blockSurface = surface;
        var blocks = await BlocksOfPage(slot.Index);
        if (_tool?.Mode != DrawMode.EditAll || !ReferenceEquals(surface, _blockSurface)) return;
        foreach (var block in blocks)
        {
            var box = new Shapes.Rectangle
            {
                Width = (block.Width + 4) * DipsPerPoint,
                Height = (block.Height + 4) * DipsPerPoint,
                StrokeThickness = 1,
                StrokeDashArray = [4, 3],
                IsHitTestVisible = false,
            };
            Canvas.SetLeft(box, (block.X - 2) * DipsPerPoint);
            Canvas.SetTop(box, (block.Y - 2) * DipsPerPoint);
            surface.Children.Add(box);
            _blockOutlines.Add((block, box));
        }
        PaintBlockOutlines();
        if (blocks.Count == 0)
            ToolHintText.Text = "Nenhum texto ou imagem encontrado nesta página (se for digitalizada, use OCR antes).";
    }

    private void RemoveBlockOutlines()
    {
        foreach (var (_, box) in _blockOutlines) _blockSurface?.Children.Remove(box);
        _blockOutlines.Clear();
        _blockSurface = null;
        _hoveredBlock = _selectedBlock = null;
    }

    /// <summary>Texto em azul (cor de destaque), imagem em verde; o bloco sob o mouse fica preenchido e o selecionado, com borda cheia.</summary>
    private void PaintBlockOutlines()
    {
        var accent = ((SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]).Color;
        var green = Windows.UI.Color.FromArgb(255, 46, 160, 67);
        foreach (var (block, box) in _blockOutlines)
        {
            var c = block.Kind == BlockKind.Text ? accent : green;
            bool selected = ReferenceEquals(block, _selectedBlock), hovered = ReferenceEquals(block, _hoveredBlock);
            box.Stroke = new SolidColorBrush(c);
            box.StrokeThickness = selected ? 2 : 1;
            box.StrokeDashArray = selected ? null : [4, 3];
            box.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb((byte)(selected ? 40 : hovered ? 24 : 0), c.R, c.G, c.B));
        }
    }

    private void HoverBlock(DrawingSurface surface, Point p)
    {
        if (SlotOf(surface) is not { } slot) return;
        // O mouse pode já estar sobre a página quando o modo é ligado (sem o evento de "entrar").
        if (!ReferenceEquals(surface, _blockSurface)) _ = ShowBlockOutlinesAsync(surface);
        var block = BlockAt(slot, p);
        surface.SetCursor(block is null ? null : block.Kind == BlockKind.Text ? InputSystemCursorShape.IBeam : InputSystemCursorShape.SizeAll);
        if (ReferenceEquals(block, _hoveredBlock)) return;
        _hoveredBlock = block;
        PaintBlockOutlines();
    }

    private void SelectBlock(PageSlot slot, Point p)
    {
        _selectedBlock = BlockAt(slot, p);
        PaintBlockOutlines();
        ToolHintText.Text = _selectedBlock is { } b ? Describe(b) : CommentTools["editartudo"].Hint;
        ToolTipService.SetToolTip(ToolHintText, _selectedBlock?.Text);
    }

    /// <summary>"Texto · Times New Roman 11 pt, negrito · 3 linhas: “Primeira linha…”" ou "Imagem · 5,6 × 2,8 cm".</summary>
    private static string Describe(ContentBlock b)
    {
        static string Cm(double pt) => (pt / 72 * 2.54).ToString("0.0");
        if (b.Kind == BlockKind.Image) return $"Imagem · {Cm(b.Width)} × {Cm(b.Height)} cm";
        var style = new List<string> { $"{b.FontFamily} {b.FontSize:0.#} pt" };
        if (b.Bold) style.Add("negrito");
        if (b.Italic) style.Add("itálico");
        string first = b.Lines[0].Text;
        if (first.Length > 40 || b.Lines.Count > 1) first = first[..Math.Min(40, first.Length)] + "…";
        return $"Texto · {string.Join(", ", style)} · {Plural(b.Lines.Count, "linha", "linhas")}: “{first}”";
    }
}
