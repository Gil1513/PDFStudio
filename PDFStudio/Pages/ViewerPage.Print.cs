using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using WinPdf = Windows.Data.Pdf;

namespace PDFStudio.Pages;

// Etapa 7.3: imprimir pela janela de impressão do Windows (impressora, cópias, páginas, papel, prévia).
// Cada página vira uma imagem (motor de PDF do Windows) posicionada no papel. Opções nossas na janela:
// "Tamanho" (ajustar à página / tamanho real) e "Comentários" (imprimir ou não).
public sealed partial class ViewerPage
{
    private const string SizeOption = "PDFStudioTamanho", CommentsOption = "PDFStudioComentarios";
    private const int PreviewDpi = 110;

    private PrintDocument? _printDoc;
    private IPrintDocumentSource? _printSource;
    private WinPdf.PdfDocument? _printPdf;          // o que vai para o papel (com ou sem comentários)
    private WinPdf.PdfDocument? _printPdfWithComments;
    private string? _printNoCommentsCopy;
    private string _printTitle = "Documento";
    private bool _printFit = true, _printComments = true;
    private List<int> _printPages = [];
    private PrintPageDescription _printPaper;
    private bool _printing;

    private async void Print_Click(object sender, RoutedEventArgs e) => await PrintAsync();
    private async void Accel_Print(Microsoft.UI.Xaml.Input.KeyboardAccelerator s, Microsoft.UI.Xaml.Input.KeyboardAcceleratorInvokedEventArgs a)
    {
        a.Handled = true;
        await PrintAsync();
    }

    private async Task PrintAsync()
    {
        if (DisplayPath is not { } path || _document is null || _printing) return;
        if (!PrintManager.IsSupported())
        {
            ShowStatus(InfoBarSeverity.Error, "Impressão indisponível", "Este Windows não oferece a janela de impressão para o app.");
            return;
        }
        if (!await Task.Run(() => PdfPasswords.AllowsPrinting(path, _password)))
        {
            ShowStatus(InfoBarSeverity.Warning, "Impressão não permitida",
                "Quem criou este PDF não permite imprimi-lo (restrição de permissões). Com a senha de permissões, use Proteger → Remover senha.");
            return;
        }
        if (_textEditor is not null) await FinishTextEditorAsync(commit: true);

        _printing = true;
        try
        {
            _printPdfWithComments = await PdfRenderer.LoadAsync(path, _password);
            _printPdf = _printPdfWithComments;
            _printTitle = Path.GetFileNameWithoutExtension(FileName ?? "Documento");
            _printFit = true;
            _printComments = true;

            _printDoc = new PrintDocument();
            _printSource = _printDoc.DocumentSource;
            _printDoc.Paginate += PrintDoc_Paginate;
            _printDoc.GetPreviewPage += PrintDoc_GetPreviewPage;
            _printDoc.AddPages += PrintDoc_AddPages;

            nint hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            var manager = PrintManagerInterop.GetForWindow(hwnd);
            manager.PrintTaskRequested += PrintManager_PrintTaskRequested;
            try
            {
                await PrintManagerInterop.ShowPrintUIForWindowAsync(hwnd);
            }
            finally
            {
                manager.PrintTaskRequested -= PrintManager_PrintTaskRequested;
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível imprimir", ex.Message);
            EndPrint();
        }
    }

    /// <summary>O Windows pede a tarefa de impressão: escolhe as opções que aparecem na janela.</summary>
    private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var source = _printSource;
        if (source is null) return;
        var task = args.Request.CreatePrintTask(_printTitle, requested => requested.SetSource(source));
        task.Options.PageRangeOptions.AllowAllPages = true;
        task.Options.PageRangeOptions.AllowCustomSetOfPages = true;
        task.Options.PageRangeOptions.AllowCurrentPage = false;

        var details = PrintTaskOptionDetails.GetFromPrintTaskOptions(task.Options);
        var shown = details.DisplayedOptions;
        shown.Clear();
        shown.Add(StandardPrintTaskOptions.Copies);
        shown.Add(StandardPrintTaskOptions.CustomPageRanges);
        shown.Add(StandardPrintTaskOptions.MediaSize);
        shown.Add(StandardPrintTaskOptions.Orientation);
        shown.Add(StandardPrintTaskOptions.ColorMode);
        shown.Add(StandardPrintTaskOptions.Duplex);

        var size = details.CreateItemListOption(SizeOption, "Tamanho");
        size.AddItem("ajustar", "Ajustar à página");
        size.AddItem("real", "Tamanho real");
        size.TrySetValue("ajustar");
        shown.Add(SizeOption);

        var comments = details.CreateItemListOption(CommentsOption, "Comentários");
        comments.AddItem("sim", "Imprimir comentários");
        comments.AddItem("nao", "Só o documento");
        comments.TrySetValue("sim");
        shown.Add(CommentsOption);

        details.OptionChanged += PrintOptions_Changed;
        task.Completed += (_, e) => DispatcherQueue.TryEnqueue(() =>
        {
            EndPrint();
            if (e.Completion == PrintTaskCompletion.Failed)
                ShowStatus(InfoBarSeverity.Error, "A impressão falhou", "O Windows não conseguiu imprimir. Confira a impressora e tente de novo.");
            else if (e.Completion == PrintTaskCompletion.Submitted)
                ShowStatus(InfoBarSeverity.Success, "Enviado para a impressora", $"“{_printTitle}” está na fila de impressão.");
        });
    }

