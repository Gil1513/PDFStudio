using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Core;
using PDFStudio.Dialogs;
using PDFStudio.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace PDFStudio.Pages;

/// <summary>
/// Tela do documento aberto: menu Arquivo, abas de ferramentas, miniaturas das páginas,
/// área de leitura e barra inferior com navegação e zoom.
/// </summary>
public sealed partial class ViewerPage : Page
{
    private const double PageSpacing = 16;   // igual ao StackLayout.Spacing
    private const double ContentMargin = 24; // igual ao Margin do ItemsRepeater
    private const double RenderScale = 2.0;
    private const int WrongPasswordHResult = unchecked((int)0x8007052B);

    private WinPdfDocument? _document;
    private string? _path;
    private List<PageSlot> _slots = [];
    private bool _updatingPageBox, _updatingZoomSlider, _updatingThumbSelection;
    private Action? _statusAction;

    /// <summary>Um botão da faixa de ferramentas. Key nulo = ainda não disponível.</summary>
    private sealed record RibbonCommand(string Glyph, string Label, string? Key = null, bool NeedsFile = false);

    private static readonly Dictionary<string, RibbonCommand[]> RibbonTabsContent = new()
    {
        ["inicio"] =
        [
            new("", "Abrir", "abrir"),
            new("", "Salvar cópia", "salvarcopia", NeedsFile: true),
            new("", "Imprimir"),
            new("|", ""),
            new("", "Diminuir", "zoomout", NeedsFile: true),
            new("", "Aumentar", "zoomin", NeedsFile: true),
            new("", "Largura", "fitwidth", NeedsFile: true),
            new("", "Página inteira", "fitpage", NeedsFile: true),
            new("", "Tamanho real", "actualsize", NeedsFile: true),
            new("|", ""),
            new("", "Miniaturas", "miniaturas", NeedsFile: true),
            new("", "Localizar"),
            new("", "Propriedades", "propriedades", NeedsFile: true),
        ],
        ["comentario"] =
        [
            new("", "Realçar"), new("", "Sublinhar"), new("", "Riscar"), new("|", ""),
            new("", "Linha"), new("", "Retângulo"), new("", "Oval"), new("", "Tinta"), new("|", ""),
            new("", "Caixa de texto"), new("", "Nota"), new("", "Carimbo"),
        ],
        ["editar"] =
        [
            new("", "Editar texto"), new("", "Editar objeto"), new("", "Imagem"), new("", "Link"),
            new("", "Anexo"), new("|", ""), new("", "Marca d'água"), new("", "Nº de página"), new("", "Cabeçalho e rodapé"),
        ],
        ["preencher"] =
        [
            new("", "Máquina de escrever"), new("", "Marcar ✓"), new("", "Marcar ✗"), new("|", ""),
            new("", "Assinatura digital", "assinar", NeedsFile: true), new("", "Achatar"),
        ],
        ["pagina"] =
        [
            new("", "Organizar páginas", "organizar", NeedsFile: true), new("", "Dividir", "dividir"), new("", "Combinar", "combinar"),
            new("|", ""), new("", "Inserir"), new("", "Extrair"), new("", "Excluir"), new("", "Recortar"), new("", "Redimensionar"),
        ],
        ["proteger"] =
        [
            new("", "Senha"), new("", "Remover senha"), new("|", ""),
            new("", "Assinar", "assinar", NeedsFile: true), new("", "Redigir"), new("", "Achatar"),
        ],
        ["ferramentas"] =
        [
            new("", "Converter"), new("", "OCR"), new("", "Comprimir"), new("|", ""),
            new("", "Combinar", "combinar"), new("", "Dividir", "dividir"), new("", "Impressão em lote"), new("|", ""),
            new("", "Ler em voz alta"), new("", "Traduzir"),
        ],
    };

