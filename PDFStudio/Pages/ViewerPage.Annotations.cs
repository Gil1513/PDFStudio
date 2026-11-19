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

// Etapa 4: comentários. Edição numa cópia de trabalho (salvar, desfazer/refazer) e ferramentas de desenho.
public sealed partial class ViewerPage
{
    /// <summary>A única instância (a página fica em cache); usada pela janela para avisar sobre alterações não salvas.</summary>
    public static ViewerPage? Instance { get; private set; }

    private WorkingCopy? _work;

    /// <summary>Arquivo mostrado na tela: a versão em edição, se houver, senão o original.</summary>
    private string? DisplayPath => _work?.CurrentPath ?? _path;

    public bool HasUnsavedChanges => _work?.IsDirty == true;

    // =====================================================================
    // Cópia de trabalho: aplicar, salvar, desfazer/refazer
    // =====================================================================

    /// <summary>
    /// Aplica uma alteração na cópia de trabalho (criada na primeira alteração) e redesenha as páginas.
    /// <paramref name="operation"/> recebe (versão atual, arquivo novo).
    /// </summary>
    private async Task<bool> ApplyEditAsync(Action<string, string> operation)
    {
        if (_path is null) return false;
        bool first = _work is null;
        try
        {
            if (_work is null)
            {
                string original = _path;
                _work = await Task.Run(() => new WorkingCopy(original));
            }
            var work = _work;
            await Task.Run(() => work.Apply(operation));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível alterar o documento", ex.Message);
            return false;
        }

        if (_hideComments) ShowCommentsAgain(); // um comentário novo precisa aparecer
        await RefreshDocumentAsync();
        UpdateEditState();
        if (first && await IsSignedAsync(_path))
            ShowStatus(InfoBarSeverity.Warning, "Este PDF tem assinatura digital",
                "Ao salvar, as alterações invalidam as assinaturas existentes. Para manter o original assinado, use \"Salvar como\".");
        return true;
    }

