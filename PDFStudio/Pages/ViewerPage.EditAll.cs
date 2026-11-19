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

    /// <summary>Clique no modo "Editar tudo": seleciona o bloco; clicar de novo num texto já selecionado abre a edição.</summary>
    private async Task ClickBlockAsync(DrawingSurface surface, PageSlot slot, Point p)
    {
        var block = BlockAt(slot, p);
        if (block is { Kind: BlockKind.Text } && ReferenceEquals(block, _selectedBlock))
        {
            await OpenBlockEditorAsync(surface, slot, block);
            return;
        }
        _selectedBlock = block;
        PaintBlockOutlines();
        ToolHintText.Text = block is null ? CommentTools["editartudo"].Hint
            : Describe(block) + (block.Kind == BlockKind.Text ? " · Clique de novo para editar." : "");
        ToolTipService.SetToolTip(ToolHintText, block?.Text);
    }

    // ---------------- Etapa 5.9: editar o texto do bloco ----------------

    private TextBox? _blockEditor;
    private (DrawingSurface Surface, PageSlot Slot, ContentBlock Block, string Font)? _blockEditing;

    private async Task OpenBlockEditorAsync(DrawingSurface surface, PageSlot slot, ContentBlock block)
    {
        string font = await ChooseEditFontAsync(block.FontFamily);
        if (_tool?.Mode != DrawMode.EditAll || _blockEditor is not null) return; // o usuário saiu do modo enquanto isso
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
        if (!await ApplyEditAsync((input, output) => result = PdfTextEditor.Replace(input, page, block, text, output, editing.Font))) return;
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
