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
        if (DisplayPath is not { } path) return;
        try
        {
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
    }

    /// <summary>Uma ferramenta de comentário: chave do botão, nome e o que mostrar na barra de opções.</summary>
    private sealed record CommentTool(string Key, string Name, AnnotationKind Kind, DrawMode Mode, string Hint,
        bool Arrow = false, bool UsesColor = true, bool UsesWidth = true);

    private static readonly Dictionary<string, CommentTool> CommentTools = new[]
    {
        new CommentTool("retangulo", "Retângulo", AnnotationKind.Rectangle, DrawMode.Box, "Arraste na página para desenhar."),
        new CommentTool("oval", "Oval", AnnotationKind.Oval, DrawMode.Box, "Arraste na página para desenhar."),
        new CommentTool("linha", "Linha", AnnotationKind.Line, DrawMode.Segment, "Arraste do início ao fim da linha. Segure Shift para retas a cada 45°."),
        new CommentTool("seta", "Seta", AnnotationKind.Line, DrawMode.Segment, "Arraste do início até onde a seta aponta. Shift: ângulos de 45°.", Arrow: true),
        new CommentTool("desenho", "Desenho livre", AnnotationKind.Ink, DrawMode.Freehand,
            "Desenhe com o mouse; pode fazer vários traços na mesma página. Clique em Concluir ou troque de página para gravar."),
    }.ToDictionary(t => t.Key);

    private static readonly (string Name, string Hex)[] Palette =
    [
        ("Vermelho", "#E53935"), ("Laranja", "#FB8C00"), ("Amarelo", "#FDD835"), ("Verde", "#43A047"),
        ("Azul", "#1E88E5"), ("Roxo", "#8E24AA"), ("Preto", "#212121"),
    ];

    private static readonly double[] LineWidths = [1, 2, 3, 5, 8];

    private CommentTool? _tool;

    private AnnotationColor ToolColor => AnnotationColor.FromHex(App.Settings.CommentColor);
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
                App.Settings.CommentColor = (string)((FrameworkElement)s).Tag;
                App.Settings.Save();
                ColorSwatch.Background = Brush(App.Settings.CommentColor);
                ColorFlyout.Hide();
            };
            ColorPalette.Children.Add(button);
        }
        ColorSwatch.Background = Brush(App.Settings.CommentColor);

        foreach (double w in LineWidths) WidthBox.Items.Add($"{w:0} pt");
        int index = Array.IndexOf(LineWidths, App.Settings.CommentLineWidth);
        WidthBox.SelectedIndex = index >= 0 ? index : 1;
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

    /// <summary>
    /// Liga uma ferramenta (ou desliga, com null). A ferramenta continua ativa até Esc ou "Concluir".
    /// Traços pendentes do desenho livre são gravados (ou descartados, com <paramref name="keepPendingInk"/> = false).
    /// </summary>
    private void SetTool(string? key, bool keepPendingInk = true)
    {
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

    private void Surface_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ((DrawingSurface)sender).SetCursor(_tool is null ? null : InputSystemCursorShape.Cross);

    private SolidColorBrush ToolBrush()
    {
        var c = ToolColor;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, c.R, c.G, c.B));
    }

    private async void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tool is null || sender is not DrawingSurface surface || SlotOf(surface) is not { } slot) return;
        var point = e.GetCurrentPoint(surface);
        if (!point.Properties.IsLeftButtonPressed) return;
        e.Handled = true;

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
        _preview!.Stroke = ToolBrush();
        _preview.StrokeThickness = thickness;
        surface.Children.Add(_preview);
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_preview is null || _tool is null || !ReferenceEquals(sender, _drawSurface)) return;
        e.Handled = true;
        var current = Clamp(e.GetCurrentPoint(_drawSurface).Position);
        switch (_tool.Mode)
        {
            case DrawMode.Box:
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
        // No desenho livre o traço já está guardado; nas outras ferramentas, perder o mouse cancela.
        if (ReferenceEquals(sender, _drawSurface) && _tool?.Mode != DrawMode.Freehand) CancelDrawing();
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