    private void PrintOptions_Changed(PrintTaskOptionDetails sender, PrintTaskOptionChangedEventArgs args)
    {
        string id = args.OptionId?.ToString() ?? "";
        if (id != SizeOption && id != CommentsOption) return;
        string value = sender.Options[id].Value?.ToString() ?? "";
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (_printDoc is null) return;
            if (id == SizeOption) _printFit = value != "real";
            else
            {
                _printComments = value != "nao";
                try
                {
                    _printPdf = _printComments ? _printPdfWithComments : await NoCommentsDocumentAsync();
                }
                catch (Exception ex)
                {
                    ShowStatus(InfoBarSeverity.Error, "Não foi possível tirar os comentários", ex.Message);
                    _printPdf = _printPdfWithComments;
                }
            }
            _printDoc?.InvalidatePreview();
        });
    }

    /// <summary>Cópia temporária sem os comentários (feita uma vez por impressão).</summary>
    private async Task<WinPdf.PdfDocument> NoCommentsDocumentAsync()
    {
        if (_printNoCommentsCopy is null)
        {
            string folder = Path.Combine(Path.GetTempPath(), "PDFStudio", "impressao");
            Directory.CreateDirectory(folder);
            string copy = Path.Combine(folder, $"{Guid.NewGuid():N}.pdf");
            string source = DisplayPath!;
            await Task.Run(() => PdfAnnotations.CopyWithoutComments(source, copy));
            _printNoCommentsCopy = copy;
        }
        return await PdfRenderer.LoadAsync(_printNoCommentsCopy, _password);
    }

    private void PrintDoc_Paginate(object sender, PaginateEventArgs e)
    {
        if (_printPdf is not { } pdf) return;
        _printPaper = e.PrintTaskOptions.GetPageDescription(0);
        var ranges = e.PrintTaskOptions.CustomPageRanges.Select(r => (r.FirstPageNumber, r.LastPageNumber));
        _printPages = PrintLayout.Pages((int)pdf.PageCount, ranges);
        ((PrintDocument)sender).SetPreviewPageCount(Math.Max(1, _printPages.Count), PreviewPageCountType.Final);
    }

    // A prévia só mostra imagens que já estão prontas quando a página é entregue. Por isso a primeira vez
    // entrega a folha vazia, desenha a página e pede ao Windows para refazer a prévia, agora com a imagem guardada.
    private readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _printPreviewCache = [];
    private bool _printPreviewRefreshPending;

    private async void PrintDoc_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        var doc = (PrintDocument)sender;
        if (_printPdf is not { } pdf || _printPages.Count == 0)
        {
            doc.SetPreviewPage(e.PageNumber, new Canvas { Width = 100, Height = 100 });
            return;
        }
        int index = _printPages[Math.Clamp(e.PageNumber - 1, 0, _printPages.Count - 1)];
        var (sheet, image, scale) = PrintSheet(pdf, index, PreviewDpi);
        string key = $"{index}|{_printFit}|{_printComments}|{_printPaper.PageSize.Width:0}x{_printPaper.PageSize.Height:0}";
        if (_printPreviewCache.TryGetValue(key, out var ready))
        {
            image.Source = ready;
            doc.SetPreviewPage(e.PageNumber, sheet);
            return;
        }
        doc.SetPreviewPage(e.PageNumber, sheet);
        try
        {
            _printPreviewCache[key] = await PdfRenderer.RenderAsync(pdf, index, scale);
        }
        catch
        {
            return; // prévia sem a imagem desta página; a impressão tenta de novo
        }
        if (_printPreviewRefreshPending || !ReferenceEquals(doc, _printDoc)) return;
        _printPreviewRefreshPending = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            _printPreviewRefreshPending = false;
            _printDoc?.InvalidatePreview();
        });
    }

    private async void PrintDoc_AddPages(object sender, AddPagesEventArgs e)
    {
        var doc = (PrintDocument)sender;
        try
        {
            if (_printPdf is { } pdf)
            {
                int dpi = PrintLayout.Dpi(_printPages.Count);
                foreach (int index in _printPages)
                {
                    var (sheet, image, scale) = PrintSheet(pdf, index, dpi);
                    image.Source = await PdfRenderer.RenderAsync(pdf, index, scale);
                    doc.AddPage(sheet);
                }
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível preparar as páginas", ex.Message);
        }
        doc.AddPagesComplete();
    }

    /// <summary>
    /// Uma folha do tamanho do papel com a imagem da página no lugar certo. Devolve também a escala para
    /// renderizar a página na resolução <paramref name="dpi"/>.
    /// </summary>
    private (Canvas Sheet, Image Image, double Scale) PrintSheet(WinPdf.PdfDocument pdf, int index, int dpi)
    {
        using var page = pdf.GetPage((uint)index);
        double pageW = page.Size.Width, pageH = page.Size.Height; // DIPs (1/96 pol.)
        var paper = _printPaper;
        var area = paper.ImageableRect;
        if (area.Width <= 0 || area.Height <= 0) area = new Windows.Foundation.Rect(0, 0, paper.PageSize.Width, paper.PageSize.Height);
        var place = PrintLayout.Place(pageW, pageH, area.X, area.Y, area.Width, area.Height, _printFit);

        // A imagem tem o "em pé" da página; se a página foi girada no papel, a imagem gira 90° no centro do lugar.
        double imageW = place.Rotated ? place.Height : place.Width, imageH = place.Rotated ? place.Width : place.Height;
        var image = new Image { Width = imageW, Height = imageH, Stretch = Stretch.Fill };
        Canvas.SetLeft(image, place.X + (place.Width - imageW) / 2);
        Canvas.SetTop(image, place.Y + (place.Height - imageH) / 2);
        if (place.Rotated)
        {
            image.RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5);
            image.RenderTransform = new RotateTransform { Angle = 90 };
        }
        var sheet = new Canvas
        {
            Width = paper.PageSize.Width,
            Height = paper.PageSize.Height,
            Background = new SolidColorBrush(Microsoft.UI.Colors.White),
            Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, paper.PageSize.Width, paper.PageSize.Height) },
        };
        sheet.Children.Add(image);
        double scale = imageW / pageW * dpi / 96.0;
        return (sheet, image, scale);
    }

    private void EndPrint()
    {
        _printing = false;
        if (_printDoc is not null)
        {
            _printDoc.Paginate -= PrintDoc_Paginate;
            _printDoc.GetPreviewPage -= PrintDoc_GetPreviewPage;
            _printDoc.AddPages -= PrintDoc_AddPages;
        }
        _printDoc = null;
        _printSource = null;
        _printPreviewCache.Clear();
        _printPdf = _printPdfWithComments = null;
        if (_printNoCommentsCopy is not null)
        {
            try { File.Delete(_printNoCommentsCopy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _printNoCommentsCopy = null;
        }
    }
}
