using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Core;
using PDFStudio.Dialogs;
using Windows.Graphics.Printing;
using WinPdf = Windows.Data.Pdf;

namespace PDFStudio.Services;

/// <summary>O que foi escolhido na tela de impressão.</summary>
/// <param name="Pages">Páginas (índices de 0), na ordem.</param>
/// <param name="DevMode">Configuração da impressora (papel, frente e verso) já com as escolhas da tela.</param>
/// <param name="OutputFile">Impressoras que gravam arquivo (Imprimir em PDF): onde gravar.</param>
public sealed record PrintChoice(PrinterInfo Printer, PrintDevMode DevMode, IReadOnlyList<int> Pages, int Copies, bool Collate,
    bool Fit, bool Color, bool Comments, string? OutputFile);

/// <summary>
/// Imprime um PDF pela tela de impressão do próprio app (<see cref="PrintDialog"/>), sem a janela do Windows:
/// impressora, cópias, páginas, papel, frente e verso, cor, comentários e prévia. Cada folha vai para a impressora
/// como uma imagem da página (motor de PDF do Windows), pelas funções de impressão do Windows (GDI).
/// Uma tarefa por impressão: crie, chame <see cref="ShowAsync"/> e espere <see cref="Ended"/>.
/// </summary>
public sealed class PdfPrintJob
{
    private readonly string _path;
    private readonly string? _password;
    private readonly string _title;
    private readonly int _currentPage;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<InfoBarSeverity, string, string> _status;

    private WinPdf.PdfDocument? _pdf, _pdfNoComments;
    private string? _noCommentsCopy;
    private bool _ended;

    /// <param name="path">PDF a imprimir.</param>
    /// <param name="password">Senha para abrir, se tiver.</param>
    /// <param name="title">Nome da tarefa na fila da impressora.</param>
    /// <param name="status">Mostra o resultado (enviado, falhou) na barra da tela que pediu.</param>
    /// <param name="currentPage">Página que está na tela (1…), para a opção "Página atual"; 0 = sem essa opção.</param>
    public PdfPrintJob(string path, string? password, string title, DispatcherQueue dispatcher,
        Action<InfoBarSeverity, string, string> status, int currentPage = 0)
    {
        _path = path;
        _password = password;
        _title = title;
        _dispatcher = dispatcher;
        _status = status;
        _currentPage = currentPage;
    }

    /// <summary>Terminou (impressa, cancelada ou com erro). Chamado uma vez, na thread da tela.</summary>
    public event Action? Ended;

    /// <summary>Como terminou (null = a tela nem chegou a abrir, ou deu erro antes).</summary>
    public PrintTaskCompletion? Completion { get; private set; }

    public string Title => _title;

    /// <summary>Abre a tela de impressão. Erros vão para a barra de situação e terminam a tarefa.</summary>
    public async Task ShowAsync()
    {
        try
        {
            _pdf = await PdfRenderer.LoadAsync(_path, _password);
            var printers = await Task.Run(WindowsPrinters.List);
            if (printers.Count == 0)
            {
                _status(InfoBarSeverity.Error, "Nenhuma impressora instalada",
                    "Adicione uma impressora no Windows (Configurações → Bluetooth e dispositivos → Impressoras e scanners).");
                return;
            }
            var dialog = new PrintDialog(this, _pdf, _currentPage, printers)
            {
                XamlRoot = App.MainWindow?.Content.XamlRoot,
            };
            await dialog.ShowAsync();
            Completion = dialog.Outcome ?? PrintTaskCompletion.Canceled;
            if (Completion == PrintTaskCompletion.Submitted && dialog.Sent is { } sent)
            {
                if (sent.OutputFile is { } file)
                    _status(InfoBarSeverity.Success, "Arquivo gravado", $"“{Path.GetFileName(file)}” foi criado em {Path.GetDirectoryName(file)}.");
                else
                    _status(InfoBarSeverity.Success, "Enviado para a impressora", $"“{_title}” está na fila de “{sent.Printer.Name}”.");
            }
        }
        catch (Exception ex)
        {
            Completion = PrintTaskCompletion.Failed;
            _status(InfoBarSeverity.Error, "Não foi possível imprimir", ex.Message);
        }
        finally
        {
            End();
        }
    }

    /// <summary>O documento a imprimir, com ou sem os comentários (a cópia sem eles é feita uma vez só).</summary>
    internal async Task<WinPdf.PdfDocument> DocumentAsync(bool comments)
    {
        if (comments || _pdf is null) return _pdf ?? await PdfRenderer.LoadAsync(_path, _password);
        if (_pdfNoComments is not null) return _pdfNoComments;
        if (_noCommentsCopy is null)
        {
            string copy = Path.Combine(TempFolder(), $"{Guid.NewGuid():N}.pdf");
            string source = _path;
            await Task.Run(() => PdfAnnotations.CopyWithoutComments(source, copy));
            _noCommentsCopy = copy;
        }
        return _pdfNoComments = await PdfRenderer.LoadAsync(_noCommentsCopy, _password);
    }

