using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Etapa 4.6: painel "Comentários" — lista por página, ir até o comentário, editar texto, cor, excluir,
// filtrar por tipo e ocultar todos na tela.
public sealed partial class ViewerPage
{
    /// <summary>Filtros do painel: nome e os tipos que cada um mostra (null = todos).</summary>
    private static readonly (string Name, AnnotationKind[]? Kinds)[] CommentFilters =
    [
        ("Todos os tipos", null),
        ("Formas", [AnnotationKind.Rectangle, AnnotationKind.Oval, AnnotationKind.Line]),
        ("Desenho livre", [AnnotationKind.Ink]),
        ("Caixas de texto e notas", [AnnotationKind.TextBox, AnnotationKind.Note]),
        ("Realce, sublinhado e riscado", [AnnotationKind.Highlight, AnnotationKind.Underline, AnnotationKind.StrikeOut]),
        ("Carimbos", [AnnotationKind.Stamp]),
        ("Outros", [AnnotationKind.Other]),
    ];

    private int _commentListVersion;
    private bool _commentPanelReady;

    private bool CommentsPanelOpen => CommentsToggle.IsChecked == true && _path is not null;

    private void InitCommentsPanel()
    {
        foreach (var (name, _) in CommentFilters) CommentFilterBox.Items.Add(name);
        CommentFilterBox.SelectedIndex = 0;
        foreach (var (name, hex) in Palette)
        {
            var button = new Button
            {
                Width = 32, Height = 32, Padding = new Thickness(0), CornerRadius = new CornerRadius(16), Tag = hex,
                Content = new Border { Width = 22, Height = 22, CornerRadius = new CornerRadius(11), Background = Brush(hex) },
            };
            ToolTipService.SetToolTip(button, name);
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, name);
            button.Click += CommentColor_Click;
            CommentColorPalette.Children.Add(button);
        }
        _commentPanelReady = true;
    }

    private void CommentsToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_commentPanelReady) return;
        if (CommentsToggle.IsChecked == true) ThumbnailsToggle.IsChecked = false; // um painel por vez
        UpdateSidePanel();
    }

    private void CommentFilterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_commentPanelReady) _ = RefreshCommentListAsync();
    }

    /// <summary>Relê os comentários do documento e monta a lista (só quando o painel está aberto).</summary>
    private async Task RefreshCommentListAsync()
    {
        if (!_commentPanelReady || !CommentsPanelOpen || DisplayPath is not { } path) return;
        int version = ++_commentListVersion;
        string? selectedId = SelectedComment?.Info.Id;
        List<AnnotationInfo> all;
        try
        {
            all = await Task.Run(() => PdfAnnotations.List(path));
        }
        catch (Exception ex)
        {
            CommentList.ItemsSource = null;
            CommentCountText.Text = $"Não foi possível ler os comentários: {ex.Message}";
            return;
        }
        if (version != _commentListVersion) return; // outra atualização começou depois desta

        var kinds = CommentFilters[Math.Max(0, CommentFilterBox.SelectedIndex)].Kinds;
        var shown = all.Where(a => kinds is null || kinds.Contains(a.Kind)).ToList();
        var items = shown.Select((a, i) => new CommentItem(a, i == 0 || shown[i - 1].PageIndex != a.PageIndex)).ToList();
        CommentList.ItemsSource = items;
        // Mantém selecionado o mesmo comentário depois de uma alteração (pelo identificador único).
        if (!string.IsNullOrEmpty(selectedId)) CommentList.SelectedItem = items.FirstOrDefault(i => i.Info.Id == selectedId);

        CommentCountText.Text = all.Count == 0 ? "Este documento não tem comentários. Use as ferramentas da aba Comentário para criar."
            : shown.Count == all.Count ? Plural(all.Count, "comentário", "comentários")
            : $"{shown.Count} de {Plural(all.Count, "comentário", "comentários")}";
        if (_hideComments) CommentCountText.Text += " (ocultos na tela)";
        UpdateCommentActions();
    }

    private static string Plural(int n, string one, string many) => $"{n} {(n == 1 ? one : many)}";

    private CommentItem? SelectedComment => CommentList.SelectedItem as CommentItem;

    private void CommentList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateCommentActions();

    private void UpdateCommentActions()
    {
        bool any = SelectedComment is not null;
        EditCommentButton.IsEnabled = CommentColorButton.IsEnabled = DeleteCommentButton.IsEnabled = any;
    }

    /// <summary>Clicar num comentário: vai até ele na página e o destaca por um instante.</summary>
    private void CommentList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is CommentItem item) GoToComment(item.Info);
    }

    private void GoToComment(AnnotationInfo info)
    {
        if (info.PageIndex < 0 || info.PageIndex >= _slots.Count) return;
        double zoom = Scroller.ZoomFactor;
        double top = PageTop(info.PageIndex) + info.Y * DipsPerPoint - 60;
        Scroller.ChangeView(null, Math.Max(0, top) * zoom, null);
        SetPageBox(info.PageIndex + 1);
        // A página pode ainda não estar desenhada: espera o layout e então destaca.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _ = FlashCommentAsync(info));
    }

    private async Task FlashCommentAsync(AnnotationInfo info)
    {
        await Task.Delay(250);
        if (PagesRepeater.TryGetElement(info.PageIndex) is not { } element || FindSurface(element) is not { } surface) return;
        var accent = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var mark = new Shapes.Rectangle
        {
            Width = info.Width * DipsPerPoint + 8,
            Height = info.Height * DipsPerPoint + 8,
            Stroke = accent,
            StrokeThickness = 3,
            StrokeDashArray = [3, 2],
            RadiusX = 4,
            RadiusY = 4,
            IsHitTestVisible = false,
        };
        Canvas.SetLeft(mark, info.X * DipsPerPoint - 4);
        Canvas.SetTop(mark, info.Y * DipsPerPoint - 4);
        surface.Children.Add(mark);
        await Task.Delay(1600);
        surface.Children.Remove(mark);
    }

    private static DrawingSurface? FindSurface(DependencyObject root)
    {
        if (root is DrawingSurface surface) return surface;
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
            if (FindSurface(VisualTreeHelper.GetChild(root, i)) is { } found) return found;
        return null;
    }

    // ---------------- Ações ----------------

    private async void EditComment_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedComment is not { } item) return;
        var info = item.Info;
        var box = new TextBox
        {
            Text = info.Contents,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 140,
            PlaceholderText = info.Kind == AnnotationKind.TextBox ? "Texto da caixa" : "Comentário (pode ficar vazio)",
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Texto do comentário");
        var dialog = Prepare(new ContentDialog
        {
            Title = $"Editar texto — {item.KindName} (página {info.PageIndex + 1})",
            Content = box,
            PrimaryButtonText = "Salvar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || box.Text == info.Contents) return;
        string text = box.Text;
        await ApplyEditAsync((input, output) => PdfAnnotations.SetContents(input, info.PageIndex, info.Index, text, output));
    }

    private async void CommentColor_Click(object sender, RoutedEventArgs e)
    {
        CommentColorFlyout.Hide();
        if (SelectedComment is not { } item) return;
        var info = item.Info;
        var color = AnnotationColor.FromHex((string)((FrameworkElement)sender).Tag);
        await ApplyEditAsync((input, output) => PdfAnnotations.SetColor(input, info.PageIndex, info.Index, color, output));
    }

    private async void DeleteComment_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedComment is not { } item) return;
        var info = item.Info;
        if (await ApplyEditAsync((input, output) => PdfAnnotations.Delete(input, info.PageIndex, info.Index, output)))
            ShowStatus(InfoBarSeverity.Success, "Comentário excluído", "Use Desfazer (Ctrl+Z) se quiser trazê-lo de volta.");
    }

    // ---------------- Ocultar todos (só na tela) ----------------

    private bool _hideComments;
    private string? _hiddenCopy;

    private async void HideCommentsToggle_Changed(object sender, RoutedEventArgs e)
    {
        bool hide = HideCommentsToggle.IsChecked == true;
        if (!_commentPanelReady || hide == _hideComments) return; // mudança feita pelo próprio código
        _hideComments = hide;
        await RefreshDocumentAsync();
        await RefreshCommentListAsync();
    }

    /// <summary>
    /// Arquivo usado para desenhar as páginas: o documento atual ou, com "ocultar todos",
    /// uma cópia temporária sem os comentários (o documento em si não muda).
    /// </summary>
    private async Task<string?> RenderSourceAsync()
    {
        if (DisplayPath is not { } path) return null;
        DeleteHiddenCopy();
        if (!_hideComments) return path;
        string folder = Path.Combine(Path.GetTempPath(), "PDFStudio", "sem-comentarios");
        Directory.CreateDirectory(folder);
        // Cópias antigas que ficaram presas (o visualizador ainda as usava quando tentamos apagar).
        foreach (var old in Directory.GetFiles(folder, "*.pdf").Where(f => File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddHours(-1)))
            try { File.Delete(old); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        string copy = Path.Combine(folder, $"{Guid.NewGuid():N}.pdf");
        await Task.Run(() => PdfAnnotations.CopyWithoutComments(path, copy));
        _hiddenCopy = copy;
        return copy;
    }

    private void DeleteHiddenCopy()
    {
        if (_hiddenCopy is null) return;
        try { File.Delete(_hiddenCopy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        _hiddenCopy = null;
    }

    /// <summary>Volta a mostrar os comentários (ao criar um novo, ou ao abrir outro arquivo).</summary>
    private void ShowCommentsAgain()
    {
        _hideComments = false;
        HideCommentsToggle.IsChecked = false;
    }
}
