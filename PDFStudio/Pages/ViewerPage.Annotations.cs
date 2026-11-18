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
        if (_work is null || (undo ? !_work.CanUndo : !_work.CanRedo)) return;
        CancelDrawing();
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

    /// <summary>Uma ferramenta de comentário: chave do botão, nome e o que mostrar na barra de opções.</summary>
    private sealed record CommentTool(string Key, string Name, AnnotationKind Kind, string Hint, bool UsesColor = true, bool UsesWidth = true);

    private static readonly Dictionary<string, CommentTool> CommentTools = new()
    {
        ["retangulo"] = new("retangulo", "Retângulo", AnnotationKind.Rectangle, "Arraste na página para desenhar."),
    };

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

    /// <summary>Liga uma ferramenta (ou desliga, com null). A ferramenta continua ativa até Esc ou "Concluir".</summary>
    private void SetTool(string? key)
    {
        CancelDrawing();
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

    /// <summary>O visualizador mede em DIPs (1/96 pol.); o PDF, em pontos (1/72 pol.).</summary>
    private const double DipsPerPoint = 96.0 / 72;

    private void Surface_PointerEntered(object sender, PointerRoutedEventArgs e) =>
        ((DrawingSurface)sender).SetCursor(_tool is null ? null : InputSystemCursorShape.Cross);

    private void Surface_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_tool is null || sender is not DrawingSurface surface || surface.DataContext is not PageSlot slot) return;
        var point = e.GetCurrentPoint(surface);
        if (!point.Properties.IsLeftButtonPressed) return;

        CancelDrawing();
        surface.CapturePointer(e.Pointer);
        _drawSurface = surface;
        _drawSlot = slot;
        _drawStart = point.Position;
        var color = ToolColor;
        _preview = new Shapes.Rectangle
        {
            Stroke = new SolidColorBrush(Windows.UI.Color.FromArgb(255, color.R, color.G, color.B)),
            StrokeThickness = App.Settings.CommentLineWidth * DipsPerPoint,
        };
        PlacePreview(_drawStart);
        surface.Children.Add(_preview);
        e.Handled = true;
    }

    private void Surface_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_preview is null || !ReferenceEquals(sender, _drawSurface)) return;
        PlacePreview(e.GetCurrentPoint(_drawSurface).Position);
        e.Handled = true;
    }

    private async void Surface_PointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (_preview is null || _tool is null || _drawSurface is null || _drawSlot is null || !ReferenceEquals(sender, _drawSurface)) return;
        e.Handled = true;
        var (x, y, w, h) = DragRect(e.GetCurrentPoint(_drawSurface).Position);
        var tool = _tool;
        int page = _drawSlot.Index;
        CancelDrawing();
        if (w < 4 || h < 4) return; // clique sem arrastar

        // Tela (DIPs) → pontos do PDF; a conversão para o sistema do PDF (rotação etc.) é feita no Core.
        double px = x / DipsPerPoint, py = y / DipsPerPoint, pw = w / DipsPerPoint, ph = h / DipsPerPoint;
        var style = ToolStyle;
        string author = App.Settings.CommentAuthor;
        await ApplyEditAsync((input, output) =>
            PdfAnnotations.AddShape(input, page, tool.Kind, px, py, pw, ph, style, author, output));
    }

    private void Surface_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (ReferenceEquals(sender, _drawSurface)) CancelDrawing();
    }

    /// <summary>Retângulo entre o ponto inicial e <paramref name="current"/>, limitado à página.</summary>
    private (double X, double Y, double W, double H) DragRect(Point current)
    {
        double maxX = _drawSurface!.ActualWidth, maxY = _drawSurface.ActualHeight;
        double x2 = Math.Clamp(current.X, 0, maxX), y2 = Math.Clamp(current.Y, 0, maxY);
        double x = Math.Min(_drawStart.X, x2), y = Math.Min(_drawStart.Y, y2);
        return (x, y, Math.Abs(x2 - _drawStart.X), Math.Abs(y2 - _drawStart.Y));
    }

    private void PlacePreview(Point current)
    {
        var (x, y, w, h) = DragRect(current);
        Canvas.SetLeft(_preview, x);
        Canvas.SetTop(_preview, y);
        _preview!.Width = w;
        _preview.Height = h;
    }

    /// <summary>Remove o desenho provisório e solta o mouse.</summary>
    private void CancelDrawing()
    {
        if (_drawSurface is not null)
        {
            if (_preview is not null) _drawSurface.Children.Remove(_preview);
            _drawSurface.ReleasePointerCaptures();
        }
        _preview = null;
        _drawSurface = null;
        _drawSlot = null;
    }
}
