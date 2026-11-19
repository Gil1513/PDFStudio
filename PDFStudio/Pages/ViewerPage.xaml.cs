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
    private bool _updatingPageBox, _updatingThumbSelection;
    private Action? _statusAction;

    /// <summary>Um botão da barra de ferramentas. Key nulo = ainda não disponível.</summary>
    private sealed record ToolCommand(string Glyph, string Label, string? Key = null, bool NeedsFile = false);

    private static readonly ToolCommand Sep = new("|", "");

    /// <summary>Categorias do trilho esquerdo e as ferramentas de cada uma, agrupadas por separadores.</summary>
    public static IReadOnlyList<ViewerCategory> Categories { get; } =
    [
        new("inicio", "\uE80F", "Início"),
        new("comentario", "\uE90A", "Comentário"),
        new("editar", "\uE70F", "Editar"),
        new("converter", "\uE8AB", "Converter"),
        new("ver", "\uE7B3", "Ver"),
        new("organizar", "\uF0E2", "Organizar"),
        new("ferramentas", "\uE90F", "Ferramentas"),
        new("formulario", "\uE8A5", "Formulário"),
        new("proteger", "\uE72E", "Proteger"),
        new("ia", "\uE945", "IA"),
    ];

    private static readonly Dictionary<string, ToolCommand[]> CategoryTools = new()
    {
        ["inicio"] =
        [
            new("\uE8E5", "Abrir", "abrir"), new("\uE792", "Salvar cópia", "salvarcopia", NeedsFile: true), Sep,
            new("\uE70F", "Editar tudo"), new("\uE8D2", "Adicionar texto", "adicionartexto", NeedsFile: true), new("\uE8E9", "OCR"), new("\uE7A8", "Recortar", "recortar", NeedsFile: true), Sep,
            new("\uE8C8", "Combinar arquivos", "combinar"), new("\uE73F", "Comprimir PDF"), Sep,
            new("\uEE56", "Assinar", "assinar", NeedsFile: true),
        ],
        ["comentario"] =
        [
            new("\uE7E6", "Realçar", "realcar", NeedsFile: true), new("\uE8DC", "Sublinhar", "sublinhar", NeedsFile: true), new("\uEDE0", "Riscar", "riscar", NeedsFile: true), Sep,
            new("\uE8D2", "Caixa de texto", "texto", NeedsFile: true), new("\uE70B", "Nota", "nota", NeedsFile: true), Sep,
            new("\uE7AD", "Linha", "linha", NeedsFile: true), new("\uE72A", "Seta", "seta", NeedsFile: true), new("\uE739", "Retângulo", "retangulo", NeedsFile: true), new("\uEA3A", "Oval", "oval", NeedsFile: true), new("\uEE56", "Desenho livre", "desenho", NeedsFile: true), Sep,
            new("\uE8EC", "Carimbo", "carimbo", NeedsFile: true),
        ],
        ["editar"] =
        [
            new("\uE70F", "Editar tudo"), new("\uE8D2", "Adicionar texto", "adicionartexto", NeedsFile: true), new("\uEB9F", "Imagem", "imagem", NeedsFile: true), new("\uE71B", "Link", "link", NeedsFile: true), Sep,
            new("\uE790", "Marca d'água", "marcadagua", NeedsFile: true), new("\uE8EF", "Número de página", "numeropagina", NeedsFile: true), new("\uE8A5", "Cabeçalho e rodapé", "cabecalho", NeedsFile: true), new("\uE771", "Fundo", "fundo", NeedsFile: true),
        ],
        ["converter"] =
        [
            new("\uE8A5", "Para Word"), new("\uEB9F", "Para imagem"), new("\uE8C4", "Para texto"), Sep,
            new("\uE91B", "Imagem para PDF"), new("\uE8E9", "OCR"),
        ],
        ["ver"] =
        [
            new("\uE9A6", "Ajustar à largura", "fitwidth", NeedsFile: true), new("\uE7F4", "Página inteira", "fitpage", NeedsFile: true),
            new("\uE8B3", "Tamanho real", "actualsize", NeedsFile: true), Sep,
            new("\uE8A9", "Miniaturas", "miniaturas", NeedsFile: true), new("\uE740", "Tela cheia", "telacheia"),
        ],
        ["organizar"] =
        [
            new("\uF0E2", "Organizar páginas", "organizar", NeedsFile: true), Sep,
            new("\uE710", "Inserir", "inserir", NeedsFile: true), new("\uEDE1", "Extrair", "extrair", NeedsFile: true), new("\uE74D", "Excluir", "excluir", NeedsFile: true), new("\uE7A8", "Recortar", "recortar", NeedsFile: true), new("\uE740", "Redimensionar", "redimensionar", NeedsFile: true), Sep,
            new("\uE8C6", "Dividir", "dividir"), new("\uE8C8", "Combinar", "combinar"),
        ],
        ["ferramentas"] =
        [
            new("\uE8E9", "OCR"), new("\uE73F", "Comprimir"), Sep,
            new("\uE8C8", "Combinar", "combinar"), new("\uE8C6", "Dividir", "dividir"), Sep,
            new("\uE8B9", "PDFs em lote"), new("\uE749", "Impressão em lote"), new("\uE767", "Ler em voz alta"),
        ],
        ["formulario"] =
        [
            new("\uE70B", "Preencher"), Sep,
            new("\uE8D2", "Campo de texto"), new("\uE73A", "Caixa de seleção"), new("\uECCB", "Botão de opção"), new("\uE8EF", "Lista"), Sep,
            new("\uE81E", "Achatar"),
        ],
        ["proteger"] =
        [
            new("\uEE56", "Assinar digitalmente", "assinar", NeedsFile: true), Sep,
            new("\uE72E", "Colocar senha"), new("\uE785", "Remover senha"), Sep,
            new("\uE7B3", "Redigir"), new("\uE81E", "Achatar"),
        ],
        ["ia"] =
        [
            new("\uE8C1", "Traduzir PDF"), new("\uE8BD", "Resumir"), new("\uE9D5", "Revisar contrato"),
        ],
    };

    public ViewerPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // mantém o PDF aberto ao ir para outra tela
        CategoryList.ItemsSource = Categories;
        CategoryList.SelectedIndex = 0;
        InitCommentOptions();
        InitCommentsPanel();
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
        // Trocar de arquivo descarta a cópia de trabalho: pergunta antes se houver alterações.
        if (!await ConfirmDiscardAsync()) return;
        SetTool(null, keepPendingInk: false);
        DiscardWork();

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

            PageCountText.Text = $"de {_slots.Count}";
            PageNumberBox.Maximum = Math.Max(1, _slots.Count);
            App.MainWindow!.SetSubtitle(Path.GetFileName(path));
            SetPageBox(1);
            SetDocumentState(true);
            UpdateEditState();

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

    /// <summary>Nome do arquivo aberto (para a barra de título).</summary>
    public string? FileName => _path is null ? null : Path.GetFileName(_path) + (HasUnsavedChanges ? " • não salvo" : "");

    private void SetDocumentState(bool open)
    {
        EmptyState.Visibility = open ? Visibility.Collapsed : Visibility.Visible;
        Scroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        QuickSaveCopyButton.IsEnabled = QuickPropertiesButton.IsEnabled = QuickFolderButton.IsEnabled = SignaturesButton.IsEnabled = open;
        PageNav.IsHitTestVisible = open;
        PageNav.Opacity = open ? 1 : 0.5;
        if (!open)
        {
            PageCountText.Text = "0";
            ThumbnailsToggle.IsChecked = false;
            CommentsToggle.IsChecked = false;
            AttachmentsToggle.IsChecked = false;
            SidePanelColumn.Width = new GridLength(0);
            App.MainWindow!.SetSubtitle("Visualizar");
        }
        BuildToolbar(SelectedCategory);
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

    private string SelectedCategory => (CategoryList.SelectedItem as ViewerCategory)?.Key ?? "inicio";

    private void CategoryList_SelectionChanged(object sender, SelectionChangedEventArgs e) => BuildToolbar(SelectedCategory);

    private void BuildToolbar(string category)
    {
        ToolbarPanel.Children.Clear();
        foreach (var cmd in CategoryTools[category])
        {
            if (ReferenceEquals(cmd, Sep))
            {
                ToolbarPanel.Children.Add(new Microsoft.UI.Xaml.Shapes.Rectangle
                {
                    Width = 1,
                    Height = 20,
                    Margin = new Thickness(6, 0, 6, 0),
                    Fill = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(70, 128, 128, 128)),
                });
                continue;
            }

            bool available = cmd.Key is not null;
            var button = new Button
            {
                Style = (Style)Resources["ToolbarButtonStyle"],
                Tag = cmd,
                IsEnabled = !cmd.NeedsFile || _path is not null,
                Content = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Opacity = available ? 1 : 0.45,
                    Children =
                    {
                        new FontIcon { Glyph = cmd.Glyph, FontSize = 16 },
                        new TextBlock { Text = cmd.Label, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
            };
            if (cmd.Key is not null && cmd.Key == _tool?.Key)
            {
                // Ferramenta de comentário ativa fica destacada.
                button.Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
                button.BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
                button.BorderThickness = new Thickness(1);
            }
            ToolTipService.SetToolTip(button, available ? cmd.Label : $"{cmd.Label} (em breve)");
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, cmd.Label);
            button.Click += ToolButton_Click;
            ToolbarPanel.Children.Add(button);
        }
    }

    private async void ToolButton_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ToolCommand cmd) return;
        if (cmd.Key is null)
        {
            ShowStatus(InfoBarSeverity.Informational, $"{cmd.Label}: em breve", "Esta ferramenta chega nas próximas etapas do PDF Studio.");
            return;
        }
        await RunCommandAsync(cmd.Key);
    }

    private async Task RunCommandAsync(string key)
    {
        // Ferramentas de comentário: liga/desliga o modo de desenho.
        if (CommentTools.ContainsKey(key))
        {
            // Imagem: primeiro escolhe o arquivo; sem arquivo, a ferramenta não liga.
            if (key == "imagem" && _tool?.Key != key && !await ChooseImageAsync()) return;
            SetTool(_tool?.Key == key ? null : key);
            return;
        }
        // Ferramentas que trabalham direto no arquivo precisam das alterações salvas.
        if (key is "excluir" or "extrair" or "inserir" or "recortar" or "redimensionar" or "organizar" or "assinar"
            && !await EnsureSavedAsync()) return;

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
            case "telacheia": ToggleFullScreen(); break;
            case "excluir": await DeletePagesAsync(); break;
            case "extrair": await ExtractPagesAsync(); break;
            case "inserir": await InsertPagesAsync(); break;
            case "recortar": await CropPagesAsync(); break;
            case "redimensionar": await ResizePagesAsync(); break;
            case "marcadagua": await WatermarkAsync(); break;
            case "numeropagina": await HeaderFooterAsync(true); break;
            case "cabecalho": await HeaderFooterAsync(false); break;
            case "fundo": await BackgroundAsync(); break;
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
            string source = DisplayPath!; // inclui as alterações ainda não salvas
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
        if (_path is null || !await EnsureSavedAsync()) return;
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

    private async void CloseFile_Click(object sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardAsync()) return;
        SetTool(null, keepPendingInk: false);
        DiscardWork();
        _document = null;
        _path = null;
        _slots = [];
        PagesRepeater.ItemsSource = null;
        ThumbnailList.ItemsSource = null;
        StatusBar.IsOpen = false;
        SetDocumentState(false);
        App.MainWindow!.GoHome();
    }

    private void GoHome_Click(object sender, RoutedEventArgs e) => App.MainWindow!.GoHome();

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

    private void ThumbnailsToggle_Click(object sender, RoutedEventArgs e)
    {
        if (ThumbnailsToggle.IsChecked == true) CommentsToggle.IsChecked = AttachmentsToggle.IsChecked = false; // um painel por vez
        UpdateSidePanel();
    }

    private void ClosePanel_Click(object sender, RoutedEventArgs e)
    {
        ThumbnailsToggle.IsChecked = false;
        CommentsToggle.IsChecked = false;
        AttachmentsToggle.IsChecked = false;
        UpdateSidePanel();
    }

    /// <summary>Mostra o painel lateral escolhido no trilho (miniaturas ou comentários), ou o esconde.</summary>
    private void UpdateSidePanel()
    {
        if (_path is null) { ThumbnailsToggle.IsChecked = false; CommentsToggle.IsChecked = false; AttachmentsToggle.IsChecked = false; }
        bool thumbs = ThumbnailsToggle.IsChecked == true, comments = CommentsToggle.IsChecked == true;
        bool attachments = AttachmentsToggle.IsChecked == true && !comments;
        SidePanelColumn.Width = new GridLength(comments || attachments ? 300 : thumbs ? 196 : 0);
        SidePanelTitle.Text = comments ? "Comentários" : attachments ? "Anexos" : "Páginas";
        ThumbnailList.Visibility = comments || attachments ? Visibility.Collapsed : Visibility.Visible;
        CommentsPanel.Visibility = comments ? Visibility.Visible : Visibility.Collapsed;
        AttachmentsPanel.Visibility = attachments ? Visibility.Visible : Visibility.Collapsed;
        if (attachments) _ = RefreshAttachmentListAsync();
        if (thumbs) SyncThumbnailSelection((int)Math.Max(1, PageNumberBox.Value) - 1);
        if (comments) _ = RefreshCommentListAsync();
    }

    private async void ThumbnailList_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
    {
        if (args.InRecycleQueue || args.Item is not PageSlot slot || slot.ThumbnailRequested || _document is null) return;
        slot.ThumbnailRequested = true;
        var doc = _document;
        try
        {
            var image = await PdfRenderer.RenderAsync(doc, slot.Index, ThumbnailScale(slot));
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

    private void UpdateZoomDisplay(float zoom) => ZoomText.Text = $"{Math.Round(zoom * 100)}%";

    private void ZoomPreset_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is string tag && int.TryParse(tag, out int percent)) SetZoom(percent / 100f);
    }

    // =====================================================================
    // Tela cheia
    // =====================================================================

    private void ToggleFullScreen_Click(object sender, RoutedEventArgs e) => ToggleFullScreen();

    private static void ToggleFullScreen()
    {
        var window = App.MainWindow!.AppWindow;
        window.SetPresenter(window.Presenter.Kind == Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen
            ? Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped
            : Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen);
    }

    private void Accel_FullScreen(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a) { a.Handled = true; ToggleFullScreen(); }

    private void Accel_ExitFullScreen(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        if (_textEditor is not null) // Esc na caixa de texto: conclui o texto (a ferramenta continua ativa)
        {
            a.Handled = true;
            _ = FinishTextEditorAsync(commit: true);
            return;
        }
        if (_tool is not null) // Esc primeiro sai da ferramenta de comentário
        {
            a.Handled = true;
            SetTool(null);
            return;
        }
        var window = App.MainWindow!.AppWindow;
        if (window.Presenter.Kind != Microsoft.UI.Windowing.AppWindowPresenterKind.FullScreen) return;
        a.Handled = true;
        window.SetPresenter(Microsoft.UI.Windowing.AppWindowPresenterKind.Overlapped);
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
    private void PrevPage_Click(object sender, RoutedEventArgs e) => GoToPage(CurrentIndex - 1);
    private void NextPage_Click(object sender, RoutedEventArgs e) => GoToPage(CurrentIndex + 1);

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

/// <summary>Uma categoria do trilho esquerdo do visualizador.</summary>
public sealed class ViewerCategory(string key, string glyph, string label)
{
    public string Key { get; } = key;
    public string Glyph { get; } = glyph;
    public string Label { get; } = label;
    public override string ToString() => Label;
}
