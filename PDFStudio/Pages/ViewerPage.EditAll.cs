using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
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
            _blockCache[page] = task = Task.Run(() => PdfContentBlocks.Detect(path, page).Concat(PdfTextEditor.Shapes(path, page)).ToList());
        return task;
    }

    /// <summary>Bloco sob o ponto (DIPs da superfície), se os blocos já foram lidos. Texto ganha de imagem (fica por cima).</summary>
    private ContentBlock? BlockAt(PageSlot slot, Point p)
    {
        if (DisplayPath is null) return null;
        var task = BlocksOfPage(slot.Index);
        if (!task.IsCompletedSuccessfully) return null;
        double x = p.X / DipsPerPoint, y = p.Y / DipsPerPoint;
        // Desenhos finos (linhas) ganham uma folga para dar para clicar neles.
        static bool Hit(ContentBlock b, double x, double y, double pad) =>
            x >= b.X - pad && x <= b.Right + pad && y >= b.Y - pad && y <= b.Bottom + pad;
        return task.Result.Where(b => Hit(b, x, y, b.Kind == BlockKind.Shape ? 3 : 0))
            .OrderBy(b => b.Kind switch { BlockKind.Text => 0, BlockKind.Image => 1, _ => 2 })
            .ThenBy(b => b.Width * b.Height).FirstOrDefault();
    }

    // ---------------- Contornos e seleção ----------------

    private readonly List<(ContentBlock Block, Shapes.Rectangle Box)> _blockOutlines = [];
    private DrawingSurface? _blockSurface;
    private ContentBlock? _hoveredBlock;
    /// <summary>Blocos selecionados (textos e imagens), na ordem em que foram escolhidos.</summary>
    private readonly List<ContentBlock> _selection = [];

    private bool IsSelected(ContentBlock block) => _selection.Any(b => ReferenceEquals(b, block));

    private async Task<List<ContentBlock>> ShowBlockOutlinesAsync(DrawingSurface surface, bool force = false)
    {
        if (!force && ReferenceEquals(surface, _blockSurface)) return [];
        if (SlotOf(surface) is not { } slot || DisplayPath is null) return [];
        RemoveBlockOutlines();
        _blockSurface = surface;
        var blocks = await BlocksOfPage(slot.Index);
        if (_tool?.Mode != DrawMode.EditAll || !ReferenceEquals(surface, _blockSurface)) return [];
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
        return blocks;
    }

    private void RemoveBlockOutlines()
    {
        foreach (var (_, box) in _blockOutlines) _blockSurface?.Children.Remove(box);
        _blockOutlines.Clear();
        RemoveBlockActions();
        CancelBlockDrag(_blockSurface);
        CancelMarquee();
        _blockSurface = null;
        _hoveredBlock = null;
        _selection.Clear();
    }

    /// <summary>Texto em azul (cor de destaque), imagem em verde; o bloco sob o mouse fica preenchido e os selecionados, com borda cheia.</summary>
    private void PaintBlockOutlines()
    {
        var accent = ((SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]).Color;
        var green = Windows.UI.Color.FromArgb(255, 46, 160, 67);
        var orange = Windows.UI.Color.FromArgb(255, 230, 120, 0);
        foreach (var (block, box) in _blockOutlines)
        {
            var c = block.Kind switch { BlockKind.Text => accent, BlockKind.Image => green, _ => orange };
            bool selected = IsSelected(block), hovered = ReferenceEquals(block, _hoveredBlock);
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
        surface.SetCursor(block is null ? null
            : block.Kind == BlockKind.Text && !IsSelected(block) ? InputSystemCursorShape.IBeam : InputSystemCursorShape.SizeAll);
        if (ReferenceEquals(block, _hoveredBlock)) return;
        _hoveredBlock = block;
        PaintBlockOutlines();
    }

    private static bool CtrlDown() =>
        InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Clique no modo "Editar tudo": seleciona o bloco (com Ctrl, junta ou tira da seleção); clicar de novo num texto
    /// que já é o único selecionado abre a edição.
    /// </summary>
    private async Task ClickBlockAsync(DrawingSurface surface, PageSlot slot, Point p, bool toggle = false)
    {
        var block = BlockAt(slot, p);
        if (toggle)
        {
            if (block is null) return;
            if (IsSelected(block)) _selection.RemoveAll(b => ReferenceEquals(b, block));
            else _selection.Add(block);
        }
        else if (block is { Kind: BlockKind.Text } && _selection.Count == 1 && ReferenceEquals(_selection[0], block))
        {
            await OpenBlockEditorAsync(surface, slot, block);
            return;
        }
        else
        {
            _selection.Clear();
            if (block is not null) _selection.Add(block);
        }
        UpdateSelectionUi(surface);
    }

    /// <summary>Como chamar o item nas mensagens: "Texto"/"o texto"/"este texto", "Imagem"…, "Desenho"….</summary>
    private static (string Name, string The, string This, bool Female) Noun(ContentBlock b) => b.Kind switch
    {
        BlockKind.Text => ("Texto", "o texto", "este texto", false),
        BlockKind.Image => ("Imagem", "a imagem", "esta imagem", true),
        _ => ("Desenho", "o desenho", "este desenho", false),
    };

    private const string SelectedTextHint = "Clique de novo para editar, arraste para mover, Delete apaga. Ctrl+clique junta outros.";
    private const string SelectedImageHint = "Arraste para mover, Delete apaga. Ctrl+clique junta outros.";

    /// <summary>Contornos, botão "Apagar" e a dica de acordo com o que está selecionado.</summary>
    private void UpdateSelectionUi(DrawingSurface surface)
    {
        PaintBlockOutlines();
        ShowBlockActions(surface);
        ToolHintText.Text = _selection.Count switch
        {
            0 => CommentTools["editartudo"].Hint,
            1 => Describe(_selection[0]) + " · " + (_selection[0].Kind == BlockKind.Text ? SelectedTextHint : SelectedImageHint),
            int n => $"{n} itens selecionados · Arraste qualquer um para mover todos juntos (o espaço entre eles não muda); Delete apaga. Ctrl+clique junta ou tira.",
        };
        ToolTipService.SetToolTip(ToolHintText, _selection.Count == 1 ? _selection[0].Text : null);
    }

    /// <summary>Seleciona os blocos da página que ocupam as mesmas áreas de <paramref name="wanted"/> (depois de mover, os blocos são lidos de novo).</summary>
    private void SelectMatching(DrawingSurface surface, List<ContentBlock> blocks, IEnumerable<ContentBlock> wanted)
    {
        _selection.Clear();
        foreach (var w in wanted)
            if (blocks.FirstOrDefault(b => b.Kind == w.Kind && Math.Abs(b.X - w.X) < 2 && Math.Abs(b.Y - w.Y) < 2 && !IsSelected(b)) is { } found)
                _selection.Add(found);
        UpdateSelectionUi(surface);
    }

    // ---------------- Mover (arrastar), selecionar com retângulo e apagar ----------------

    /// <summary>Blocos que podem estar sendo arrastados: onde o mouse apertou e as molduras que acompanham o mouse.</summary>
    private sealed class BlockDrag
    {
        public required PageSlot Slot { get; init; }
        public required List<ContentBlock> Blocks { get; init; }
        public required Point Start { get; init; }
        public List<Shapes.Rectangle> Ghosts { get; } = [];
    }

    /// <summary>Retângulo de seleção (arrastar numa área vazia da página).</summary>
    private sealed class Marquee
    {
        public required PageSlot Slot { get; init; }
        public required Point Start { get; init; }
        public required bool Add { get; init; }
        public Shapes.Rectangle? Box { get; set; }
    }

    private BlockDrag? _blockDrag;
    private Marquee? _marquee;
    private FrameworkElement? _blockActions;

    /// <summary>Arrastando blocos ou o retângulo de seleção.</summary>
    private bool BlockGestureActive => _blockDrag is not null || _marquee is not null;

    /// <summary>
    /// Mouse apertado no modo Editar. Num bloco: espera para ver se é clique ou arrasto (arrasto de um bloco
    /// selecionado leva todos os selecionados); com Ctrl, junta ou tira o bloco da seleção. Fora: retângulo de seleção.
    /// </summary>
    private async Task PressBlockAsync(DrawingSurface surface, PageSlot slot, Point p)
    {
        bool ctrl = CtrlDown();
        if (BlockAt(slot, p) is { } block)
        {
            if (ctrl)
            {
                surface.ReleasePointerCaptures();
                await ClickBlockAsync(surface, slot, p, toggle: true);
                return;
            }
            _blockDrag = new BlockDrag { Slot = slot, Start = p, Blocks = IsSelected(block) ? [.. _selection] : [block] };
            return;
        }
        _marquee = new Marquee { Slot = slot, Start = p, Add = ctrl };
    }

    /// <summary>Quanto os blocos andam (DIPs), sem deixar nenhum sair da página.</summary>
    private static (double Dx, double Dy) DragOffset(DrawingSurface surface, BlockDrag drag, Point p)
    {
        double left = drag.Blocks.Min(b => b.X), top = drag.Blocks.Min(b => b.Y);
        double right = drag.Blocks.Max(b => b.Right), bottom = drag.Blocks.Max(b => b.Bottom);
        double dx = p.X - drag.Start.X, dy = p.Y - drag.Start.Y;
        dx = Math.Clamp(dx, -left * DipsPerPoint, Math.Max(0, surface.ActualWidth - right * DipsPerPoint));
        dy = Math.Clamp(dy, -top * DipsPerPoint, Math.Max(0, surface.ActualHeight - bottom * DipsPerPoint));
        return (dx, dy);
    }

    /// <summary>Mouse movendo com o botão apertado no modo Editar: molduras dos blocos ou retângulo de seleção.</summary>
    private void DragBlock(DrawingSurface surface, Point p)
    {
        if (_marquee is { } marquee)
        {
            if (marquee.Box is null)
            {
                if (Math.Abs(p.X - marquee.Start.X) + Math.Abs(p.Y - marquee.Start.Y) < 5) return;
                marquee.Box = new Shapes.Rectangle
                {
                    Stroke = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                    StrokeThickness = 1,
                    StrokeDashArray = [3, 2],
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(25, 0, 95, 184)),
                    IsHitTestVisible = false,
                };
                surface.Children.Add(marquee.Box);
            }
            var r = MarqueeRect(surface, marquee, p);
            Canvas.SetLeft(marquee.Box, r.X);
            Canvas.SetTop(marquee.Box, r.Y);
            marquee.Box.Width = r.Width;
            marquee.Box.Height = r.Height;
            return;
        }
        if (_blockDrag is not { } drag) return;
        if (drag.Ghosts.Count == 0)
        {
            if (Math.Abs(p.X - drag.Start.X) + Math.Abs(p.Y - drag.Start.Y) < 5) return; // ainda é um clique
            RemoveBlockActions();
            foreach (var block in drag.Blocks)
            {
                var ghost = new Shapes.Rectangle
                {
                    Width = (block.Width + 4) * DipsPerPoint,
                    Height = (block.Height + 4) * DipsPerPoint,
                    Stroke = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"],
                    StrokeThickness = 2,
                    StrokeDashArray = [4, 3],
                    Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(30, 0, 95, 184)),
                    IsHitTestVisible = false,
                };
                surface.Children.Add(ghost);
                drag.Ghosts.Add(ghost);
            }
            surface.SetCursor(InputSystemCursorShape.SizeAll);
            ToolHintText.Text = drag.Blocks.Count > 1 ? "Solte onde os itens devem ficar (Esc cancela)."
                : $"Solte onde {Noun(drag.Blocks[0]).The} deve ficar (Esc cancela).";
        }
        var (dx, dy) = DragOffset(surface, drag, p);
        for (int i = 0; i < drag.Blocks.Count; i++)
        {
            Canvas.SetLeft(drag.Ghosts[i], (drag.Blocks[i].X - 2) * DipsPerPoint + dx);
            Canvas.SetTop(drag.Ghosts[i], (drag.Blocks[i].Y - 2) * DipsPerPoint + dy);
        }
    }

    private static Rect MarqueeRect(DrawingSurface surface, Marquee marquee, Point p)
    {
        double x2 = Math.Clamp(p.X, 0, surface.ActualWidth), y2 = Math.Clamp(p.Y, 0, surface.ActualHeight);
        return new Rect(Math.Min(marquee.Start.X, x2), Math.Min(marquee.Start.Y, y2),
            Math.Abs(x2 - marquee.Start.X), Math.Abs(y2 - marquee.Start.Y));
    }

    private void CancelBlockDrag(DrawingSurface? surface)
    {
        if (_blockDrag is { } drag) foreach (var ghost in drag.Ghosts) surface?.Children.Remove(ghost);
        _blockDrag = null;
    }

    private void CancelMarquee()
    {
        if (_marquee?.Box is { Parent: Panel parent } box) parent.Children.Remove(box);
        _marquee = null;
    }

    /// <summary>Mouse solto: move os blocos arrastados (com desfazer), conclui o retângulo de seleção ou é um clique comum.</summary>
    private async Task DropBlockAsync(DrawingSurface surface, Point p)
    {
        if (_marquee is { } marquee)
        {
            bool drawn = marquee.Box is not null;
            var r = MarqueeRect(surface, marquee, p);
            CancelMarquee();
            if (!marquee.Add) _selection.Clear();
            if (drawn && _blockOutlines.Count > 0)
            {
                // Entra o que o retângulo encosta (em pontos do PDF).
                double x1 = r.X / DipsPerPoint, y1 = r.Y / DipsPerPoint, x2 = r.Right / DipsPerPoint, y2 = r.Bottom / DipsPerPoint;
                foreach (var (block, _) in _blockOutlines)
                    if (block.X < x2 && block.Right > x1 && block.Y < y2 && block.Bottom > y1 && !IsSelected(block))
                        _selection.Add(block);
            }
            UpdateSelectionUi(surface);
            return;
        }
        if (_blockDrag is not { } drag) return;
        bool dragged = drag.Ghosts.Count > 0;
        var (dx, dy) = DragOffset(surface, drag, p);
        CancelBlockDrag(surface);
        double ddx = dx / DipsPerPoint, ddy = dy / DipsPerPoint;
        if (!dragged || (Math.Abs(ddx) < 0.5 && Math.Abs(ddy) < 0.5))
        {
            await ClickBlockAsync(surface, drag.Slot, drag.Start);
            return;
        }
        int page = drag.Slot.Index;
        var blocks = drag.Blocks;
        bool single = blocks.Count == 1;
        var noun = Noun(blocks[0]);
        ToolHintText.Text = single ? $"Movendo {noun.The}…" : $"Movendo {blocks.Count} itens…";
        bool done = await ApplyEditAsync((input, output) => PdfTextEditor.Move(input, page, blocks, ddx, ddy, output),
            label: single ? $"Mover {noun.Name.ToLowerInvariant()}" : "Mover itens");
        if (done)
            ShowStatus(InfoBarSeverity.Success, single ? $"{noun.Name} {(noun.Female ? "movida" : "movido")}" : $"{blocks.Count} itens movidos",
                (single ? "" : "O espaço entre eles não mudou. ") + "A formatação continua a mesma. Use Desfazer (Ctrl+Z) para voltar.");
        ToolHintText.Text = CommentTools["editartudo"].Hint;
        if (_tool?.Mode != DrawMode.EditAll) return;
        var now = await ShowBlockOutlinesAsync(surface, force: true);
        // Continua selecionado o que foi movido (para ajustar de novo, se quiser).
        if (done) SelectMatching(surface, now, blocks.Select(b => b with { X = b.X + ddx, Y = b.Y + ddy }));
    }

    /// <summary>Apaga os textos e imagens selecionados (com desfazer).</summary>
    private async Task DeleteSelectedBlockAsync()
    {
        if (_selection.Count == 0 || _blockSurface is not { } surface || SlotOf(surface) is not { } slot) return;
        if (_blockEditor is not null) return;
        int page = slot.Index;
        var blocks = _selection.ToList();
        bool single = blocks.Count == 1;
        var noun = Noun(blocks[0]);
        TextEditResult? result = null;
        if (await ApplyEditAsync((input, output) => result = PdfTextEditor.Delete(input, page, blocks, output),
                label: single ? $"Apagar {noun.Name.ToLowerInvariant()}" : "Apagar itens"))
            ShowStatus(InfoBarSeverity.Success, single ? $"{noun.Name} {(noun.Female ? "apagada" : "apagado")}" : $"{blocks.Count} itens apagados",
                (result?.Covered == true ? "Parte do texto não pôde ser apagada e foi coberta com branco. " : "") + "Use Desfazer (Ctrl+Z) para voltar.");
        ToolHintText.Text = CommentTools["editartudo"].Hint;
        if (_tool?.Mode == DrawMode.EditAll) await ShowBlockOutlinesAsync(surface, force: true);
    }

    /// <summary>Delete (ou Backspace) apaga o que está selecionado; Ctrl+A seleciona tudo da página; Esc cancela um arrasto.</summary>
    private async void BlockKey_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (_tool?.Mode != DrawMode.EditAll || _blockEditor is not null) return;
        if (e.Key == Windows.System.VirtualKey.Escape && BlockGestureActive)
        {
            e.Handled = true;
            _blockSurface?.ReleasePointerCaptures();
            CancelBlockDrag(_blockSurface);
            CancelMarquee();
            if (_blockSurface is { } s) UpdateSelectionUi(s);
            return;
        }
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or RichEditBox or AutoSuggestBox) return;
        if (e.Key == Windows.System.VirtualKey.A && CtrlDown() && _blockSurface is { } surface && _blockOutlines.Count > 0)
        {
            e.Handled = true;
            _selection.Clear();
            _selection.AddRange(_blockOutlines.Select(o => o.Block));
            UpdateSelectionUi(surface);
            return;
        }
        if (e.Key is not (Windows.System.VirtualKey.Delete or Windows.System.VirtualKey.Back) || _selection.Count == 0) return;
        e.Handled = true;
        await DeleteSelectedBlockAsync();
    }

    /// <summary>Botão "Apagar" ao lado do que está selecionado (do último escolhido).</summary>
    private void ShowBlockActions(DrawingSurface surface)
    {
        RemoveBlockActions();
        if (_selection.Count == 0) return;
        var block = _selection[^1];
        int n = _selection.Count;
        var delete = new Button
        {
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { new FontIcon { Glyph = "\uE74D", FontSize = 14 }, new TextBlock { Text = n > 1 ? $"Apagar ({n})" : "Apagar" } },
            },
            Padding = new Thickness(10, 4, 10, 4),
        };
        ToolTipService.SetToolTip(delete, n > 1 ? $"Apagar os {n} itens selecionados (Delete)"
            : $"Apagar {Noun(block).This} (Delete)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(delete, "DeleteBlockButton");
        delete.Click += async (_, _) => await DeleteSelectedBlockAsync();
        var bar = new Border
        {
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(2),
            Child = delete,
        };
        // Ao lado direito (para não cobrir o que está em cima); sem espaço, logo abaixo.
        const double barWidth = 130;
        double left = (block.Right + 6) * DipsPerPoint, top = (block.Y - 2) * DipsPerPoint;
        if (left + barWidth > surface.ActualWidth)
        {
            left = Math.Max(0, (block.Right + 2) * DipsPerPoint - barWidth);
            top = (block.Bottom + 6) * DipsPerPoint;
        }
        Canvas.SetLeft(bar, left);
        Canvas.SetTop(bar, top);
        surface.Children.Add(bar);
        _blockActions = bar;
    }

    private void RemoveBlockActions()
    {
        if (_blockActions is { Parent: Panel parent }) parent.Children.Remove(_blockActions);
        _blockActions = null;
    }

    // ---------------- Etapa 5.9: editar o texto do bloco ----------------

    private TextBox? _blockEditor;
    private (DrawingSurface Surface, PageSlot Slot, ContentBlock Block, string Font)? _blockEditing;

    private async Task OpenBlockEditorAsync(DrawingSurface surface, PageSlot slot, ContentBlock block)
    {
        string font = await ChooseEditFontAsync(block.FontFamily);
        if (_tool?.Mode != DrawMode.EditAll || _blockEditor is not null) return; // o usuário saiu do modo enquanto isso
        RemoveBlockActions();
        var c = block.Color;
        // Atenção: AcceptsReturn precisa vir ANTES do texto. Uma caixa de uma linha só corta o texto na primeira
        // quebra de linha; ao aplicar, as outras linhas do bloco eram apagadas da página.
        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = EditorFontFamily(font),
            FontSize = Math.Max(4, block.FontSize * DipsPerPoint),
            FontWeight = block.Bold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal,
            FontStyle = block.Italic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)),
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Padding = new Thickness(2, 0, 2, 0),
            MinWidth = (block.Width + 8) * DipsPerPoint,
            MinHeight = 0,
        };
        editor.Text = block.Text; // só depois de AcceptsReturn (veja acima)
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor, "Editar texto do bloco");
        // Mesmo fundo branco ao passar o mouse e com foco (o tema escuro do WinUI escureceria a caixa).
        foreach (var key in new[] { "TextControlBackgroundPointerOver", "TextControlBackgroundFocused" })
            editor.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.White);
        foreach (var key in new[] { "TextControlForegroundPointerOver", "TextControlForegroundFocused" })
            editor.Resources[key] = editor.Foreground;
        // PreviewKeyDown: a caixa com AcceptsReturn "engole" o Enter antes do KeyDown.
        editor.PreviewKeyDown += async (s, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape) { e.Handled = true; await FinishBlockEditorAsync(commit: false); }
            else if (e.Key == Windows.System.VirtualKey.Enter
                && Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down))
            { e.Handled = true; await FinishBlockEditorAsync(commit: true); }
        };
        Canvas.SetLeft(editor, (block.X - 3) * DipsPerPoint);
        Canvas.SetTop(editor, (block.Y - 2) * DipsPerPoint);
        surface.Children.Add(editor);
        _blockEditor = editor;
        _blockEditing = (surface, slot, block, font);
        editor.Focus(FocusState.Programmatic);
        editor.SelectAll();
        ToolHintText.Text = (font.Equals(block.FontFamily, StringComparison.OrdinalIgnoreCase) ? "" : $"Original: {block.FontFamily} → usando: {font} · ")
            + "Ctrl+Enter ou clique fora para aplicar; Esc cancela. Apagar tudo remove o texto.";
    }

    /// <summary>Fecha a caixa de edição; com <paramref name="commit"/>, troca o texto na página (com desfazer).</summary>
    private async Task FinishBlockEditorAsync(bool commit)
    {
        if (_blockEditor is not { } editor || _blockEditing is not { } editing) return;
        _blockEditor = null;
        _blockEditing = null;
        string text = editor.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        editing.Surface.Children.Remove(editor);
        var block = editing.Block;
        if (!commit || text == block.Text)
        {
            ToolHintText.Text = Describe(block) + " · Clique de novo para editar.";
            return;
        }
        int page = editing.Slot.Index;
        TextEditResult? result = null;
        if (!await ApplyEditAsync((input, output) => result = PdfTextEditor.Replace(input, page, block, text, output, editing.Font), label: "Editar texto")) return;
        var notes = new List<string>();
        if (result is { } r && !r.FontUsed.Equals(block.FontFamily, StringComparison.OrdinalIgnoreCase))
            notes.Add($"Original: {block.FontFamily} → usando: {r.FontUsed}.");
        if (result?.Covered == true)
            notes.Add("Parte do texto antigo não pôde ser apagada e foi coberta com branco.");
        notes.Add("Use Desfazer (Ctrl+Z) para voltar.");
        ShowStatus(InfoBarSeverity.Success, text.Trim().Length == 0 ? "Texto removido" : "Texto alterado", string.Join(" ", notes));
        ToolHintText.Text = CommentTools["editartudo"].Hint;
        if (_tool?.Mode == DrawMode.EditAll) await ShowBlockOutlinesAsync(editing.Surface, force: true); // blocos da página nova
    }

    /// <summary>"Texto · Times New Roman 11 pt, negrito · 3 linhas: “Primeira linha…”" ou "Imagem · 5,6 × 2,8 cm".</summary>
    private static string Describe(ContentBlock b)
    {
        static string Cm(double pt) => (pt / 72 * 2.54).ToString("0.0");
        if (b.Kind == BlockKind.Image) return $"Imagem · {Cm(b.Width)} × {Cm(b.Height)} cm";
        if (b.Kind == BlockKind.Shape)
            return b.Width < 1.5 || b.Height < 1.5 ? $"Linha · {Cm(Math.Max(b.Width, b.Height))} cm" : $"Desenho · {Cm(b.Width)} × {Cm(b.Height)} cm";
        var style = new List<string> { $"{b.FontFamily} {b.FontSize:0.#} pt" };
        if (b.Bold) style.Add("negrito");
        if (b.Italic) style.Add("itálico");
        string first = b.Lines[0].Text;
        if (first.Length > 40 || b.Lines.Count > 1) first = first[..Math.Min(40, first.Length)] + "…";
        return $"Texto · {string.Join(", ", style)} · {Plural(b.Lines.Count, "linha", "linhas")}: “{first}”";
    }

    // ---------------- Etapa 5.10: fonte para editar (gêmeas e Google Fonts) ----------------

    private static readonly HttpClient FontHttp = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly HashSet<string> _fontDownloadDeclined = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A fonte para editar o texto. Se o app não tem a original nem uma gêmea, pergunta se pode procurá-la no
    /// Google Fonts (uma vez por fonte nesta sessão) e baixa só com o "sim" do usuário.
    /// </summary>
    private async Task<string> ChooseEditFontAsync(string original)
    {
        var choice = await Task.Run(() => FontLibrary.Resolve(original));
        if (!choice.CanTryDownload || _fontDownloadDeclined.Contains(original)) return choice.Use;

        var dialog = Prepare(new ContentDialog
        {
            Title = "Fonte que não está no computador",
            Content = new TextBlock
            {
                Text = $"O texto usa a fonte “{original}”, que não está neste computador.\n\n" +
                       "O PDF Studio pode procurá-la no Google Fonts (fontes gratuitas, com licença livre) e baixá-la para " +
                       $"{FontLibrary.DownloadedFontsFolder}. Ela não é instalada no Windows; só o PDF Studio a usa.\n\n" +
                       $"Sem baixar, o texto será escrito com “{choice.Use}”.",
                TextWrapping = TextWrapping.Wrap,
            },
            PrimaryButtonText = "Procurar e baixar",
            CloseButtonText = $"Usar {choice.Use}",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            _fontDownloadDeclined.Add(original);
            return choice.Use;
        }
        ToolHintText.Text = $"Procurando “{original}” no Google Fonts…";
        try
        {
            var files = await GoogleFonts.DownloadAsync(original, FontHttp);
            if (files.Count > 0 && await Task.Run(() => FontLibrary.Resolve(original)) is { Source: FontSource.Original } found)
            {
                ShowStatus(InfoBarSeverity.Success, "Fonte baixada", $"“{original}” ({Plural(files.Count, "estilo", "estilos")}) agora pode ser usada pelo PDF Studio.");
                return found.Use;
            }
            ShowStatus(InfoBarSeverity.Informational, "Fonte não encontrada no Google Fonts", $"O texto será escrito com “{choice.Use}”.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException or InvalidDataException)
        {
            ShowStatus(InfoBarSeverity.Warning, "Não foi possível baixar a fonte", $"{ex.Message} O texto será escrito com “{choice.Use}”.");
        }
        _fontDownloadDeclined.Add(original); // não pergunta de novo nesta sessão
        return choice.Use;
    }

    /// <summary>Fonte da caixa de edição: as do app (gêmeas/baixadas) são lidas do arquivo, as do Windows pelo nome.</summary>
    private static FontFamily EditorFontFamily(string family) =>
        FontLibrary.FileOf(family) is { } file ? new FontFamily($"{file}#{family}") : new FontFamily(family);
}
