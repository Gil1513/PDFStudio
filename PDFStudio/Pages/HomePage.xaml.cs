using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PDFStudio.Pages;

/// <summary>Tela inicial: barra lateral, ferramentas rápidas e arquivos recentes.</summary>
public sealed partial class HomePage : Page
{
    private enum Section { Recent, Starred, Folders }
    private enum SortColumn { Date, Name, Size }

    private Section _section = Section.Recent;
    private SortColumn _sort = SortColumn.Date;
    private bool _descending = true;
    private bool _showAllTools;
    private RecentItem? _contextItem;
    /// <summary>Miniaturas já geradas, por caminho + data de modificação (evita refazer a cada atualização).</summary>
    private readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _thumbnails = new(StringComparer.OrdinalIgnoreCase);

    public HomePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        ToolGroupsRepeater.ItemsSource = ToolCatalog.Quick;
        SetViewMode(App.Settings.RecentAsGrid, save: false);
        SectionList.SelectedIndex = 0;
        App.Recent.Changed += (_, _) => DispatcherQueue.TryEnqueue(Refresh);
    }

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        Refresh(); // datas e tamanhos podem ter mudado enquanto outra tela estava aberta
    }

    // ---------- Barra lateral ----------

    private async void OpenPdf_Click(object sender, RoutedEventArgs e) => await App.MainWindow!.PickAndOpenAsync();

    private void CreateFromMerge_Click(object sender, RoutedEventArgs e) => App.MainWindow!.OpenTool("combinar");

    private void Settings_Click(object sender, RoutedEventArgs e) => App.MainWindow!.OpenTool("configuracoes");

    private async void CreateBlank_Click(object sender, RoutedEventArgs e)
    {
        var pages = new NumberBox
        {
            Header = "Número de páginas",
            Minimum = 1,
            Maximum = 1000,
            Value = 1,
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
        };
        var size = new ComboBox { Header = "Tamanho", HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        foreach (var (name, _) in PdfOperations.BlankPageSizes) size.Items.Add(name);
        var orientation = new RadioButtons { Header = "Orientação", SelectedIndex = 0, MaxColumns = 2 };
        orientation.Items.Add("Retrato");
        orientation.Items.Add("Paisagem");

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = App.Settings.Theme,
            Title = "Criar PDF em branco",
            Content = new StackPanel { Spacing = 16, MinWidth = 320, Children = { pages, size, orientation } },
            PrimaryButtonText = "Criar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        string? output = await FilePickers.SavePdfAsync("Novo documento");
        if (output is null) return;
        try
        {
            int count = double.IsNaN(pages.Value) ? 1 : (int)pages.Value;
            var pageSize = PdfOperations.BlankPageSizes[Math.Max(0, size.SelectedIndex)].Size;
            bool landscape = orientation.SelectedIndex == 1;
            await Task.Run(() => PdfOperations.CreateBlank(output, count, pageSize, landscape));
            App.MainWindow!.OpenInViewer(output);
        }
        catch (Exception ex)
        {
            ShowNotice(InfoBarSeverity.Error, "Não foi possível criar o PDF", ex.Message);
        }
    }

    private void SectionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SectionList.SelectedItem is not ListViewItem item) return;
        _section = (item.Tag as string) switch
        {
            "starred" => Section.Starred,
            "folders" => Section.Folders,
            _ => Section.Recent,
        };
        RecentTitle.Text = _section switch
        {
            Section.Starred => "Arquivos com estrela",
            Section.Folders => "Pastas recentes",
            _ => "Arquivos recentes",
        };
        SearchBox.PlaceholderText = _section == Section.Folders ? "Buscar pastas" : "Buscar arquivos";
        DateHeaderText.Text = _section == Section.Folders ? "Último uso" : "Aberto em";
        Refresh();
    }

    // ---------- Ferramentas ----------

    private void AllTools_Click(object sender, RoutedEventArgs e)
    {
        _showAllTools = !_showAllTools;
        ToolGroupsRepeater.ItemsSource = _showAllTools ? ToolCatalog.All : ToolCatalog.Quick;
        ToolsTitle.Text = _showAllTools ? "Todas as ferramentas" : "Ferramentas rápidas";
        AllToolsText.Text = _showAllTools ? "Mostrar menos" : "Todas as ferramentas";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(AllToolsLink, AllToolsText.Text);
    }

    private async void ToolCard_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolInfo tool) return;
        if (!tool.IsAvailable)
        {
            ShowNotice(InfoBarSeverity.Informational, $"{tool.Title}: em breve",
                "Esta ferramenta ainda está sendo construída e chega nas próximas etapas do PDF Studio.");
            return;
        }
        if (tool.Key == "viewer") await App.MainWindow!.PickAndOpenAsync();
        else App.MainWindow!.OpenTool(tool.Key);
    }

    private void ShowNotice(InfoBarSeverity severity, string title, string message)
    {
        NoticeBar.Severity = severity;
        NoticeBar.Title = title;
        NoticeBar.Message = message;
        NoticeBar.IsOpen = true;
    }

    // ---------- Lista de recentes ----------

    private void Refresh()
    {
        IEnumerable<RecentItem> items = _section switch
        {
            Section.Folders => App.Recent.Folders.Select(RecentItem.FromFolder),
            Section.Starred => App.Recent.Starred.Select(RecentItem.FromEntry),
            _ => App.Recent.Entries.Select(RecentItem.FromEntry),
        };

        string query = SearchBox.Text.Trim();
        if (query.Length > 0)
            items = items.Where(i => i.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                                     || i.Path.Contains(query, StringComparison.CurrentCultureIgnoreCase));

        items = (_sort, _descending) switch
        {
            (SortColumn.Name, false) => items.OrderBy(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            (SortColumn.Name, true) => items.OrderByDescending(i => i.Name, StringComparer.CurrentCultureIgnoreCase),
            (SortColumn.Size, false) => items.OrderBy(i => i.IsFolder ? i.FileCount : i.Size),
            (SortColumn.Size, true) => items.OrderByDescending(i => i.IsFolder ? i.FileCount : i.Size),
            // Data = quando foi aberto pela última vez (a mesma mostrada na lista).
            (SortColumn.Date, false) => items.OrderBy(i => i.LastOpened),
            _ => items.OrderByDescending(i => i.LastOpened),
        };

        var list = items.ToList();
        RecentList.ItemsSource = list;
        RecentGrid.ItemsSource = list;

        bool grid = App.Settings.RecentAsGrid && _section != Section.Folders;
        RecentList.Visibility = !grid && list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        RecentGrid.Visibility = grid && list.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ColumnHeaders.Visibility = RecentList.Visibility;
        ViewModeButtons.Visibility = _section == Section.Folders ? Visibility.Collapsed : Visibility.Visible;

        EmptyState.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        EmptyText.Text = query.Length > 0 ? $"Nada encontrado para \"{query}\"."
            : _section switch
            {
                Section.Starred => "Nenhum arquivo com estrela. Clique na estrela ao lado de um arquivo para ele aparecer aqui.",
                Section.Folders => "Nenhuma pasta recente ainda.",
                _ => "Nenhum arquivo recente. Abra um PDF ou arraste um arquivo para esta janela.",
            };
        UpdateSortIcons();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Refresh();

    private void SortByName_Click(object sender, RoutedEventArgs e) => SortBy(SortColumn.Name);
    private void SortByDate_Click(object sender, RoutedEventArgs e) => SortBy(SortColumn.Date);
    private void SortBySize_Click(object sender, RoutedEventArgs e) => SortBy(SortColumn.Size);

    private void SortBy(SortColumn column)
    {
        if (_sort == column) _descending = !_descending;
        else
        {
            _sort = column;
            _descending = column != SortColumn.Name; // nome: A→Z; data e tamanho: maior primeiro
        }
        Refresh();
    }

    private void UpdateSortIcons()
    {
        const string neutral = "", up = "", down = "";
        NameSortIcon.Glyph = _sort == SortColumn.Name ? (_descending ? down : up) : neutral;
        DateSortIcon.Glyph = _sort == SortColumn.Date ? (_descending ? down : up) : neutral;
        SizeSortIcon.Glyph = _sort == SortColumn.Size ? (_descending ? down : up) : neutral;
    }

    private void ListMode_Click(object sender, RoutedEventArgs e) => SetViewMode(grid: false, save: true);
    private void GridMode_Click(object sender, RoutedEventArgs e) => SetViewMode(grid: true, save: true);

    private void SetViewMode(bool grid, bool save)
    {
        ListModeButton.IsChecked = !grid;
        GridModeButton.IsChecked = grid;
        if (save)
        {
            App.Settings.RecentAsGrid = grid;
            App.Settings.Save();
            Refresh();
        }
    }

    private void Recent_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is RecentItem item) Open(item);
    }

    private void Open(RecentItem item)
    {
        if (item.IsFolder)
        {
            if (Directory.Exists(item.Path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{item.Path}\"") { UseShellExecute = true });
            else
                ShowNotice(InfoBarSeverity.Warning, "Pasta não encontrada", item.Path);
            return;
        }
        if (!File.Exists(item.Path))
        {
            ShowNotice(InfoBarSeverity.Warning, "Arquivo não encontrado",
                $"\"{item.Name}\" foi movido ou apagado. Use \"Remover da lista\" no menu do botão direito.");
            return;
        }
        App.MainWindow!.OpenInViewer(item.Path);
    }

    private void Star_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RecentItem item) return;
        item.Starred = !item.Starred;
        App.Recent.SetStarred(item.Path, item.Starred); // dispara Refresh (a lista "com estrela" se atualiza)
    }

    private async void RecentGrid_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        // Miniatura da primeira página, gerada só quando o bloco aparece.
        if (args.InRecycleQueue || args.Item is not RecentItem item || item.ThumbnailRequested || !item.Exists || item.IsFolder) return;
        item.ThumbnailRequested = true;
        string key = $"{item.Path}|{item.Modified.Ticks}";
        if (_thumbnails.TryGetValue(key, out var cached))
        {
            item.Thumbnail = cached;
            return;
        }
        try
        {
            var doc = await PdfRenderer.LoadAsync(item.Path);
            if (doc.PageCount == 0) return;
            using var page = doc.GetPage(0);
            double scale = 180.0 / Math.Max(page.Size.Width, page.Size.Height);
            item.Thumbnail = _thumbnails[key] = await PdfRenderer.RenderAsync(doc, 0, scale);
        }
        catch
        {
            // PDF protegido ou inválido: fica o ícone.
        }
    }

    private async void RemoveMissing_Click(object sender, RoutedEventArgs e)
    {
        int removed = await Task.Run(App.Recent.RemoveMissing);
        ShowNotice(InfoBarSeverity.Success, "Lista atualizada",
            removed == 0 ? "Todos os arquivos da lista ainda existem." : $"{removed} {(removed == 1 ? "arquivo removido" : "arquivos removidos")} da lista.");
    }

    private void ClearRecent_Click(object sender, RoutedEventArgs e) => App.Recent.Clear(keepStarred: true);

    // ---------- Menu do botão direito ----------

    private void RecentContextMenu_Opening(object sender, object e)
    {
        var flyout = (MenuFlyout)sender;
        _contextItem = (flyout.Target as FrameworkElement)?.DataContext as RecentItem;
        foreach (var entry in flyout.Items.OfType<MenuFlyoutItem>())
        {
            if (entry.Text is "Abrir" or "Abrir pasta") entry.Text = _contextItem?.IsFolder == true ? "Abrir pasta" : "Abrir";
            if (entry.Text == "Mostrar na pasta") entry.Visibility = _contextItem?.IsFolder == true ? Visibility.Collapsed : Visibility.Visible;
            if (entry.Text == "Remover da lista") entry.Visibility = _contextItem?.IsFolder == true ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void MenuOpen_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is not null) Open(_contextItem);
    }

    private void MenuShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is null) return;
        string args = File.Exists(_contextItem.Path) ? $"/select,\"{_contextItem.Path}\"" : $"\"{_contextItem.Folder}\"";
        Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
    }

    private void MenuCopyPath_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is null) return;
        var package = new DataPackage();
        package.SetText(_contextItem.Path);
        Clipboard.SetContent(package);
    }

    private void MenuRemove_Click(object sender, RoutedEventArgs e)
    {
        if (_contextItem is not null) App.Recent.Remove(_contextItem.Path);
    }

    // ---------- Arrastar e soltar ----------

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Abrir PDF";
        }
    }

    private async void Page_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        var pdf = items.OfType<StorageFile>().FirstOrDefault(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase));
        if (pdf is not null) App.MainWindow!.OpenInViewer(pdf.Path);
        else ShowNotice(InfoBarSeverity.Warning, "Arquivo não suportado", "Solte um arquivo .pdf.");
    }
}
