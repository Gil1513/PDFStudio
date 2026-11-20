using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Printing;
using PDFStudio.Core;
using Windows.Graphics.Printing;
using Windows.Graphics.Printing.OptionDetails;
using WinPdf = Windows.Data.Pdf;

namespace PDFStudio.Services;

/// <summary>
/// Etapa 7.3 (separado na 8.2 para servir também à impressão em lote): imprime um PDF pela janela de impressão do
/// Windows (impressora, cópias, páginas, papel, prévia). Cada página vira uma imagem (motor de PDF do Windows)
/// posicionada no papel. Opções nossas na janela: "Tamanho" (ajustar à página / tamanho real) e "Comentários".
/// Uma tarefa por impressão: crie, chame <see cref="ShowAsync"/> e espere <see cref="Ended"/>.
/// </summary>
public sealed class PdfPrintJob
{
    private const string SizeOption = "PDFStudioTamanho", CommentsOption = "PDFStudioComentarios";
    private const int PreviewDpi = 110;

    private readonly string _path;
    private readonly string? _password;
    private readonly string _title;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<InfoBarSeverity, string, string> _status;

    private PrintDocument? _printDoc;
    private IPrintDocumentSource? _printSource;
    private WinPdf.PdfDocument? _printPdf;          // o que vai para o papel (com ou sem comentários)
    private WinPdf.PdfDocument? _printPdfWithComments;
    private string? _noCommentsCopy;
    private bool _fit = true, _comments = true;
    private List<int> _pages = [];
    private PrintPageDescription _paper;
    private bool _ended;

    // A prévia só mostra imagens que já estão prontas quando a página é entregue. Por isso a primeira vez
    // entrega a folha vazia, desenha a página e pede ao Windows para refazer a prévia, agora com a imagem guardada.
    private readonly Dictionary<string, Microsoft.UI.Xaml.Media.Imaging.BitmapImage> _previewCache = [];
    private bool _previewRefreshPending;

    /// <param name="path">PDF a imprimir.</param>
    /// <param name="password">Senha para abrir, se tiver.</param>
    /// <param name="title">Nome da tarefa na fila da impressora.</param>
    /// <param name="status">Mostra o resultado (enviado, falhou) na barra da tela que pediu.</param>
    public PdfPrintJob(string path, string? password, string title, DispatcherQueue dispatcher, Action<InfoBarSeverity, string, string> status)
    {
        _path = path;
        _password = password;
        _title = title;
        _dispatcher = dispatcher;
        _status = status;
    }

    /// <summary>Terminou (impressa, cancelada ou com erro). Chamado uma vez, na thread da tela.</summary>
    public event Action? Ended;

    /// <summary>Como terminou (null = a janela nem chegou a criar a tarefa, ou deu erro antes).</summary>
    public PrintTaskCompletion? Completion { get; private set; }

    /// <summary>Abre a janela de impressão do Windows. Erros vão para a barra de situação e terminam a tarefa.</summary>
    public async Task ShowAsync()
    {
        try
        {
            _printPdfWithComments = await PdfRenderer.LoadAsync(_path, _password);
            _printPdf = _printPdfWithComments;

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
            _status(InfoBarSeverity.Error, "Não foi possível imprimir", ex.Message);
            End();
        }
    }

    /// <summary>O Windows pede a tarefa de impressão: escolhe as opções que aparecem na janela.</summary>
    private void PrintManager_PrintTaskRequested(PrintManager sender, PrintTaskRequestedEventArgs args)
    {
        var source = _printSource;
        if (source is null) return;
        var task = args.Request.CreatePrintTask(_title, requested => requested.SetSource(source));
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
        task.Completed += (_, e) => _dispatcher.TryEnqueue(() =>
        {
            Completion = e.Completion;
            if (e.Completion == PrintTaskCompletion.Failed)
                _status(InfoBarSeverity.Error, "A impressão falhou", "O Windows não conseguiu imprimir. Confira a impressora e tente de novo.");
            else if (e.Completion == PrintTaskCompletion.Submitted)
                _status(InfoBarSeverity.Success, "Enviado para a impressora", $"“{_title}” está na fila de impressão.");
            End();
        });
    }

