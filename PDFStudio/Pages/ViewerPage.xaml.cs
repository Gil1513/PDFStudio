using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using WinPdfDocument = Windows.Data.Pdf.PdfDocument;

namespace PDFStudio.Pages;

/// <summary>Função 1: abrir e visualizar um PDF.</summary>
public sealed partial class ViewerPage : Page
{
    private const double PageSpacing = 16;   // igual ao StackLayout.Spacing
    private const double ContentMargin = 24; // igual ao Margin do ItemsRepeater
    private const double RenderScale = 2.0;
    private const int WrongPasswordHResult = unchecked((int)0x8007052B);

    private WinPdfDocument? _document;
    private List<PageSlot> _slots = [];
    private bool _updatingPageBox;

    public ViewerPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required; // mantém o PDF aberto ao trocar de página no menu
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        if (e.Parameter is string path && !string.IsNullOrEmpty(path))
            await OpenAsync(path);
    }

    /// <summary>Abre um PDF e prepara as páginas (a renderização ocorre sob demanda).</summary>
    public async Task OpenAsync(string path)
    {
        LoadingBar.Visibility = Visibility.Visible;
        StatusBar.IsOpen = false;
        try
        {
            WinPdfDocument? doc = null;
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
            _slots = PdfRenderer.CreateSlots(doc);
            PagesRepeater.ItemsSource = _slots;

            FileNameText.Text = Path.GetFileName(path);
            ToolTipService.SetToolTip(FileNameText, path);
            PageCountText.Text = $"de {_slots.Count}";
            PageNumberBox.Maximum = Math.Max(1, _slots.Count);
            SetPageBox(1);

            EmptyState.Visibility = Visibility.Collapsed;
            Scroller.Visibility = Visibility.Visible;
            ZoomInButton.IsEnabled = ZoomOutButton.IsEnabled = FitWidthButton.IsEnabled = PageNumberBox.IsEnabled = true;

            // Ajusta à largura depois que o layout estiver pronto (o ScrollViewer acabou de ficar visível).
            Scroller.ChangeView(0, 0, null, true);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => FitWidth(disableAnimation: true));
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível abrir o PDF", ex.HResult == WrongPasswordHResult ? "Senha incorreta." : ex.Message);
        }
        finally
        {
            LoadingBar.Visibility = Visibility.Collapsed;
        }
    }

    private async Task<string?> AskPasswordAsync(string fileName)
    {
        var box = new PasswordBox { PlaceholderText = "Senha" };
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
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

    // ---------- Renderização sob demanda ----------

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

    // ---------- Comandos ----------

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is not null) await OpenAsync(path);
    }

    private void ZoomIn_Click(object sender, RoutedEventArgs e) => SetZoom(Scroller.ZoomFactor * 1.25f);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => SetZoom(Scroller.ZoomFactor / 1.25f);
    private void FitWidth_Click(object sender, RoutedEventArgs e) => FitWidth(false);

    private void FitWidth(bool disableAnimation)
    {
        if (_slots.Count == 0 || Scroller.ViewportWidth <= 0) return;
        double widest = _slots.Max(s => s.Width) + 2 * ContentMargin + 2;
        SetZoom((float)(Scroller.ViewportWidth / widest), disableAnimation);
    }

    private void SetZoom(float zoom, bool disableAnimation = false)
    {
        zoom = Math.Clamp(zoom, Scroller.MinZoomFactor, Scroller.MaxZoomFactor);
        Scroller.ChangeView(null, null, zoom, disableAnimation);
        ZoomText.Text = $"{Math.Round(zoom * 100)}%";
    }

    // ---------- Página atual ----------

    /// <summary>Posição vertical (sem zoom) do topo de cada página.</summary>
    private double PageTop(int index)
    {
        double y = ContentMargin;
        for (int i = 0; i < index; i++) y += _slots[i].Height + 2 + PageSpacing; // +2 = borda
        return y;
    }

    private void Scroller_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        ZoomText.Text = $"{Math.Round(Scroller.ZoomFactor * 100)}%";
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
        SetPageBox(current);
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
        int index = Math.Clamp((int)args.NewValue, 1, _slots.Count) - 1;
        Scroller.ChangeView(null, PageTop(index) * Scroller.ZoomFactor, null);
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
        if (pdf is not null) await OpenAsync(pdf.Path);
        else ShowError("Arquivo não suportado", "Solte um arquivo .pdf.");
    }

    private void ShowError(string title, string message)
    {
        StatusBar.Severity = InfoBarSeverity.Error;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }
}