    /// <summary>Recarrega o arquivo mostrado e redesenha só as páginas e miniaturas que estão na tela.</summary>
    private async Task RefreshDocumentAsync()
    {
        try
        {
            if (await RenderSourceAsync() is not { } path) return;
            var doc = await PdfRenderer.LoadAsync(path);
            _document = doc;
            foreach (var slot in _slots)
            {
                if (slot.Image is not null)
                {
                    var image = await PdfRenderer.RenderAsync(doc, slot.Index, RenderScale);
                    if (doc == _document) slot.Image = image;
                }
                if (slot.Thumbnail is not null)
                {
                    var thumb = await PdfRenderer.RenderAsync(doc, slot.Index, ThumbnailScale(slot));
                    if (doc == _document) slot.Thumbnail = thumb;
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível atualizar a visualização", ex.Message);
        }
    }

    private static double ThumbnailScale(PageSlot slot) => PageSlot.ThumbMaxWidth * 1.5 / Math.Max(slot.Width, slot.Height);

    /// <summary>Atualiza botões Salvar/Desfazer/Refazer e o título (marca "não salvo").</summary>
    private void UpdateEditState()
    {
        UndoButton.IsEnabled = _work?.CanUndo == true;
        RedoButton.IsEnabled = _work?.CanRedo == true;
        QuickSaveButton.IsEnabled = HasUnsavedChanges;
        if (_path is not null) App.MainWindow!.SetSubtitle(FileName!);
        _ = RefreshCommentListAsync();
        _ = RefreshAttachmentListAsync();
    }

    /// <summary>Grava as alterações no arquivo original. Retorna false se não conseguiu.</summary>
    private async Task<bool> SaveAsync()
    {
        if (_work is not { IsDirty: true } work) return true;
        try
        {
            await Task.Run(work.Save);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar",
                $"{ex.Message} Se o arquivo estiver aberto em outro programa, feche-o ou use \"Salvar como\".");
            return false;
        }
        UpdateEditState();
        ShowStatus(InfoBarSeverity.Success, "Alterações salvas", work.OriginalPath);
        return true;
    }

    private async Task SaveAsAsync()
    {
        if (_path is null) return;
        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_path) + " - editado");
        if (output is null) return;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            await SaveAsync();
            return;
        }
        try
        {
            if (_work is { } work) await Task.Run(() => work.SaveAs(output));
            else { string source = _path; await Task.Run(() => File.Copy(source, output, overwrite: true)); }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar", ex.Message);
            return;
        }
        _path = output; // a partir de agora, o arquivo aberto é o novo
        App.Recent.Add(output);
        UpdateEditState();
        ShowStatus(InfoBarSeverity.Success, "Arquivo salvo", output);
    }

    private async Task UndoRedoAsync(bool undo)
    {
        if (_textEditor is not null) { await FinishTextEditorAsync(commit: false); return; }
        CancelDrawing();
        if (undo && _inkStrokes.Count > 0) // primeiro desfaz o desenho livre que ainda não foi gravado
        {
            ClearInk();
            return;
        }
        if (_work is null || (undo ? !_work.CanUndo : !_work.CanRedo)) return;
        if (undo) _work.Undo(); else _work.Redo();
        await RefreshDocumentAsync();
        UpdateEditState();
    }

    /// <summary>Descarta a cópia de trabalho (sem perguntar).</summary>
    public void DiscardWork()
    {
        _work?.Dispose();
        _work = null;
        ShowCommentsAgain();
        DeleteHiddenCopy();
        UpdateEditState();
    }

    /// <summary>
    /// Se houver alterações não salvas, pergunta: Salvar / Não salvar / Cancelar.
    /// Retorna true se pode continuar (salvou ou descartou).
    /// </summary>
    public async Task<bool> ConfirmDiscardAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = App.MainWindow!.Content.XamlRoot,
            RequestedTheme = App.Settings.Theme,
            Title = "Salvar as alterações?",
            Content = $"\"{Path.GetFileName(_path)}\" tem alterações que ainda não foram salvas.",
            PrimaryButtonText = "Salvar",
            SecondaryButtonText = "Não salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() switch
        {
            ContentDialogResult.Primary => await SaveAsync(),
            ContentDialogResult.Secondary => true,
            _ => false,
        };
    }

    /// <summary>Para ferramentas que trabalham direto no arquivo: exige salvar as alterações antes.</summary>
    private async Task<bool> EnsureSavedAsync()
    {
        if (!HasUnsavedChanges) return true;
        var dialog = Prepare(new ContentDialog
        {
            Title = "Salvar antes de continuar?",
            Content = "Esta ferramenta trabalha com o arquivo salvo. Salve as alterações (comentários etc.) para continuar.",
            PrimaryButtonText = "Salvar e continuar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        return await dialog.ShowAsync() == ContentDialogResult.Primary && await SaveAsync();
    }

    private async void Save_Click(object sender, RoutedEventArgs e) => await SaveAsync();
    private async void SaveAs_Click(object sender, RoutedEventArgs e) => await SaveAsAsync();
    private async void Undo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: true);
    private async void Redo_Click(object sender, RoutedEventArgs e) => await UndoRedoAsync(undo: false);

    private async void Accel_Save(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; await SaveAsync(); }

    private async void Accel_Undo(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (IsTyping()) return; // numa caixa de texto, Ctrl+Z desfaz a digitação
        a.Handled = true;
        await UndoRedoAsync(undo: true);
    }

    private async void Accel_Redo(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (IsTyping()) return;
        a.Handled = true;
        await UndoRedoAsync(undo: false);
    }

    private bool IsTyping() => FocusManager.GetFocusedElement(XamlRoot) is TextBox or PasswordBox or NumberBox;

    // =====================================================================
    // Ferramentas de comentário
    // =====================================================================

    /// <summary>Como a ferramenta usa o mouse.</summary>
    private enum DrawMode
    {
        /// <summary>Arrastar uma caixa (retângulo, oval).</summary>
        Box,
        /// <summary>Arrastar do início ao fim (linha, seta).</summary>
        Segment,
        /// <summary>Traço livre; vários traços viram um único desenho ao concluir.</summary>
        Freehand,
        /// <summary>Arrastar (ou clicar) e digitar o texto na própria página.</summary>
        Text,
        /// <summary>Clicar onde fica o ícone da nota e digitar o texto.</summary>
        Note,
        /// <summary>Arrastar sobre o texto da página, como numa seleção (realçar, sublinhar, riscar).</summary>
        TextSelect,
        /// <summary>Clicar onde o carimbo deve ficar (a prévia acompanha o mouse).</summary>
        Stamp,
        /// <summary>Etapa 5.1: clicar e digitar um texto que passa a fazer parte da página (não é comentário).</summary>
        PageText,
        /// <summary>Etapa 5.2: clicar (tamanho automático) ou arrastar (tamanho escolhido) para colocar uma imagem.</summary>
        Image,
        /// <summary>Etapa 5.3: arrastar sobre a área que vira link; clicar num link existente edita ou exclui.</summary>
        Link,
        /// <summary>Etapa 5.8: mostra os blocos da página (parágrafos e imagens); clicar seleciona um.</summary>
        EditAll,
    }

    /// <summary>Uma ferramenta de comentário: chave do botão, nome e o que mostrar na barra de opções.</summary>
    private sealed record CommentTool(string Key, string Name, AnnotationKind Kind, DrawMode Mode, string Hint,
        bool Arrow = false, bool UsesColor = true, bool UsesWidth = true, bool UsesFontSize = false);

    private static readonly Dictionary<string, CommentTool> CommentTools = new[]
    {
        new CommentTool("realcar", "Realçar", AnnotationKind.Highlight, DrawMode.TextSelect,
            "Arraste sobre o texto para realçar.", UsesWidth: false),
        new CommentTool("sublinhar", "Sublinhar", AnnotationKind.Underline, DrawMode.TextSelect,
            "Arraste sobre o texto para sublinhar.", UsesWidth: false),
        new CommentTool("riscar", "Riscar", AnnotationKind.StrikeOut, DrawMode.TextSelect,
            "Arraste sobre o texto para riscar.", UsesWidth: false),
        new CommentTool("retangulo", "Retângulo", AnnotationKind.Rectangle, DrawMode.Box, "Arraste na página para desenhar."),
        new CommentTool("oval", "Oval", AnnotationKind.Oval, DrawMode.Box, "Arraste na página para desenhar."),
        new CommentTool("linha", "Linha", AnnotationKind.Line, DrawMode.Segment, "Arraste do início ao fim da linha. Segure Shift para retas a cada 45°."),
        new CommentTool("seta", "Seta", AnnotationKind.Line, DrawMode.Segment, "Arraste do início até onde a seta aponta. Shift: ângulos de 45°.", Arrow: true),
        new CommentTool("desenho", "Desenho livre", AnnotationKind.Ink, DrawMode.Freehand,
            "Desenhe com o mouse; pode fazer vários traços na mesma página. Clique em Concluir ou troque de página para gravar."),
        new CommentTool("texto", "Caixa de texto", AnnotationKind.TextBox, DrawMode.Text,
            "Clique (ou arraste uma caixa) e digite. Clique fora ou Esc para concluir.", UsesWidth: false, UsesFontSize: true),
        new CommentTool("nota", "Nota", AnnotationKind.Note, DrawMode.Note,
            "Clique onde a nota deve ficar e digite o texto. Clique fora ou Esc para concluir.", UsesWidth: false),
        new CommentTool("carimbo", "Carimbo", AnnotationKind.Stamp, DrawMode.Stamp,
            "Clique na página onde o carimbo deve ficar.", UsesWidth: false),
        new CommentTool("adicionartexto", "Adicionar texto", AnnotationKind.Other, DrawMode.PageText,
            "Clique onde o texto deve começar e digite (Enter = nova linha). Clique fora ou Esc para concluir.",
            UsesWidth: false, UsesFontSize: true),
        new CommentTool("imagem", "Imagem", AnnotationKind.Other, DrawMode.Image,
            "Clique para colocar a imagem ou arraste para escolher o tamanho (a proporção é mantida).",
            UsesColor: false, UsesWidth: false),
        new CommentTool("link", "Link", AnnotationKind.Other, DrawMode.Link,
            "Arraste sobre a área que vai virar link (ex.: um texto). Clique num link existente (contorno azul) para editar ou excluir.",
            UsesColor: false, UsesWidth: false),
        new CommentTool("editartudo", "Editar tudo", AnnotationKind.Other, DrawMode.EditAll,
            "Textos (azul) e imagens (verde) aparecem contornados. Clique num texto para selecionar e clique de novo para editar.",
            UsesColor: false, UsesWidth: false),
    }.ToDictionary(t => t.Key);

    private static readonly (string Name, string Hex)[] Palette =
    [
        ("Vermelho", "#E53935"), ("Laranja", "#FB8C00"), ("Amarelo", "#FDD835"), ("Verde", "#43A047"),
        ("Azul", "#1E88E5"), ("Roxo", "#8E24AA"), ("Preto", "#212121"),
    ];

    private static readonly double[] LineWidths = [1, 2, 3, 5, 8];
    private static readonly double[] FontSizes = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 72];

    /// <summary>Modelos de carimbo (desenho próprio do PDF Studio). O último é o personalizado (texto e cor livres).</summary>
    private static readonly (string Label, string Text, string Hex, string Name, bool AlwaysDetail)[] StampPresets =
    [
        ("Aprovado", "Aprovado", "#2E7D32", "Approved", false),
        ("Rejeitado", "Rejeitado", "#C62828", "NotApproved", false),
        ("Confidencial", "Confidencial", "#C62828", "Confidential", false),
        ("Rascunho", "Rascunho", "#546E7A", "Draft", false),
        ("Recebido (com data)", "Recebido", "#1565C0", "Received", true),
        ("Personalizado…", "", "", "Custom", false),
    ];

    private bool IsCustomStamp => App.Settings.StampPreset == StampPresets.Length - 1;

    private CommentTool? _tool;

    private bool UsesHighlightColor => _tool?.Kind == AnnotationKind.Highlight;
    private bool IsPageTextTool => _tool?.Mode == DrawMode.PageText;

    /// <summary>Cor da ferramenta ativa (o realce e o "Adicionar texto" guardam a sua separada).</summary>
    private string ToolColorHex
    {
        get => UsesHighlightColor ? App.Settings.HighlightColor : IsPageTextTool ? App.Settings.PageTextColor : App.Settings.CommentColor;
        set
        {
            if (UsesHighlightColor) App.Settings.HighlightColor = value;
            else if (IsPageTextTool) App.Settings.PageTextColor = value;
            else App.Settings.CommentColor = value;
            App.Settings.Save();
        }
    }

    /// <summary>Tamanho da letra da ferramenta ativa (caixa de texto ou "Adicionar texto").</summary>
    private double ToolFontSize
    {
        get => IsPageTextTool ? App.Settings.PageTextFontSize : App.Settings.CommentFontSize;
        set
        {
            if (IsPageTextTool) App.Settings.PageTextFontSize = value; else App.Settings.CommentFontSize = value;
            App.Settings.Save();
        }
    }

    private AnnotationColor ToolColor => AnnotationColor.FromHex(ToolColorHex);
    private AnnotationStyle ToolStyle => new(ToolColor, App.Settings.CommentLineWidth);

    /// <summary>Monta a paleta de cores e a lista de espessuras (chamado no construtor).</summary>
    private void InitCommentOptions()
    {
        Instance = this;
        foreach (var (name, hex) in Palette)
        {
            var button = new Button
            {
                Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), Tag = hex,
                Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush(hex) },
            };
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += (s, _) =>
            {
                ToolColorHex = (string)((FrameworkElement)s).Tag;
                ColorSwatch.Background = Brush(ToolColorHex);
                ColorFlyout.Hide();
            };
            ColorPalette.Children.Add(button);
        }
        ColorSwatch.Background = Brush(App.Settings.CommentColor);

        foreach (double w in LineWidths) WidthBox.Items.Add($"{w:0} pt");
        int index = Array.IndexOf(LineWidths, App.Settings.CommentLineWidth);
        WidthBox.SelectedIndex = index >= 0 ? index : 1;

        foreach (var preset in StampPresets) StampBox.Items.Add(preset.Label);
        StampBox.SelectedIndex = Math.Clamp(App.Settings.StampPreset, 0, StampPresets.Length - 1);
        StampTextBox.Text = App.Settings.StampCustomText;
        StampDetailCheck.IsChecked = App.Settings.StampWithDetail;

        foreach (double size in FontSizes) FontSizeBox.Items.Add($"{size:0} pt");
        SelectToolFontSize();
    }

    private bool _settingFontSize;

    /// <summary>Mostra na lista o tamanho guardado da ferramenta ativa.</summary>
    private void SelectToolFontSize()
    {
        int index = Array.IndexOf(FontSizes, ToolFontSize);
        _settingFontSize = true;
        FontSizeBox.SelectedIndex = index >= 0 ? index : Array.IndexOf(FontSizes, 12);
        _settingFontSize = false;
    }

    private static SolidColorBrush Brush(string hex)
    {
        var c = AnnotationColor.FromHex(hex);
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
    }

    private void WidthBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (WidthBox.SelectedIndex < 0) return;
        App.Settings.CommentLineWidth = LineWidths[WidthBox.SelectedIndex];
        App.Settings.Save();
    }

    private void StampBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StampBox is null || StampBox.SelectedIndex < 0) return;
        App.Settings.StampPreset = StampBox.SelectedIndex;
        App.Settings.Save();
        UpdateStampOptions();
    }

    private void StampTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (StampTextBox is null) return;
        App.Settings.StampCustomText = StampTextBox.Text;
        App.Settings.Save();
    }

    private void StampDetailCheck_Click(object sender, RoutedEventArgs e)
    {
        App.Settings.StampWithDetail = StampDetailCheck.IsChecked == true;
        App.Settings.Save();
    }

    /// <summary>No carimbo personalizado aparecem o campo de texto e a cor; nos modelos, a cor é a do modelo.</summary>
    private void UpdateStampOptions()
    {
        if (StampTextBox is null || _tool?.Mode != DrawMode.Stamp) return;
        StampTextBox.Visibility = IsCustomStamp ? Visibility.Visible : Visibility.Collapsed;
        ColorOption.Visibility = IsCustomStamp ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>O carimbo escolhido, com a linha de data e autor quando pedida (ou sempre, no "Recebido").</summary>
    private StampDesign? CurrentStamp()
    {
        var preset = StampPresets[Math.Clamp(App.Settings.StampPreset, 0, StampPresets.Length - 1)];
        string text = IsCustomStamp ? App.Settings.StampCustomText : preset.Text;
        if (string.IsNullOrWhiteSpace(text)) return null;
        var color = IsCustomStamp ? AnnotationColor.FromHex(App.Settings.CommentColor) : AnnotationColor.FromHex(preset.Hex);
        string? detail = null;
        if (preset.AlwaysDetail || App.Settings.StampWithDetail)
        {
            string author = App.Settings.CommentAuthor.Trim();
            string date = DateTime.Now.ToString("dd/MM/yyyy HH:mm");
            detail = author.Length > 0 ? $"{author} · {date}" : date;
        }
        return new StampDesign(text, color, detail, preset.Name);
    }

    private void FontSizeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FontSizeBox is null || FontSizeBox.SelectedIndex < 0 || _settingFontSize) return;
        ToolFontSize = FontSizes[FontSizeBox.SelectedIndex];
    }

    /// <summary>
    /// Liga uma ferramenta (ou desliga, com null). A ferramenta continua ativa até Esc ou "Concluir".
    /// Traços pendentes do desenho livre são gravados (ou descartados, com <paramref name="keepPendingInk"/> = false).
    /// </summary>
    private void SetTool(string? key, bool keepPendingInk = true)
    {
        if (_textEditor is not null) _ = FinishTextEditorAsync(commit: keepPendingInk);
        RemoveStampGhost();
        RemoveLinkOutlines();
        if (_blockEditor is not null) _ = FinishBlockEditorAsync(commit: keepPendingInk);
        RemoveBlockOutlines();
        ToolTipService.SetToolTip(ToolHintText, null);
        CancelImageDrag();
        RemoveImageGhost();
        CancelDrawing();
        if (_inkStrokes.Count > 0)
        {
            if (keepPendingInk) _ = CommitInkAsync();
            else ClearInk();
        }
        _tool = key is not null && CommentTools.TryGetValue(key, out var tool) ? tool : null;
        ToolOptionsBar.Visibility = _tool is null ? Visibility.Collapsed : Visibility.Visible;
        if (_tool is not null)
        {
            ToolNameText.Text = _tool.Name;
            ToolHintText.Text = _tool.Hint;
            ColorOption.Visibility = _tool.UsesColor ? Visibility.Visible : Visibility.Collapsed;
            WidthOption.Visibility = _tool.UsesWidth ? Visibility.Visible : Visibility.Collapsed;
            FontSizeOption.Visibility = _tool.UsesFontSize ? Visibility.Visible : Visibility.Collapsed;
            if (_tool.UsesFontSize) SelectToolFontSize();
            PageFontOption.Visibility = IsPageTextTool ? Visibility.Visible : Visibility.Collapsed;
            if (IsPageTextTool) InitPageFontOptions();
            ColorSwatch.Background = Brush(ToolColorHex);
            StampOption.Visibility = _tool.Mode == DrawMode.Stamp ? Visibility.Visible : Visibility.Collapsed;
            ImageOption.Visibility = _tool.Mode == DrawMode.Image ? Visibility.Visible : Visibility.Collapsed;
            UpdateStampOptions();
            // Seleção de texto: já lê as palavras das páginas na tela, para a primeira seleção ser imediata.
            if (_tool.Mode == DrawMode.TextSelect && DisplayPath is not null)
                foreach (var slot in _slots.Where(s => s.Image is not null)) _ = WordsOfPage(slot.Index);
        }
        BuildToolbar(SelectedCategory);
    }

    private void FinishTool_Click(object sender, RoutedEventArgs e) => SetTool(null);

    // ---------------- Desenho com o mouse ----------------

    private DrawingSurface? _drawSurface;
    private PageSlot? _drawSlot;
    private Point _drawStart;
    private Shapes.Shape? _preview;

    // Desenho livre: traços já feitos (ainda não gravados) e os desenhos provisórios na tela.
    private readonly List<List<Point>> _inkStrokes = [];
    private readonly List<Shapes.Polyline> _inkPreviews = [];
    private DrawingSurface? _inkSurface;
    private PageSlot? _inkSlot;

    /// <summary>O visualizador mede em DIPs (1/96 pol.); o PDF, em pontos (1/72 pol.).</summary>
    private const double DipsPerPoint = 96.0 / 72;

    private void Surface_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var surface = (DrawingSurface)sender;
        _overLink = false;
        surface.SetCursor(_tool is null ? null
            : _tool.Mode is DrawMode.TextSelect or DrawMode.PageText ? InputSystemCursorShape.IBeam : InputSystemCursorShape.Cross);
        if (_tool?.Mode == DrawMode.Link) _ = ShowLinkOutlinesAsync(surface);
        else if (_tool?.Mode == DrawMode.EditAll) { surface.SetCursor(null); _ = ShowBlockOutlinesAsync(surface); }
        // Já lê os links da página, para a "mãozinha" aparecer sem atraso.
        else if (_tool is null && DisplayPath is not null && SlotOf(surface) is { } slot) _ = LinksOfPage(slot.Index);
    }

    private SolidColorBrush ToolBrush()
    {
        var c = ToolColor;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
    }

    private async void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not DrawingSurface surface || SlotOf(surface) is not { } slot) return;
        var point = e.GetCurrentPoint(surface);
        if (!point.Properties.IsLeftButtonPressed) return;
        if (_tool is null)
        {
            // Sem ferramenta: clicar num link segue o link.
            if (LinkAt(slot, point.Position) is not null) { e.Handled = true; await FollowLinkAsync(slot, point.Position); }
            return;
        }
        e.Handled = true;

        // Com um texto sendo digitado, clicar fora dele só conclui o texto.
        if (_textEditor is not null)
        {
            await FinishTextEditorAsync(commit: true);
            return;
        }
        if (_tool.Mode == DrawMode.TextSelect)
        {
            await StartTextSelectionAsync(surface, slot, e);
            return;
        }
        if (_tool.Mode == DrawMode.Image)
        {
            CancelDrawing();
            StartImageDrag(surface, slot, e);
            return;
        }
        if (_tool.Mode == DrawMode.EditAll)
        {
            if (_blockEditor is not null) { await FinishBlockEditorAsync(commit: true); return; } // clicar fora aplica
            await BlocksOfPage(slot.Index); // a leitura pode ter começado agora há pouco (mouse chegando)
            await ShowBlockOutlinesAsync(surface);
            await ClickBlockAsync(surface, slot, point.Position);
            return;
        }
        if (_tool.Mode == DrawMode.Link && LinkAt(slot, point.Position) is { } existing)
        {
            CancelDrawing();
            await EditLinkAsync(surface, existing);
            return;
        }
        if (_tool.Mode == DrawMode.Stamp)
        {
            CancelDrawing();
            await PlaceStampAsync(slot, point.Position);
            return;
        }
        if (_tool.Mode == DrawMode.PageText)
        {
            CancelDrawing();
            OpenTextEditor(surface, slot, point.Position.X, point.Position.Y, 0, 0);
            return;
        }
        if (_tool.Mode == DrawMode.Note)
        {
            CancelDrawing();
            var p = point.Position;
            OpenTextEditor(surface, slot, p.X, p.Y, PdfAnnotations.NoteIconSize * DipsPerPoint, PdfAnnotations.NoteIconSize * DipsPerPoint);
            return;
        }

        // Desenho livre em outra página: grava o desenho da página anterior antes de começar.
        if (_tool.Mode == DrawMode.Freehand && _inkSlot is not null && _inkSlot != slot) await CommitInkAsync();

        CancelDrawing();
        surface.CapturePointer(e.Pointer);
        _drawSurface = surface;
        _drawSlot = slot;
        _drawStart = Clamp(point.Position);
        double thickness = App.Settings.CommentLineWidth * DipsPerPoint;

        switch (_tool.Mode)
        {
            case DrawMode.Box:
                _preview = _tool.Kind == AnnotationKind.Oval ? new Shapes.Ellipse() : new Shapes.Rectangle();
                PlaceBox(_drawStart);
                break;
            case DrawMode.Text or DrawMode.Link:
                _preview = new Shapes.Rectangle { StrokeDashArray = [4, 3] };
                thickness = 1;
                PlaceBox(_drawStart);
                break;
            case DrawMode.Segment:
                _preview = new Shapes.Line
                {
                    X1 = _drawStart.X, Y1 = _drawStart.Y, X2 = _drawStart.X, Y2 = _drawStart.Y,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                break;
            case DrawMode.Freehand:
                _inkSurface = surface;
                _inkSlot = slot;
                _inkStrokes.Add([_drawStart]);
                var line = new Shapes.Polyline
                {
                    StrokeLineJoin = PenLineJoin.Round, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                };
                line.Points.Add(_drawStart);
                _inkPreviews.Add(line);
                _preview = line;
                break;
        }
        _preview!.Stroke = _tool.Mode == DrawMode.Link
            ? (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"] : ToolBrush();
        _preview.StrokeThickness = thickness;
        surface.Children.Add(_preview);
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_tool?.Mode == DrawMode.Image && sender is DrawingSurface imageSurface)
        {
            MoveImageGhost(imageSurface, e);
            return;
        }
        if (_tool?.Mode == DrawMode.EditAll && sender is DrawingSurface blockSurface)
        {
            HoverBlock(blockSurface, e.GetCurrentPoint(blockSurface).Position);
            return;
        }
        if (_tool is null && sender is DrawingSurface plain)
        {
            UpdateLinkCursor(plain, e.GetCurrentPoint(plain).Position);
            return;
        }
        if (_tool?.Mode == DrawMode.Stamp && sender is DrawingSurface hovered)
        {
            MoveStampGhost(hovered, e.GetCurrentPoint(hovered).Position);
            return;
        }
        if (_tool?.Mode == DrawMode.TextSelect && _selLayer is not null && ReferenceEquals(sender, _drawSurface))
        {
            e.Handled = true;
            ShowSelection(Clamp(e.GetCurrentPoint(_drawSurface).Position));
            return;
        }
        if (_preview is null || _tool is null || !ReferenceEquals(sender, _drawSurface)) return;
        e.Handled = true;
        var current = Clamp(e.GetCurrentPoint(_drawSurface).Position);
        switch (_tool.Mode)
        {
            case DrawMode.Box or DrawMode.Text or DrawMode.Link:
                PlaceBox(current);
                break;
            case DrawMode.Segment when _preview is Shapes.Line line:
                var end = SnapAngle(current, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
                line.X2 = end.X;
                line.Y2 = end.Y;
                break;
            case DrawMode.Freehand when _preview is Shapes.Polyline poly:
                var last = poly.Points[^1];
                if (Math.Abs(current.X - last.X) + Math.Abs(current.Y - last.Y) < 1.5) break; // ignora tremidas mínimas
                poly.Points.Add(current);
                _inkStrokes[^1].Add(current);
                break;
        }
    }

    private async void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_tool?.Mode == DrawMode.Image && sender is DrawingSurface imageSurface)
        {
            await FinishImageDragAsync(imageSurface, e);
            return;
        }
        if (_tool?.Mode == DrawMode.TextSelect && _selLayer is not null && ReferenceEquals(sender, _drawSurface))
        {
            e.Handled = true;
            await FinishTextSelectionAsync(Clamp(e.GetCurrentPoint(_drawSurface).Position));
            return;
        }
        if (_preview is null || _tool is null || _drawSurface is null || _drawSlot is null || !ReferenceEquals(sender, _drawSurface)) return;
        e.Handled = true;
        var tool = _tool;
        int page = _drawSlot.Index;
        var current = Clamp(e.GetCurrentPoint(_drawSurface).Position);
        var style = ToolStyle;
        string author = App.Settings.CommentAuthor;

        if (tool.Mode == DrawMode.Freehand)
        {
            // O traço fica na tela; todos os traços viram um só comentário ao concluir.
            _drawSurface.ReleasePointerCaptures();
            _preview = null;
            _drawSurface = null;
            _drawSlot = null;
            ToolHintText.Text = "Clique em Concluir (ou Esc) para gravar o desenho.";
            return;
        }

        if (tool.Mode == DrawMode.Text)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            var surface = _drawSurface;
            var slot = _drawSlot;
            CancelDrawing();
            if (w < 30 || h < 10) (x, y, w, h) = (_drawStart.X, _drawStart.Y, 0, 0); // só um clique: tamanho padrão
            OpenTextEditor(surface, slot, x, y, w, h);
            return;
        }

        if (tool.Mode == DrawMode.Link)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            var surface = _drawSurface;
            var slot = _drawSlot;
            CancelDrawing();
            if (w < 6 || h < 6) return; // clique sem arrastar (fora de um link)
            await CreateLinkAsync(surface, slot, new Rect(x, y, w, h));
            return;
        }

        if (tool.Mode == DrawMode.Box)
        {
            var (x, y, w, h) = BoxBetween(_drawStart, current);
            CancelDrawing();
            if (w < 4 || h < 4) return; // clique sem arrastar
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddShape(input, page, tool.Kind, x / DipsPerPoint, y / DipsPerPoint, w / DipsPerPoint, h / DipsPerPoint,
                    style, author, output));
        }
        else
        {
            var start = _drawStart;
            var end = SnapAngle(current, e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift));
            CancelDrawing();
            if (Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) < 6) return;
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddLine(input, page, start.X / DipsPerPoint, start.Y / DipsPerPoint, end.X / DipsPerPoint, end.Y / DipsPerPoint,
                    tool.Arrow, style, author, output));
        }
    }

    private void Surface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _imageDragSurface)) { CancelImageDrag(); return; }
        // No desenho livre o traço já está guardado; nas outras ferramentas, perder o mouse cancela.
        // Na seleção de texto, soltar o botão também solta o mouse, mas a seleção termina em PointerReleased
        // (que pode ainda estar esperando a leitura das palavras).
        if (ReferenceEquals(sender, _drawSurface) && _tool?.Mode is not (DrawMode.Freehand or DrawMode.TextSelect)) CancelDrawing();
    }

    // ---------------- Caixa de texto e nota: digitar na página ----------------

    private TextBox? _textEditor;
    private DrawingSurface? _textSurface;
    private PageSlot? _textSlot;
    private Rect _textBox; // caixa de texto: posição e tamanho (DIPs); nota: posição e tamanho do ícone
    private DrawMode _textMode;

    /// <summary>Largura padrão (pontos) da caixa de texto criada com um clique e da área para digitar a nota.</summary>
    private const double DefaultTextWidth = 200, NoteEditorWidth = 220;

    /// <summary>Mostra uma caixa para digitar na página, na posição (x, y) em DIPs.</summary>
    private void OpenTextEditor(DrawingSurface surface, PageSlot slot, double x, double y, double width, double height)
    {
        _textMode = _tool?.Mode ?? DrawMode.Text;
        bool note = _textMode == DrawMode.Note;
        double fontSize = App.Settings.CommentFontSize;
        if (_textMode == DrawMode.Text && width <= 0)
        {
            width = Math.Min(DefaultTextWidth * DipsPerPoint, surface.ActualWidth - x);
            height = PdfAnnotations.TextHeight(1, fontSize) * DipsPerPoint;
        }
        _textBox = new Rect(x, y, width, height);
        _textSurface = surface;
        _textSlot = slot;

        var editor = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MinWidth = 0,
            MinHeight = 0,
            CornerRadius = new CornerRadius(0),
            RequestedTheme = ElementTheme.Light, // está sobre a página branca
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(editor,
            note ? "Texto da nota" : _textMode == DrawMode.PageText ? "Texto a escrever na página" : "Texto da caixa");
        if (_textMode == DrawMode.PageText)
        {
            // Texto da página: cresce conforme se digita, com a fonte, o tamanho, a cor e o estilo escolhidos.
            // O canto do texto fica no ponto clicado (a margem interna da caixa é descontada ao gravar).
            double pad = PageTextPadding * DipsPerPoint;
            editor.TextWrapping = TextWrapping.NoWrap;
            editor.MinWidth = 60;
            editor.FontFamily = new FontFamily(App.Settings.PageTextFont);
            editor.FontSize = App.Settings.PageTextFontSize * DipsPerPoint;
            editor.FontWeight = App.Settings.PageTextBold ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            editor.FontStyle = App.Settings.PageTextItalic ? Windows.UI.Text.FontStyle.Italic : Windows.UI.Text.FontStyle.Normal;
            editor.Foreground = ToolBrush();
            editor.Padding = new Thickness(pad - 1);
            editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(160, 255, 255, 255));
            editor.PlaceholderText = "Digite aqui";
            _textBox = new Rect(x - pad, y - pad, 0, 0);
            Canvas.SetLeft(editor, x - pad);
            Canvas.SetTop(editor, y - pad);
        }
        else if (note)
        {
            // A nota: área de digitação ao lado do ponto clicado, com fundo de "post-it".
            editor.PlaceholderText = "Texto da nota";
            editor.Width = Math.Min(NoteEditorWidth * DipsPerPoint, Math.Max(120, surface.ActualWidth - 8));
            editor.MinHeight = 80;
            editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 249, 196));
            Canvas.SetLeft(editor, Math.Max(0, Math.Min(x + 4, surface.ActualWidth - editor.Width)));
            Canvas.SetTop(editor, Math.Max(0, y + 4));
            var icon = new FontIcon { Glyph = "\uE70B", FontSize = 18, Foreground = ToolBrush(), Tag = "note-icon" };
            Canvas.SetLeft(icon, x);
            Canvas.SetTop(icon, y);
            surface.Children.Add(icon);
        }
        else
        {
            // A caixa de texto: mesma posição, largura, letra e cor que terá no PDF.
            editor.Width = width;
            editor.MinHeight = height;
            editor.FontFamily = new FontFamily("Arial");
            editor.FontSize = fontSize * DipsPerPoint;
            editor.Foreground = ToolBrush();
            editor.Padding = new Thickness(PdfAnnotations.TextPadding * DipsPerPoint - 1);
            editor.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(200, 255, 255, 255));
            Canvas.SetLeft(editor, x);
            Canvas.SetTop(editor, y);
        }
        editor.LostFocus += async (_, _) => { if (ReferenceEquals(editor, _textEditor)) await FinishTextEditorAsync(commit: true); };
        _textEditor = editor;
        surface.Children.Add(editor);
        ToolHintText.Text = "Digite o texto. Clique fora (ou Esc) para concluir.";
        editor.Loaded += (_, _) => editor.Focus(FocusState.Programmatic);
    }

    /// <summary>Fecha a caixa de digitação; com <paramref name="commit"/>, grava o texto (se houver) como comentário.</summary>
    private async Task FinishTextEditorAsync(bool commit)
    {
        if (_textEditor is not { } editor || _textSurface is not { } surface || _textSlot is not { } slot) return;
        _textEditor = null; // evita gravar duas vezes (perda de foco + clique)
        string text = editor.Text;
        bool note = _textMode == DrawMode.Note;
        var box = _textBox;
        surface.Children.Remove(editor);
        foreach (var icon in surface.Children.OfType<FontIcon>().Where(i => (i.Tag as string) == "note-icon").ToList())
            surface.Children.Remove(icon);
        _textSurface = null;
        _textSlot = null;
        if (_tool is not null) ToolHintText.Text = _tool.Hint;
        if (!commit || string.IsNullOrWhiteSpace(text)) return;

        int page = slot.Index;
        string author = App.Settings.CommentAuthor;
        var color = ToolColor;
        if (_textMode == DrawMode.PageText)
        {
            var style = CurrentPageTextStyle();
            double pad = PageTextPadding;
            await ApplyEditAsync((input, output) =>
                PdfPageContent.AddText(input, page, box.X / DipsPerPoint + pad, box.Y / DipsPerPoint + pad, text, style, output));
        }
        else if (note)
        {
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddNote(input, page, box.X / DipsPerPoint, box.Y / DipsPerPoint, text, color, author, output));
        }
        else
        {
            double fontSize = App.Settings.CommentFontSize;
            var style = new AnnotationStyle(color, LineWidth: 0);
            await ApplyEditAsync((input, output) =>
                PdfAnnotations.AddTextBox(input, page, box.X / DipsPerPoint, box.Y / DipsPerPoint, box.Width / DipsPerPoint,
                    box.Height / DipsPerPoint, text, fontSize, style, author, output));
        }
    }

    // ---------------- Carimbo ----------------

    private Border? _stampGhost;
    private DrawingSurface? _stampGhostSurface;

    private void Surface_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _stampGhostSurface)) RemoveStampGhost();
        if (ReferenceEquals(sender, _imageGhostSurface) && _imageDragSurface is null) RemoveImageGhost();
    }

    /// <summary>Mostra (ou move) a prévia do carimbo centralizada no mouse.</summary>
    private void MoveStampGhost(DrawingSurface surface, Point p)
    {
        if (CurrentStamp() is not { } design) { RemoveStampGhost(); return; }
        if (!ReferenceEquals(surface, _stampGhostSurface)) RemoveStampGhost();
        var (w, h) = PdfAnnotations.StampSize(design);
        var color = Windows.UI.Color.FromArgb(255, design.Color.R, design.Color.G, design.Color.B);
        if (_stampGhost is null)
        {
            _stampGhost = new Border
            {
                BorderThickness = new Thickness(2.2 * DipsPerPoint),
                CornerRadius = new CornerRadius(6 * DipsPerPoint),
                Opacity = 0.55,
                IsHitTestVisible = false,
                Child = new TextBlock
                {
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    FontFamily = new FontFamily("Arial"),
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    FontSize = 18 * DipsPerPoint,
                },
            };
            _stampGhostSurface = surface;
            surface.Children.Add(_stampGhost);
        }
        _stampGhost.Width = w * DipsPerPoint;
        _stampGhost.Height = h * DipsPerPoint;
        _stampGhost.BorderBrush = new SolidColorBrush(color);
        var label = (TextBlock)_stampGhost.Child;
        label.Text = design.Text.Trim().ToUpper(System.Globalization.CultureInfo.GetCultureInfo("pt-BR"));
        label.Foreground = new SolidColorBrush(color);
        Canvas.SetLeft(_stampGhost, p.X - _stampGhost.Width / 2);
        Canvas.SetTop(_stampGhost, p.Y - _stampGhost.Height / 2);
    }

    private void RemoveStampGhost()
    {
        if (_stampGhost is not null) _stampGhostSurface?.Children.Remove(_stampGhost);
        _stampGhost = null;
        _stampGhostSurface = null;
    }

    private async Task PlaceStampAsync(PageSlot slot, Point p)
    {
        if (CurrentStamp() is not { } design)
        {
            ShowStatus(InfoBarSeverity.Informational, "Digite o texto do carimbo", "Escreva o texto no campo ao lado de \"Modelo\" e clique na página.");
            StampTextBox.Focus(FocusState.Programmatic);
            return;
        }
        RemoveStampGhost();
        int page = slot.Index;
        string author = App.Settings.CommentAuthor;
        await ApplyEditAsync((input, output) =>
            PdfAnnotations.AddStamp(input, page, p.X / DipsPerPoint, p.Y / DipsPerPoint, design, author, output));
    }

    // ---------------- Realçar / sublinhar / riscar: selecionar o texto ----------------

    private Canvas? _selLayer;                     // retângulos da seleção, mostrados enquanto arrasta
    private Task<List<TextWord>>? _selWordsTask;
    private List<TextWord>? _selWords;
    private List<TextWord> _selected = [];

    // Palavras de cada página, lidas uma vez por versão do arquivo.
    private readonly Dictionary<int, Task<List<TextWord>>> _wordCache = [];
    private string? _wordCachePath;

    private Task<List<TextWord>> WordsOfPage(int page)
    {
        string path = DisplayPath!;
        if (_wordCachePath != path) { _wordCache.Clear(); _wordCachePath = path; }
        if (!_wordCache.TryGetValue(page, out var task))
            _wordCache[page] = task = Task.Run(() => PdfTextLayout.Words(path, page));
        return task;
    }

    private async Task StartTextSelectionAsync(DrawingSurface surface, PageSlot slot, PointerRoutedEventArgs e)
    {
        CancelDrawing();
        surface.CapturePointer(e.Pointer);
        _drawSurface = surface;
        _drawSlot = slot;
        _drawStart = Clamp(e.GetCurrentPoint(surface).Position);
        _selected = [];
        var layer = new Canvas { IsHitTestVisible = false };
        _selLayer = layer;
        surface.Children.Add(layer);

        var task = WordsOfPage(slot.Index);
        _selWordsTask = task;
        var words = await task;
        if (!ReferenceEquals(layer, _selLayer)) return; // já soltou ou cancelou
        _selWords = words;
        if (words.Count == 0)
        {
            CancelDrawing();
            ShowStatus(InfoBarSeverity.Informational, "Esta página não tem texto para selecionar",
                "Ela provavelmente é uma imagem (documento escaneado). Use o retângulo ou o desenho livre para marcar.");
        }
    }

    /// <summary>Atualiza as palavras selecionadas entre o início do arraste e <paramref name="current"/> e as mostra.</summary>
    private void ShowSelection(Point current)
    {
        if (_selLayer is not { } layer || _selWords is not { Count: > 0 } words) return;
        var a = new Point(_drawStart.X / DipsPerPoint, _drawStart.Y / DipsPerPoint);
        var b = new Point(current.X / DipsPerPoint, current.Y / DipsPerPoint);
        // Começou longe de qualquer texto e quase não arrastou: não seleciona nada.
        bool nearText = words.Min(w => w.DistanceTo(a.X, a.Y)) < 12 || words.Min(w => w.DistanceTo(b.X, b.Y)) < 12;
        _selected = nearText ? PdfTextLayout.Select(words, a.X, a.Y, b.X, b.Y) : [];

        layer.Children.Clear();
        var c = ToolColor;
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(90, c.R, c.G, c.B));
        foreach (var (x, y, w, h) in PdfTextLayout.LineRects(_selected))
        {
            var rect = new Shapes.Rectangle { Width = w * DipsPerPoint, Height = h * DipsPerPoint, Fill = fill };
            Canvas.SetLeft(rect, x * DipsPerPoint);
            Canvas.SetTop(rect, y * DipsPerPoint);
            layer.Children.Add(rect);
        }
    }

    private async Task FinishTextSelectionAsync(Point current)
    {
        if (_tool is not { } tool || _drawSlot is not { } slot || _selWordsTask is not { } task) { CancelDrawing(); return; }
        var layer = _selLayer;
        _selWords ??= await task;
        if (!ReferenceEquals(layer, _selLayer)) return;
        ShowSelection(current);
        var selected = _selected;
        CancelDrawing();
        if (selected.Count == 0) return;

        var lines = PdfTextLayout.LineRects(selected);
        string text = PdfTextLayout.TextOf(selected);
        int page = slot.Index;
        var style = new AnnotationStyle(ToolColor, LineWidth: 1);
        string author = App.Settings.CommentAuthor;
        await ApplyEditAsync((input, output) => PdfAnnotations.AddTextMarkup(input, page, tool.Kind, lines, style, author, output, text));
    }

    /// <summary>Grava como um único comentário todos os traços do desenho livre feitos até agora.</summary>
    private async Task CommitInkAsync()
    {
        if (_inkSlot is null || _inkStrokes.Count == 0) { ClearInk(); return; }
        int page = _inkSlot.Index;
        var strokes = _inkStrokes.Where(s => s.Count >= 2)
            .Select(s => (IReadOnlyList<(double X, double Y)>)s.Select(p => (p.X / DipsPerPoint, p.Y / DipsPerPoint)).ToList())
            .ToList();
        var style = ToolStyle;
        string author = App.Settings.CommentAuthor;
        ClearInk();
        if (strokes.Count == 0) return;
        await ApplyEditAsync((input, output) => PdfAnnotations.AddInk(input, page, strokes, style, author, output));
    }

    private void ClearInk()
    {
        if (_inkSurface is not null) foreach (var p in _inkPreviews) _inkSurface.Children.Remove(p);
        _inkPreviews.Clear();
        _inkStrokes.Clear();
        _inkSurface = null;
        _inkSlot = null;
    }

    /// <summary>
    /// Página à qual um elemento do modelo pertence. O ItemsRepeater com x:Bind não preenche o DataContext,
    /// então sobe na árvore visual até o filho direto do repetidor e pergunta o índice dele.
    /// </summary>
    private PageSlot? SlotOf(DependencyObject element)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement ui && VisualTreeHelper.GetParent(current) == PagesRepeater)
            {
                int index = PagesRepeater.GetElementIndex(ui);
                return index >= 0 && index < _slots.Count ? _slots[index] : null;
            }
        }
        return null;
    }

    /// <summary>Mantém o ponto dentro da página.</summary>
    private Point Clamp(Point p) => _drawSurface is null ? p
        : new(Math.Clamp(p.X, 0, _drawSurface.ActualWidth), Math.Clamp(p.Y, 0, _drawSurface.ActualHeight));

    /// <summary>Com Shift, prende a linha em múltiplos de 45° (horizontal, vertical, diagonal).</summary>
    private Point SnapAngle(Point end, bool snap)
    {
        if (!snap) return end;
        double dx = end.X - _drawStart.X, dy = end.Y - _drawStart.Y, length = Math.Sqrt(dx * dx + dy * dy);
        double angle = Math.Round(Math.Atan2(dy, dx) / (Math.PI / 4)) * (Math.PI / 4);
        return Clamp(new Point(_drawStart.X + length * Math.Cos(angle), _drawStart.Y + length * Math.Sin(angle)));
    }

    private static (double X, double Y, double W, double H) BoxBetween(Point a, Point b) =>
        (Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(b.X - a.X), Math.Abs(b.Y - a.Y));

    private void PlaceBox(Point current)
    {
        var (x, y, w, h) = BoxBetween(_drawStart, current);
        Canvas.SetLeft(_preview, x);
        Canvas.SetTop(_preview, y);
        _preview!.Width = w;
        _preview.Height = h;
    }

    /// <summary>Remove o desenho provisório (exceto os traços do desenho livre) e solta o mouse.</summary>
    private void CancelDrawing()
    {
        if (_selLayer is not null)
        {
            _drawSurface?.Children.Remove(_selLayer);
            _selLayer = null;
            _selWords = null;
            _selWordsTask = null;
        }
        if (_drawSurface is not null)
        {
            if (_preview is Shapes.Polyline poly && _inkPreviews.Contains(poly)) { }
            else if (_preview is not null) _drawSurface.Children.Remove(_preview);
            _drawSurface.ReleasePointerCaptures();
        }
        _preview = null;
        _drawSurface = null;
        _drawSlot = null;
    }
}
