using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PDFStudio.Core;

namespace PDFStudio.Pages;

// Etapa 7.2: painel "Marcadores" — o sumário do PDF. Clicar leva ao ponto; + marca o ponto que está na tela.
public sealed partial class ViewerPage
{
    /// <summary>Um marcador na árvore da tela, com o caminho dele no arquivo (posição em cada nível).</summary>
    private sealed class BookmarkItem(Bookmark bookmark, int[] path)
    {
        public Bookmark Bookmark { get; } = bookmark;
        public int[] Path { get; } = path;
        public string Key => string.Join(".", Path);
        public override string ToString() => Bookmark.Title;
    }

    private int _bookmarkVersion;
    private int[]? _bookmarkSelectAfter; // marcador a selecionar depois da próxima atualização da lista

    private bool BookmarksPanelOpen => BookmarksToggle.IsChecked == true && CommentsToggle.IsChecked != true
                                       && AttachmentsToggle.IsChecked != true && SearchToggle.IsChecked != true && _path is not null;

    private void BookmarksToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_commentPanelReady) return;
        if (BookmarksToggle.IsChecked == true) ThumbnailsToggle.IsChecked = CommentsToggle.IsChecked = AttachmentsToggle.IsChecked = SearchToggle.IsChecked = false; // um painel por vez
        UpdateSidePanel();
    }

    private BookmarkItem? SelectedBookmark => BookmarkTree.SelectedNode?.Content as BookmarkItem;

    private async Task RefreshBookmarksAsync()
    {
        if (!_commentPanelReady || !BookmarksPanelOpen || DisplayPath is not { } path) return;
        int version = ++_bookmarkVersion;
        string? selectKey = _bookmarkSelectAfter is { } after ? string.Join(".", after) : SelectedBookmark?.Key;
        _bookmarkSelectAfter = null;
        var expanded = new HashSet<string>();
        foreach (var node in AllNodes(BookmarkTree.RootNodes))
            if (node.IsExpanded && node.Content is BookmarkItem item) expanded.Add(item.Key);
        bool firstLoad = BookmarkTree.RootNodes.Count == 0;

        List<Bookmark> list;
        try
        {
            list = await Task.Run(() => PdfBookmarks.List(path));
        }
        catch (Exception ex)
        {
            BookmarkTree.RootNodes.Clear();
            BookmarkCountText.Text = $"Não foi possível ler os marcadores: {ex.Message}";
            return;
        }
        if (version != _bookmarkVersion) return;

        BookmarkTree.RootNodes.Clear();
        TreeViewNode? toSelect = null;
        void AddNodes(IList<TreeViewNode> target, IReadOnlyList<Bookmark> items, int[] parentPath)
        {
            for (int i = 0; i < items.Count; i++)
            {
                var item = new BookmarkItem(items[i], [.. parentPath, i]);
                var node = new TreeViewNode { Content = item };
                AddNodes(node.Children, items[i].Children, item.Path);
                node.IsExpanded = node.Children.Count > 0 && (expanded.Contains(item.Key) || (firstLoad && items[i].IsOpen) || IsAncestorOf(item.Key, selectKey));
                target.Add(node);
                if (item.Key == selectKey) toSelect = node;
            }
        }
        AddNodes(BookmarkTree.RootNodes, list, []);
        if (toSelect is not null) BookmarkTree.SelectedNode = toSelect;

        int total = AllNodes(BookmarkTree.RootNodes).Count();
        BookmarkCountText.Text = total == 0
            ? "Este documento não tem marcadores. Vá até um ponto do documento e clique em + para marcá-lo."
            : $"{Plural(total, "marcador", "marcadores")}. Clique num marcador para ir até ele.";
        UpdateBookmarkActions();
    }

    private static bool IsAncestorOf(string key, string? other) => other is not null && other.StartsWith(key + ".", StringComparison.Ordinal);

    private static IEnumerable<TreeViewNode> AllNodes(IEnumerable<TreeViewNode> nodes)
    {
        foreach (var node in nodes)
        {
            yield return node;
            foreach (var child in AllNodes(node.Children)) yield return child;
        }
    }

    private void UpdateBookmarkActions()
    {
        bool selected = SelectedBookmark is not null;
        RenameBookmarkButton.IsEnabled = RetargetBookmarkButton.IsEnabled = DeleteBookmarkButton.IsEnabled = BookmarkMoreButton.IsEnabled = selected;
    }

    private void BookmarkTree_SelectionChanged(TreeView sender, TreeViewSelectionChangedEventArgs args) => UpdateBookmarkActions();

    private void BookmarkTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        // O clique também seleciona; a seleção só muda depois deste evento.
        DispatcherQueue.TryEnqueue(UpdateBookmarkActions);
        if ((args.InvokedItem as TreeViewNode)?.Content is not BookmarkItem item) return;
        if (item.Bookmark.Page is not { } page)
        {
            ShowStatus(InfoBarSeverity.Informational, "Marcador sem página",
                $"“{item.Bookmark.Title}” não leva a uma página deste documento (pode abrir um site ou outro arquivo).");
            return;
        }
        GoToPoint(page, item.Bookmark.Top);
    }

    private async void BookmarkTree_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (SelectedBookmark is null) return;
        if (e.Key == Windows.System.VirtualKey.F2) { e.Handled = true; await RenameBookmarkAsync(); }
        else if (e.Key == Windows.System.VirtualKey.Delete) { e.Handled = true; await DeleteBookmarkAsync(); }
    }

    /// <summary>Rola até a página e a altura (pontos) indicadas.</summary>
    private void GoToPoint(int page, double? top)
    {
        if (_slots.Count == 0) return;
        page = Math.Clamp(page, 0, _slots.Count - 1);
        double y = PageTop(page) + (top ?? 0) * DipsPerPoint;
        Scroller.ChangeView(null, Math.Max(0, y * Scroller.ZoomFactor), null);
        SetPageBox(page + 1);
        SyncThumbnailSelection(page);
    }

    /// <summary>Página e altura (pontos) do que está no alto da tela agora.</summary>
    private (int Page, double Top) CurrentViewPoint()
    {
        double y = Scroller.VerticalOffset / Scroller.ZoomFactor;
        int page = 0;
        for (int i = 0; i < _slots.Count; i++)
        {
            if (PageTop(i) > y + 2) break; // folga: a rolagem com zoom arredonda e pode parar um pouquinho antes da página
            page = i;
        }
        double top = Math.Clamp((y - PageTop(page)) / DipsPerPoint, 0, _slots[page].Height / DipsPerPoint);
        return (page, Math.Round(top));
    }

    // ---------------- Ações ----------------

    private async void AddBookmark_Click(object sender, RoutedEventArgs e) => await AddBookmarkAsync();
    private async void RenameBookmark_Click(object sender, RoutedEventArgs e) => await RenameBookmarkAsync();
    private async void DeleteBookmark_Click(object sender, RoutedEventArgs e) => await DeleteBookmarkAsync();

    private async Task AddBookmarkAsync()
    {
        if (_slots.Count == 0) return;
        var (page, top) = CurrentViewPoint();
        if (await AskBookmarkTitleAsync("Adicionar marcador", $"Página {page + 1}", "Adicionar",
                $"O marcador vai levar ao ponto que está no alto da tela agora (página {page + 1}).") is not { } title) return;
        int[] parent = SelectedBookmark is { } selected ? selected.Path[..^1] : [];
        int index = SelectedBookmark is { } s ? s.Path[^1] + 1 : BookmarkTree.RootNodes.Count;
        await EditBookmarksAsync([.. parent, index], (input, output) => PdfBookmarks.Add(input, parent, index, title, page, top, output));
    }

    private async Task RenameBookmarkAsync()
    {
        if (SelectedBookmark is not { } item) return;
        if (await AskBookmarkTitleAsync("Renomear marcador", item.Bookmark.Title, "Renomear", null) is not { } title
            || title == item.Bookmark.Title) return;
        await EditBookmarksAsync(item.Path, (input, output) => PdfBookmarks.Rename(input, item.Path, title, output));
    }

    private async void RetargetBookmark_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBookmark is not { } item || _slots.Count == 0) return;
        var (page, top) = CurrentViewPoint();
        if (await EditBookmarksAsync(item.Path, (input, output) => PdfBookmarks.SetDestination(input, item.Path, page, top, output)))
            ShowStatus(InfoBarSeverity.Success, "Marcador atualizado", $"“{item.Bookmark.Title}” agora leva à página {page + 1}. Ctrl+Z desfaz.");
    }

    private async Task DeleteBookmarkAsync()
    {
        if (SelectedBookmark is not { } item) return;
        int inside = AllNodes(BookmarkTree.SelectedNode!.Children).Count();
        int[] next = item.Path[^1] > 0 ? [.. item.Path[..^1], item.Path[^1] - 1] : item.Path[..^1];
        if (await EditBookmarksAsync(next, (input, output) => PdfBookmarks.Delete(input, item.Path, output)))
            ShowStatus(InfoBarSeverity.Success, "Marcador excluído",
                inside == 0 ? $"“{item.Bookmark.Title}” saiu. Ctrl+Z desfaz."
                            : $"“{item.Bookmark.Title}” saiu com {Plural(inside, "marcador", "marcadores")} de dentro. Ctrl+Z desfaz.");
    }

    private async void BookmarkUp_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBookmark is not { } item || item.Path[^1] == 0) return;
        int[] parent = item.Path[..^1];
        int index = item.Path[^1] - 1;
        await EditBookmarksAsync([.. parent, index], (input, output) => PdfBookmarks.Move(input, item.Path, parent, index, output));
    }

    private async void BookmarkDown_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBookmark is not { } item) return;
        int siblings = BookmarkTree.SelectedNode!.Parent is { } p && p.Content is BookmarkItem ? p.Children.Count : BookmarkTree.RootNodes.Count;
        if (item.Path[^1] >= siblings - 1) return;
        int[] parent = item.Path[..^1];
        int index = item.Path[^1] + 1;
        await EditBookmarksAsync([.. parent, index], (input, output) => PdfBookmarks.Move(input, item.Path, parent, index, output));
    }

    /// <summary>Coloca dentro do marcador de cima (vira o último filho dele).</summary>
    private async void BookmarkIndent_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBookmark is not { } item) return;
        if (item.Path[^1] == 0)
        {
            ShowStatus(InfoBarSeverity.Informational, "Não há marcador acima", "Para colocar dentro de outro, o marcador precisa ter um marcador logo acima, no mesmo nível.");
            return;
        }
        int[] newParent = [.. item.Path[..^1], item.Path[^1] - 1];
        var siblings = BookmarkTree.SelectedNode!.Parent is { } p && p.Content is BookmarkItem ? p.Children : BookmarkTree.RootNodes;
        int index = siblings[item.Path[^1] - 1].Children.Count;
        await EditBookmarksAsync([.. newParent, index], (input, output) => PdfBookmarks.Move(input, item.Path, newParent, index, output));
    }

    /// <summary>Tira de dentro do marcador pai (fica logo depois dele).</summary>
    private async void BookmarkOutdent_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedBookmark is not { } item || item.Path.Length < 2) return;
        int[] newParent = item.Path[..^2];
        int index = item.Path[^2] + 1;
        await EditBookmarksAsync([.. newParent, index], (input, output) => PdfBookmarks.Move(input, item.Path, newParent, index, output));
    }

    /// <summary>Aplica a mudança (com Desfazer) e, na lista atualizada, seleciona <paramref name="selectAfter"/>.</summary>
    private async Task<bool> EditBookmarksAsync(int[] selectAfter, Action<string, string> edit)
    {
        _bookmarkSelectAfter = selectAfter;
        bool ok = await ApplyEditAsync(edit);
        if (!ok) _bookmarkSelectAfter = null;
        return ok;
    }

    private async Task<string?> AskBookmarkTitleAsync(string caption, string text, string button, string? hint)
    {
        var box = new TextBox { Header = "Nome do marcador", Text = text, MinWidth = 320 };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(box, "Nome do marcador");
        var panel = new StackPanel { Spacing = 8, Children = { box } };
        if (hint is not null)
            panel.Children.Add(new TextBlock { Text = hint, Opacity = 0.75, TextWrapping = TextWrapping.Wrap, MaxWidth = 380 });
        var dialog = Prepare(new ContentDialog
        {
            Title = caption,
            Content = panel,
            PrimaryButtonText = button,
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = box.Text.Trim().Length > 0;
        dialog.Opened += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }
}