    /// <summary>
    /// Manda as folhas para a impressora. Cancelar no meio desiste de tudo (nada fica na fila).
    /// <paramref name="progress"/> recebe (folhas prontas, total).
    /// </summary>
    internal async Task PrintAsync(PrintChoice choice, IProgress<(int Done, int Total)> progress, CancellationToken cancel)
    {
        var pdf = await DocumentAsync(choice.Comments);
        var dm = choice.DevMode.Clone();
        var sheets = PrintLayout.Sequence(choice.Pages, choice.Copies, choice.Collate, dm.Duplex != 1);
        dm.Copies = 1; // as cópias são feitas aqui (nem todo driver faz)
        dm.Color = choice.Color;
        string title = _title;

        await Task.Run(async () =>
        {
            var finalDm = WindowsPrinters.Validate(choice.Printer.Name, dm);
            using var session = WindowsPrintSession.Start(choice.Printer.Name, finalDm, title, choice.OutputFile);
            var sheet = session.Sheet;
            int dpi = Math.Max(72, Math.Min(Math.Min(sheet.DpiX, sheet.DpiY), PrintLayout.Dpi(choice.Pages.Count)));
            int lastIndex = -1;
            byte[] pixels = [];
            int width = 0, height = 0;
            PrintPlacement place = default;
            for (int i = 0; i < sheets.Count; i++)
            {
                cancel.ThrowIfCancellationRequested();
                progress.Report((i, sheets.Count));
                if (sheets[i] is not int index)
                {
                    session.AddBlankSheet();
                    continue;
                }
                if (index != lastIndex) // sem agrupar, a mesma página vem várias vezes seguidas: desenha uma vez só
                {
                    (pixels, width, height, place) = await RenderForSheetAsync(pdf, index, sheet, dpi, choice.Fit, choice.Color);
                    lastIndex = index;
                }
                session.AddImageSheet(pixels, width, height, (int)Math.Round(place.X), (int)Math.Round(place.Y),
                    Math.Max(1, (int)Math.Round(place.Width)), Math.Max(1, (int)Math.Round(place.Height)));
            }
            cancel.ThrowIfCancellationRequested();
            session.Finish();
            progress.Report((sheets.Count, sheets.Count));
        }, cancel);
    }

    /// <summary>
    /// A imagem da página do jeito que vai para a folha: no tamanho certo para <paramref name="dpi"/>, girada se a
    /// página foi deitada no papel e em cinza se for preto e branco. Também serve para a prévia (com um dpi baixo).
    /// </summary>
    internal static async Task<(byte[] Pixels, int Width, int Height, PrintPlacement Place)> RenderForSheetAsync(
        WinPdf.PdfDocument pdf, int index, SheetMetrics sheet, double dpi, bool fit, bool color)
    {
        double pageW, pageH;
        using (var page = pdf.GetPage((uint)index))
        {
            pageW = page.Size.Width / 96.0; // polegadas
            pageH = page.Size.Height / 96.0;
        }
        var place = PrintLayout.PlaceOnSheet(sheet, pageW, pageH, fit);
        // Tamanho da imagem em pé (antes de girar), na resolução pedida.
        double inchesW = (place.Rotated ? place.Height / sheet.DpiY : place.Width / sheet.DpiX);
        double inchesH = (place.Rotated ? place.Width / sheet.DpiX : place.Height / sheet.DpiY);
        int w = Math.Max(1, (int)Math.Round(inchesW * dpi)), h = Math.Max(1, (int)Math.Round(inchesH * dpi));
        var pixels = await PdfRenderer.RenderPixelsAsync(pdf, index, w, h);
        if (!color) PrintBitmap.ToGray(pixels);
        if (place.Rotated)
        {
            pixels = PrintBitmap.Rotate90(pixels, w, h);
            (w, h) = (h, w);
        }
        return (pixels, w, h, place);
    }

    /// <summary>Pasta dos arquivos temporários de impressão (%TEMP%\PDFStudio\impressao).</summary>
    public static string TempFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), "PDFStudio", "impressao");
        Directory.CreateDirectory(folder);
        return folder;
    }

    private void End()
    {
        if (_ended) return;
        _ended = true;
        _pdf = _pdfNoComments = null;
        if (_noCommentsCopy is not null)
        {
            try { File.Delete(_noCommentsCopy); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            _noCommentsCopy = null;
        }
        Ended?.Invoke(); // ShowAsync roda na thread da tela
    }
}
