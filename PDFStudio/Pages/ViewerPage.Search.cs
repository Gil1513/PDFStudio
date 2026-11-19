using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Etapa 7.1: pesquisar no documento. Painel lateral com a caixa de texto, a lista de resultados (com o trecho
// em volta) e os destaques amarelos nas páginas; o resultado atual fica em laranja.
public sealed partial class ViewerPage
{
    private const string SearchMarkTag = "pesquisa";

    private static readonly SolidColorBrush HitBrush = new(Windows.UI.Color.FromArgb(90, 255, 214, 0));
    private static readonly SolidColorBrush CurrentHitBrush = new(Windows.UI.Color.FromArgb(130, 255, 120, 0));
    private static readonly SolidColorBrush CurrentHitStroke = new(Windows.UI.Color.FromArgb(255, 230, 81, 0));

    private List<SearchHit> _hits = [];
    private int _hitIndex = -1;
    private CancellationTokenSource? _searchCancel;
    private DispatcherTimer? _searchTimer;

    // Texto do documento já lido (vale para uma versão do arquivo; editar cria outra).
    private List<IReadOnlyList<TextWord>>? _searchWords;
    private string? _searchWordsPath;

    private bool SearchPanelOpen => SearchToggle.IsChecked == true && CommentsToggle.IsChecked != true
                                    && AttachmentsToggle.IsChecked != true && _path is not null;

    private void SearchToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_commentPanelReady) return;
        if (SearchToggle.IsChecked == true) ThumbnailsToggle.IsChecked = CommentsToggle.IsChecked = AttachmentsToggle.IsChecked = false; // um painel por vez
        UpdateSidePanel();
    }

    /// <summary>Ctrl+F: abre o painel e põe o cursor na caixa (com o texto anterior selecionado).</summary>
    private void OpenSearch()
    {
        if (_path is null) return;
        if (SearchToggle.IsChecked != true) SearchToggle.IsChecked = true; // o Checked atualiza o painel
        else UpdateSidePanel();
        SearchBox.Focus(FocusState.Programmatic);
        SearchBox.SelectAll();
    }

    /// <summary>O painel abriu ou fechou: os destaques só aparecem com ele aberto.</summary>
    private void SearchPanelChanged(bool open)
    {
        if (open)
        {
            SearchBox.Focus(FocusState.Programmatic);
            if (SearchBox.Text.Trim().Length > 0 && _searchWordsPath != DisplayPath) _ = RunSearchAsync(keepPosition: true);
        }
        RefreshSearchMarks();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        // Espera a pessoa parar de digitar um instante antes de pesquisar.
        if (_searchTimer is null)
        {
            _searchTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
            _searchTimer.Tick += (_, _) => { _searchTimer.Stop(); _ = RunSearchAsync(); };
        }
        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void SearchOption_Click(object sender, RoutedEventArgs e) => _ = RunSearchAsync();

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.Enter) return;
        e.Handled = true;
        bool shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (_searchTimer?.IsEnabled == true)
        {
            // Enter antes da pesquisa automática: pesquisa já.
            _searchTimer.Stop();
            _ = RunSearchAsync();
            return;
        }
        StepHit(shift ? -1 : +1);
    }

    private void SearchPrev_Click(object sender, RoutedEventArgs e) => StepHit(-1);
    private void SearchNext_Click(object sender, RoutedEventArgs e) => StepHit(+1);

    private void SearchResultList_ItemClick(object sender, ItemClickEventArgs e)
    {
        int index = SearchResultList.Items.IndexOf(e.ClickedItem);
        if (index >= 0) GoToHit(index);
    }

    /// <summary>Vai para o próximo (+1) ou anterior (-1) resultado, dando a volta no fim/começo.</summary>
    private void StepHit(int direction)
    {
        if (_hits.Count == 0) return;
        GoToHit(_hitIndex < 0 ? (direction > 0 ? 0 : _hits.Count - 1) : (_hitIndex + direction + _hits.Count) % _hits.Count);
    }

    /// <summary>Depois de editar (ou desfazer) o texto pode ter mudado: pesquisa de novo, ficando no mesmo lugar.</summary>
    private async Task RefreshSearchAfterEditAsync()
    {
        if (!SearchPanelOpen || SearchBox.Text.Trim().Length == 0 || _searchWordsPath == DisplayPath) return;
        await RunSearchAsync(keepPosition: true);
    }

    private async Task RunSearchAsync(bool keepPosition = false)
    {
        _searchCancel?.Cancel();
        if (!SearchPanelOpen || DisplayPath is not { } path) return;
        var cancel = (_searchCancel = new CancellationTokenSource()).Token;
        string query = SearchBox.Text;
        var options = new SearchOptions(SearchCaseCheck.IsChecked == true, SearchWordCheck.IsChecked == true, SearchAccentCheck.IsChecked == true);
        int previous = _hitIndex;

        if (query.Trim().Length == 0)
        {
            ShowHits([], -1, "");
            return;
        }
        SearchCountText.Text = "Pesquisando…";
        List<SearchHit> hits;
        try
        {
            if (_searchWordsPath != path || _searchWords is null)
            {
                var words = await Task.Run(() => PdfTextLayout.AllWords(path, cancel), cancel);
                if (cancel.IsCancellationRequested) return;
                (_searchWords, _searchWordsPath) = (words, path);
            }
            var pages = _searchWords;
            hits = await Task.Run(() => PdfSearch.Find(pages, query, options, cancel), cancel);
        }
        catch (OperationCanceledException)
        {
            return; // outra pesquisa começou
        }
        if (cancel.IsCancellationRequested) return;

        int current;
        if (hits.Count == 0) current = -1;
        else if (keepPosition && previous >= 0) current = Math.Min(previous, hits.Count - 1);
        else
        {
            // Primeiro resultado a partir da página que está na tela.
            current = hits.FindIndex(h => h.Page >= CurrentIndex);
            if (current < 0) current = 0;
        }
        ShowHits(hits, current, query);
        if (current >= 0 && !keepPosition) GoToHit(current);
    }

    private void ShowHits(List<SearchHit> hits, int current, string query)
    {
        _hits = hits;
        _hitIndex = current;
        SearchResultList.Items.Clear();
        foreach (var hit in hits) SearchResultList.Items.Add(ResultItem(hit));
        if (current >= 0) SearchResultList.SelectedIndex = current;
        SearchPrevButton.IsEnabled = SearchNextButton.IsEnabled = hits.Count > 0;
        UpdateSearchCount(query);
        RefreshSearchMarks();
    }

    private void UpdateSearchCount(string? query = null)
    {
        query ??= SearchBox.Text;
        if (query.Trim().Length == 0) { SearchCountText.Text = "Digite o que procura. Enter vai para o próximo; Shift+Enter, para o anterior."; return; }
        if (_hits.Count == 0)
        {
            bool noText = _searchWords is { } w && w.All(p => p.Count == 0);
            SearchCountText.Text = noText
                ? "Este PDF não tem texto para pesquisar (parece ser imagem escaneada). Isso será possível com o OCR."
                : "Nenhum resultado.";
            return;
        }
        int pages = _hits.Select(h => h.Page).Distinct().Count();
        string total = $"{Plural(_hits.Count, "resultado", "resultados")} em {Plural(pages, "página", "páginas")}";
        if (_hits.Count >= PdfSearch.MaxHits) total += $" (mostrando os primeiros {PdfSearch.MaxHits})";
        SearchCountText.Text = _hitIndex >= 0 ? $"{_hitIndex + 1} de {total}" : total;
    }

    /// <summary>Item da lista: "Página n" e o trecho com o texto achado em negrito.</summary>
    private static UIElement ResultItem(SearchHit hit)
    {
        var text = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 3, TextTrimming = TextTrimming.CharacterEllipsis };
        text.Inlines.Add(new Run { Text = hit.Before });
        text.Inlines.Add(new Run { Text = hit.Match, FontWeight = FontWeights.Bold });
        text.Inlines.Add(new Run { Text = hit.After });
        var page = new TextBlock
        {
            Text = $"Página {hit.Page + 1}", FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var panel = new StackPanel { Padding = new Thickness(0, 6, 0, 6), Spacing = 2, Children = { page, text } };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(panel, $"Página {hit.Page + 1}: {hit.Before}{hit.Match}{hit.After}");
        return panel;
    }

    /// <summary>Mostra o resultado <paramref name="index"/>: rola até ele (no terço de cima da tela) e o pinta de laranja.</summary>
    private void GoToHit(int index)
    {
        if (index < 0 || index >= _hits.Count || _slots.Count == 0) return;
        _hitIndex = index;
        var hit = _hits[index];
        SearchResultList.SelectedIndex = index;
        SearchResultList.ScrollIntoView(SearchResultList.Items[index]);
        double top = hit.Boxes.Count > 0 ? hit.Boxes.Min(b => b.Y) * DipsPerPoint : 0;
        double y = (PageTop(hit.Page) + top) * Scroller.ZoomFactor - Scroller.ViewportHeight / 3;
        Scroller.ChangeView(null, Math.Max(0, y), null, true);
        SetPageBox(hit.Page + 1);
        SyncThumbnailSelection(hit.Page);
        UpdateSearchCount();
        RefreshSearchMarks();
    }

    // ---------------- Destaques nas páginas ----------------

    /// <summary>Atualiza os destaques de todas as páginas que estão na tela.</summary>
    private void RefreshSearchMarks()
    {
        for (int i = 0; i < _slots.Count; i++)
            if (PagesRepeater.TryGetElement(i) is { } element) ShowSearchMarks(element, i);
    }

    /// <summary>Põe na página (elemento do repetidor) os destaques dos resultados dela; tira os que não são dela.</summary>
    private void ShowSearchMarks(UIElement pageElement, int pageIndex)
    {
        if (SurfaceIn(pageElement) is not { } surface) return;
        for (int i = surface.Children.Count - 1; i >= 0; i--)
            if (surface.Children[i] is FrameworkElement { Tag: SearchMarkTag }) surface.Children.RemoveAt(i);
        if (!SearchPanelOpen || _hits.Count == 0) return;

        int insertAt = 0; // por baixo dos desenhos de comentário
        for (int h = 0; h < _hits.Count; h++)
        {
            if (_hits[h].Page != pageIndex) continue;
            bool current = h == _hitIndex;
            foreach (var box in _hits[h].Boxes)
            {
                var shape = new Shapes.Rectangle
                {
                    Width = Math.Max(1, box.Width * DipsPerPoint),
                    Height = Math.Max(1, box.Height * DipsPerPoint),
                    Fill = current ? CurrentHitBrush : HitBrush,
                    Stroke = current ? CurrentHitStroke : null,
                    StrokeThickness = current ? 1.5 : 0,
                    RadiusX = 2, RadiusY = 2,
                    IsHitTestVisible = false,
                    Tag = SearchMarkTag,
                };
                Canvas.SetLeft(shape, box.X * DipsPerPoint);
                Canvas.SetTop(shape, box.Y * DipsPerPoint);
                surface.Children.Insert(insertAt++, shape);
            }
        }
    }

    private static DrawingSurface? SurfaceIn(UIElement pageElement) =>
        (pageElement as Border)?.Child is Grid grid ? grid.Children.OfType<DrawingSurface>().FirstOrDefault() : null;
}