    public ViewerPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // mantém o PDF aberto ao ir para outra tela
        BuildRibbon("inicio");
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path && !string.IsNullOrEmpty(path) && !string.Equals(path, _path, StringComparison.OrdinalIgnoreCase))
            await OpenAsync(path);
    }

    // =====================================================================
    // Abrir
    // =====================================================================

    /// <summary>Abre um PDF e prepara as páginas (a renderização ocorre sob demanda).</summary>
    public async Task OpenAsync(string path)
    {
        LoadingBar.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        try
        {
            WinPdfDocument? doc;
            try
            {
                doc = await PdfRenderer.LoadAsync(path);
            }
            catch (Exception ex) when (ex.HResult == WrongPasswordHResult)
            {
                string? password = await AskPasswordAsync(Path.GetFileName(path));
                if (password is null) return;
                doc = await PdfRenderer.LoadAsync(path, password);
            }

            _document = doc;
            _path = path;
            App.Recent.Add(path);
            _slots = PdfRenderer.CreateSlots(doc);
            PagesRepeater.ItemsSource = _slots;
            ThumbnailList.ItemsSource = _slots;

            FileNameText.Text = Path.GetFileName(path);
            ToolTipService.SetToolTip(FileNameText, path);
            PageCountText.Text = $"/ {_slots.Count}";
            PageNumberBox.Maximum = Math.Max(1, _slots.Count);
            DocInfoText.Text = $"{_slots.Count} {(_slots.Count == 1 ? "página" : "páginas")} · {DocumentPropertiesService.FormatSize(new FileInfo(path).Length)}";
            SetPageBox(1);
            SetDocumentState(true);

            // Ajusta à largura depois que o layout estiver pronto (o ScrollViewer acabou de ficar visível).
            Scroller.ChangeView(0, 0, null, true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FitWidth(disableAnimation: true));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível abrir o PDF", ex.HResult == WrongPasswordHResult ? "Senha incorreta." : ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private void SetDocumentState(bool open)
    {
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Scroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        QuickSaveCopyButton.IsEnabled = QuickPropertiesButton.IsEnabled = open;
        PageNav.IsHitTestVisible = ZoomBar.IsHitTestVisible = open;
        PageNav.Opacity = ZoomBar.Opacity = open ? 1 : 0.5;
        if (!open)
        {
            FileNameText.Text = "Nenhum arquivo aberto";
            DocInfoText.Text = "";
            PageCountText.Text = "/ 0";
            ThumbnailsToggle.IsChecked = false;
            SidePanelColumn.Width = new GridLength(0);
        }
        BuildRibbon(SelectedTab);
    }

    private async Task<string?> AskPasswordAsync(string fileName)
    {
        var box = new PasswordBox { PlaceholderText = "Senha" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            RequestedTheme = App.Settings.Theme,
            Title = "PDF protegido por senha",
            Content = new StackPanel
            {
                Spacing = 12,
                Children = { new TextBlock { Text = $"Digite a senha para abrir \"{fileName}\".", TextWrapping = TextWrapping.Wrap }, box },
            },
            PrimaryButtonText = "Abrir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    // =====================================================================
    // Faixa de ferramentas (abas)
    // =====================================================================

    private string SelectedTab => (RibbonTabs.SelectedItem?.Tag as string) ?? "inicio";

    private void RibbonTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args) => BuildRibbon(SelectedTab);

    private void BuildRibbon(string tab)
    {
        RibbonPanel.Children.Clear();
        foreach (var cmd in RibbonTabsContent[tab])
        {
            if (cmd.Glyph == "|")
            {
                RibbonPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 1,
                    Margin = new Thickness(8, 10, 8, 10),
                    Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(70, 128, 128, 128)),
                });
                continue;
            }

            bool available = cmd.Key is not null;
            bool enabled = !cmd.NeedsFile || _path is not null;
            var button = new Button
            {
                Style = (Style)Resources["RibbonButtonStyle"],
                Tag = cmd,
                IsEnabled = enabled,
                Content = new StackPanel
                {
                    Spacing = 4,
                    Opacity = available ? 1 : 0.5,
                    Children =
                    {
                        new FontIcon { Glyph = cmd.Glyph, FontSize = 20 },
                        new TextBlock { Text = cmd.Label, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center },
                    },
                },
            };
            ToolTipService.SetToolTip(button, available ? cmd.Label : $"{cmd.Label} (em breve)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, cmd.Label);
            button.Click += RibbonButton_Click;
            RibbonPanel.Children.Add(button);
        }
    }

    private async void RibbonButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not RibbonCommand cmd) return;
        if (cmd.Key is null)
        {
            ShowStatus(InfoBarSeverity.Informational, $"{cmd.Label}: em breve", "Esta ferramenta chega nas próximas etapas do PDF Studio.");
            return;
        }
        await RunCommandAsync(cmd.Key);
    }

    private async Task RunCommandAsync(string key)
    {
        switch (key)
        {
            case "abrir": await PickAndOpenAsync(); break;
            case "salvarcopia": await SaveCopyAsync(); break;
            case "zoomin": SetZoom(Scroller.ZoomFactor * 1.25f); break;
            case "zoomout": SetZoom(Scroller.ZoomFactor / 1.25f); break;
            case "fitwidth": FitWidth(false); break;
            case "fitpage": FitPage(); break;
            case "actualsize": SetZoom(1f); break;
            case "miniaturas":
                ThumbnailsToggle.IsChecked = ThumbnailsToggle.IsChecked != true;
                UpdateSidePanel();
                break;
            case "propriedades": await ShowPropertiesAsync(); break;
            // Ferramentas que trabalham com o arquivo aberto recebem o caminho dele.
            case "organizar" or "assinar": App.MainWindow!.OpenTool(key, _path); break;
            default: App.MainWindow!.OpenTool(key); break;
        }
    }

    // =====================================================================
    // Menu Arquivo
    // =====================================================================

    private void FileMenu_Opening(object sender, object e)
    {
        bool open = _path is not null;
        SaveCopyMenuItem.IsEnabled = PropertiesMenuItem.IsEnabled = ShowInFolderMenuItem.IsEnabled = open;

        RecentSubMenu.Items.Clear();
        var recent = App.Recent.Entries.Where(r => File.Exists(r.Path)).Take(10).ToList();
        if (recent.Count == 0)
            RecentSubMenu.Items.Add(new MenuFlyoutItem { Text = "Nenhum arquivo recente", IsEnabled = false });
        foreach (var entry in recent)
        {
            var item = new MenuFlyoutItem { Text = Path.GetFileName(entry.Path), Tag = entry.Path };
            ToolTipService.SetToolTip(item, entry.Path);
            item.Click += async (s, _) => await OpenAsync((string)((FrameworkElement)s).Tag);
            RecentSubMenu.Items.Add(item);
        }
    }

    private async void Open_Click(object sender, RoutedEventArgs e) => await PickAndOpenAsync();

    private async Task PickAndOpenAsync()
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is not null) await OpenAsync(path);
    }

    private async void SaveCopy_Click(object sender, RoutedEventArgs e) => await SaveCopyAsync();

    /// <summary>Grava uma cópia idêntica do arquivo em outro lugar.</summary>
    private async Task SaveCopyAsync()
    {
        if (_path is null) return;
        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_path) + " - cópia");
        if (output is null) return;
        if (string.Equals(Path.GetFullPath(output), Path.GetFullPath(_path), StringComparison.OrdinalIgnoreCase))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escolha outro nome", "A cópia não pode ter o mesmo nome e pasta do arquivo aberto.");
            return;
        }
        try
        {
            string source = _path;
            await Task.Run(() => File.Copy(source, output, overwrite: true));
            ShowStatus(InfoBarSeverity.Success, "Cópia salva", output, "Abrir a cópia", async () => await OpenAsync(output));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar a cópia", ex.Message);
        }
    }

    private void MenuTool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string key) return;
        App.MainWindow!.OpenTool(key, key is "organizar" or "assinar" ? _path : null);
    }

    private async void Properties_Click(object sender, RoutedEventArgs e) => await ShowPropertiesAsync();

    private async Task ShowPropertiesAsync()
    {
        if (_path is null) return;
        string path = _path;
        try
        {
            var props = await Task.Run(() => DocumentPropertiesService.Read(path));
            var dialog = new DocumentPropertiesDialog(props) { XamlRoot = XamlRoot, RequestedTheme = App.Settings.Theme };
            await dialog.ShowAsync();
            if (dialog.Saved)
            {
                _path = null; // força recarregar
                await OpenAsync(path);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível ler as propriedades", ex.Message);
        }
    }

    private void ShowInFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_path is not null)
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_path}\"") { UseShellExecute = true });
    }

    private void CloseFile_Click(object sender, RoutedEventArgs e)
    {
        _document = null;
        _path = null;
        _slots = [];
        PagesRepeater.ItemsSource = null;
        ThumbnailList.ItemsSource = null;
        StatusBar.IsOpen = false;
        SetDocumentState(false);
        App.MainWindow!.GoHome();
    }

    private void Soon_Click(object sender, RoutedEventArgs e)
    {
        string name = (sender as FrameworkElement)?.Tag as string ?? "Esta função";
        ShowStatus(InfoBarSeverity.Informational, $"{name}: em breve", "Esta função chega nas próximas etapas do PDF Studio.");
    }

    // ---------- Atalhos ----------

    private async void Accel_Open(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; await PickAndOpenAsync(); }
    private async void Accel_Properties(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; await ShowPropertiesAsync(); }
    private async void Accel_SaveCopy(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; await SaveCopyAsync(); }
    private void Accel_ZoomIn(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; if (_path is not null) SetZoom(Scroller.ZoomFactor * 1.25f); }
    private void Accel_ZoomOut(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; if (_path is not null) SetZoom(Scroller.ZoomFactor / 1.25f); }
    private void Accel_FitWidth(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; FitWidth(false); }

    // =====================================================================
    // Renderização sob demanda
    // =====================================================================

    private async void PagesRepeater_ElementPrepared(ItemsRepeater sender, ItemsRepeaterElementPreparedEventArgs args)
    {
        if (_document is null || args.Index >= _slots.Count) return;
        var slot = _slots[args.Index];
        if (slot.Image is not null || slot.IsRendering) return;

        var doc = _document;
        slot.IsRendering = true;
        try
        {
            var image = await PdfRenderer.RenderAsync(doc, slot.Index, RenderScale);
            if (doc == _document) slot.Image = image; // ignora se outro PDF foi aberto no meio
        }
        catch
        {
            // Página com erro fica em branco; o restante do documento continua utilizável.
        }
        finally
        {
            slot.IsRendering = false;
        }
    }

    private void PagesRepeater_ElementClearing(ItemsRepeater sender, ItemsRepeaterElementClearingEventArgs args)
    {
        // Libera a imagem quando a página sai da área visível.
        int index = sender.GetElementIndex(args.Element);
        if (index >= 0 && index < _slots.Count) _slots[index].Image = null;
    }

    // =====================================================================
    // Miniaturas
    // =====================================================================

    private void ThumbnailsToggle_Click(object sender, RoutedEventArgs e) => UpdateSidePanel();

    private void UpdateSidePanel()
    {
        bool show = ThumbnailsToggle.IsChecked == true && _path is not null;
        if (ThumbnailsToggle.IsChecked == true && _path is null) ThumbnailsToggle.IsChecked = false;
        SidePanelColumn.Width = new GridLength(show ? 180 : 0);
        if (show) SyncThumbnailSelection((int)Math.Max(1, PageNumberBox.Value) - 1);
    }

    private async void ThumbnailList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not PageSlot slot || slot.ThumbnailRequested || _document is null) return;
        slot.ThumbnailRequested = true;
        var doc = _document;
        try
        {
            double scale = PageSlot.ThumbMaxWidth * 1.5 / Math.Max(slot.Width, slot.Height);
            var image = await PdfRenderer.RenderAsync(doc, slot.Index, scale);
            if (doc == _document) slot.Thumbnail = image;
        }
        catch
        {
            slot.ThumbnailRequested = false;
        }
    }

    private void ThumbnailList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PageSlot slot) GoToPage(slot.Index);
    }

    private void SyncThumbnailSelection(int index)
    {
        if (SidePanelColumn.Width.Value == 0 || index < 0 || index >= _slots.Count) return;
        _updatingThumbSelection = true;
        ThumbnailList.SelectedIndex = index;
        ThumbnailList.ScrollIntoView(_slots[index]);
        _updatingThumbSelection = false;
    }

    // =====================================================================
    // Zoom
    // =====================================================================

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Scroller.ZoomFactor * 1.25f);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Scroller.ZoomFactor / 1.25f);
    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth(false);
    private void FitPage_Click(object sender, RoutedEventArgs e) => FitPage();

    private void FitWidth(bool disableAnimation)
    {
        if (_slots.Count == 0 || Scroller.ViewportWidth <= 0) return;
        double widest = _slots.Max(s => s.Width) + 2 * ContentMargin + 2;
        SetZoom((float)(Scroller.ViewportWidth / widest), disableAnimation);
    }

    /// <summary>Zoom para a página atual caber inteira na tela.</summary>
    private void FitPage()
    {
        if (_slots.Count == 0 || Scroller.ViewportWidth <= 0) return;
        int index = Math.Clamp((int)PageNumberBox.Value - 1, 0, _slots.Count - 1);
        var slot = _slots[index];
        float zoom = (float)Math.Min(Scroller.ViewportWidth / (slot.Width + 2 * ContentMargin + 2),
                                     Scroller.ViewportHeight / (slot.Height + 2 * PageSpacing + 2));
        zoom = Math.Clamp(zoom, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        Scroller.ChangeView(null, Math.Max(0, (PageTop(index) - PageSpacing) * zoom), zoom, true);
        UpdateZoomDisplay(zoom);
    }

    private void SetZoom(float zoom, bool disableAnimation = false)
    {
        zoom = Math.Clamp(zoom, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        Scroller.ChangeView(null, null, zoom, disableAnimation);
        UpdateZoomDisplay(zoom);
    }

    private void UpdateZoomDisplay(float zoom)
    {
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";
        _updatingZoomSlider = true;
        ZoomSlider.Value = Math.Clamp(zoom * 100, ZoomSlider.Minimum, ZoomSlider.Maximum);
        _updatingZoomSlider = false;
    }

    private void ZoomSlider_ValueChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (_updatingZoomSlider || _slots.Count == 0 || Scroller is null) return;
        SetZoom((float)(e.NewValue / 100), disableAnimation: true);
    }

    // =====================================================================
    // Página atual e navegação
    // =====================================================================

    /// <summary>Posição vertical (sem zoom) do topo de cada página.</summary>
    private double PageTop(int index)
    {
        double y = ContentMargin;
        for (int i = 0; i < index; i++) y += _slots[i].Height + 2 + PageSpacing; // +2 = borda
        return y;
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        UpdateZoomDisplay(Scroller.ZoomFactor);
        if (_slots.Count == 0) return;

        // A página atual é a que ocupa o centro da tela.
        double center = (Scroller.VerticalOffset + Scroller.ViewportHeight / 2) / Scroller.ZoomFactor;
        double y = ContentMargin;
        int current = _slots.Count;
        for (int i = 0; i < _slots.Count; i++)
        {
            y += _slots[i].Height + 2 + PageSpacing;
            if (center < y) { current = i + 1; break; }
        }
        if ((int)PageNumberBox.Value != current)
        {
            SetPageBox(current);
            if (!e.IsIntermediate) SyncThumbnailSelection(current - 1);
        }
    }

    private void SetPageBox(int page)
    {
        _updatingPageBox = true;
        PageNumberBox.Value = page;
        _updatingPageBox = false;
    }

    private void PageNumberBox_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_updatingPageBox || _slots.Count == 0 || double.IsNaN(args.NewValue)) return;
        GoToPage(Math.Clamp((int)args.NewValue, 1, _slots.Count) - 1);
    }

    private void GoToPage(int index)
    {
        if (_slots.Count == 0) return;
        index = Math.Clamp(index, 0, _slots.Count - 1);
        Scroller.ChangeView(null, PageTop(index) * Scroller.ZoomFactor, null);
        SetPageBox(index + 1);
        if (!_updatingThumbSelection) SyncThumbnailSelection(index);
    }

    private int CurrentIndex => Math.Clamp((int)PageNumberBox.Value - 1, 0, Math.Max(0, _slots.Count - 1));
    private void FirstPage_Click(object sender, RoutedEventArgs e) => GoToPage(0);
    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(CurrentIndex - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(CurrentIndex + 1);
    private void LastPage_Click(object sender, RoutedEventArgs e) => GoToPage(_slots.Count - 1);

    // =====================================================================
    // Arrastar e soltar / mensagens
    // =====================================================================

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
        if (pdf is not null) await OpenAsync(pdf.Path);
        else ShowStatus(InfoBarSeverity.Warning, "Arquivo não suportado", "Solte um arquivo .pdf.");
    }

    private void ShowStatus(InfoBarSeverity severity, string title, string message, string? actionText = null, Action? action = null)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        _statusAction = action;
        StatusAction.Content = actionText;
        StatusAction.Visibility = action is null ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.IsOpen = true;
    }

    private void StatusAction_Click(object sender, RoutedEventArgs e) => _statusAction?.Invoke();
}