    private void PrintOptions_Changed(PrintTaskOptionDetails sender, PrintTaskOptionChangedEventArgs args)
    {
        string id = args.OptionId?.ToString() ?? "";
        if (id != SizeOption && id != CommentsOption) return;
        string value = sender.Options[id].Value?.ToString() ?? "";
        _dispatcher.TryEnqueue(async () =>
        {
            if (_printDoc is null) return;
            if (id == SizeOption) _fit = value != "real";
            else
            {
                _comments = value != "nao";
                try
                {
                    _printPdf = _comments ? _printPdfWithComments : await NoCommentsDocumentAsync();
                }
                catch (Exception ex)
                {
                    _status(InfoBarSeverity.Error, "Não foi possível tirar os comentários", ex.Message);
                    _printPdf = _printPdfWithComments;
                }
            }
            _printDoc?.InvalidatePreview();
        });
    }

    /// <summary>Cópia temporária sem os comentários (feita uma vez por impressão).</summary>
    private async Task<WinPdf.PdfDocument> NoCommentsDocumentAsync()
    {
        if (_noCommentsCopy is null)
        {
            string copy = Path.Combine(TempFolder(), $"{Guid.NewGuid():N}.pdf");
            string source = _path;
            await Task.Run(() => PdfAnnotations.CopyWithoutComments(source, copy));
            _noCommentsCopy = copy;
        }
        return await PdfRenderer.LoadAsync(_noCommentsCopy, _password);
    }

    /// <summary>Pasta dos arquivos temporários de impressão (%TEMP%\PDFStudio\impressao).</summary>
    public static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PDFStudio", "impressao");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void PrintDoc_Paginate(object sender, PaginateEventArgs e)
    {
        if (_printPdf is not { } pdf) return;
        _paper = e.PrintTaskOptions.GetPageDescription(0);
        var ranges = e.PrintTaskOptions.CustomPageRanges.Select(r => (r.FirstPageNumber, r.LastPageNumber));
        _pages = PrintLayout.Pages((int)pdf.PageCount, ranges);
        ((PrintDocument)sender).SetPreviewPageCount(Math.Max(1, _pages.Count), PreviewPageCountType.Final);
    }

    private async void PrintDoc_GetPreviewPage(object sender, GetPreviewPageEventArgs e)
    {
        var doc = (PrintDocument)sender;
        if (_printPdf is not { } pdf || _pages.Count == 0)
        {
            doc.SetPreviewPage(e.PageNumber, new Canvas { Width = 100, Height = 100 });
            return;
        }
        int index = _pages[Math.Clamp(e.PageNumber - 1, 0, _pages.Count - 1)];
        var (sheet, image, scale) = PrintSheet(pdf, index, PreviewDpi);
        string key = $"{index}|{_fit}|{_comments}|{_paper.PageSize.Width:0}x{_paper.PageSize.Height:0}";
        if (_previewCache.TryGetValue(key, out var ready))
        {
            image.Source = ready;
            doc.SetPreviewPage(e.PageNumber, sheet);
            return;
        }
        doc.SetPreviewPage(e.PageNumber, sheet);
        try
        {
            _previewCache[key] = await PdfRenderer.RenderAsync(pdf, index, scale);
        }
        catch
        {
            return; // prévia sem a imagem desta página; a impressão tenta de novo
        }
        if (_previewRefreshPending || !ReferenceEquals(doc, _printDoc)) return;
        _previewRefreshPending = true;
        _dispatcher.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _previewRefreshPending = false;
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
                int dpi = PrintLayout.Dpi(_pages.Count);
                foreach (int index in _pages)
                {
                    var (sheet, image, scale) = PrintSheet(pdf, index, dpi);
                    image.Source = await PdfRenderer.RenderAsync(pdf, index, scale);
                    doc.AddPage(sheet);
                }
            }
        }
        catch (Exception ex)
        {
            _status(InfoBarSeverity.Error, "Não foi possível preparar as páginas", ex.Message);
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
        var paper = _paper;
        var area = paper.ImageableRect;
        if (area.Width <= 0 || area.Height <= 0) area = new Windows.Foundation.Rect(0, 0, paper.PageSize.Width, paper.PageSize.Height);
        var place = PrintLayout.Place(pageW, pageH, area.X, area.Y, area.Width, area.Height, _fit);

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

    private void End()
    {
        if (_ended) return;
        _ended = true;
        if (_printDoc is not null)
        {
            _printDoc.Paginate -= PrintDoc_Paginate;
            _printDoc.GetPreviewPage -= PrintDoc_GetPreviewPage;
            _printDoc.AddPages -= PrintDoc_AddPages;
        }
        _printDoc = null;
        _printSource = null;
        _previewCache.Clear();
        _printPdf = _printPdfWithComments = null;
        if (_noCommentsCopy is not null)
        {
            try { File.Delete(_noCommentsCopy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _noCommentsCopy = null;
        }
        Ended?.Invoke();
    }
}
