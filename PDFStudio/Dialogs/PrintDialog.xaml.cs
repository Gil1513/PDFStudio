using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Graphics.Printing;
using WinPdf = Windows.Data.Pdf;

namespace PDFStudio.Dialogs;

/// <summary>
/// Tela de impressão do próprio app (no lugar da janela do Windows): impressora, cópias, páginas, papel, tamanho,
/// frente e verso, cor, comentários e a prévia da folha como ela vai sair (margens da impressora em tracejado).
/// O botão Imprimir manda as folhas e mostra o andamento aqui mesmo; Cancelar no meio desiste de tudo.
/// </summary>
public sealed partial class PrintDialog : ContentDialog
{
    private const double PreviewWidth = 300, PreviewHeight = 400, PreviewFill = 0.92;

    private readonly PdfPrintJob _job;
    private readonly WinPdf.PdfDocument _pdf;
    private readonly int _pageCount, _currentPage;
    private readonly List<PrinterInfo> _printers;
    private readonly bool _ready;

    private PrinterInfo? _printer;
    private PrintDevMode? _devMode;
    private PrinterCapabilities? _caps;
    private SheetMetrics? _sheet;
    private bool _loading;              // preenchendo as listas: as mudanças não são da pessoa
    private int _printerVersion, _previewVersion, _previewPos;
    private bool _busy;
    private CancellationTokenSource? _cancel;

    /// <param name="currentPage">Página na tela (1…); 0 = sem a opção "Página atual".</param>
    public PrintDialog(PdfPrintJob job, WinPdf.PdfDocument pdf, int currentPage, List<PrinterInfo> printers)
    {
        InitializeComponent();
        _job = job;
        _pdf = pdf;
        _pageCount = (int)pdf.PageCount;
        _currentPage = currentPage > 0 && currentPage <= _pageCount ? currentPage : 0;
        _printers = printers;
        Title = $"Imprimir “{job.Title}”";

        if (_currentPage > 0) PagesChoice.Items[1] = $"Página atual ({_currentPage})";
        else PagesChoice.Items.RemoveAt(1);
        PagesChoice.Items[0] = _pageCount == 1 ? "Todas (1 página)" : $"Todas ({_pageCount} páginas)";

        foreach (var p in printers) PrinterBox.Items.Add(p.IsDefault ? $"{p.Name} (padrão)" : p.Name);
        int start = printers.FindIndex(p => p.Name == App.Settings.LastPrinter);
        if (start < 0) start = printers.FindIndex(p => p.IsDefault);
        _ready = true;
        PrinterBox.SelectedIndex = Math.Max(0, start);
        UpdateSummary();
    }

    // ---------------- Resultado ----------------

    /// <summary>Submitted = foi para a fila; Canceled = fechou sem imprimir (ou parou no meio).</summary>
    public PrintTaskCompletion? Outcome { get; private set; }

    /// <summary>O que foi impresso (quando <see cref="Outcome"/> é Submitted).</summary>
    public PrintChoice? Sent { get; private set; }

    // ---------------- Impressora ----------------

    private async void PrinterBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || PrinterBox.SelectedIndex < 0) return;
        var printer = _printers[PrinterBox.SelectedIndex];
        int version = ++_printerVersion;
        _printer = printer;
        _devMode = null;
        _caps = null;
        _sheet = null;
        ErrorBar.IsOpen = false;
        PreviewRing.IsActive = true;
        IsPrimaryButtonEnabled = false;
        PrinterNote.Text = printer.WritesFile ? "Grava um arquivo: você escolhe onde ao imprimir." : "";
        try
        {
            var (dm, caps) = await Task.Run(() =>
            {
                var d = WindowsPrinters.DefaultDevMode(printer.Name);
                return (d, WindowsPrinters.Capabilities(printer, d));
            });
            if (version != _printerVersion) return;
            _devMode = dm;
            _caps = caps;
            FillFromDevMode();
            IsPrimaryButtonEnabled = !_busy;
            await RemeasureAsync();
        }
        catch (Exception ex)
        {
            if (version != _printerVersion) return;
            PreviewRing.IsActive = false;
            ShowError("Não foi possível ler a impressora", ex.Message);
        }
    }

    /// <summary>Põe nas listas (papel, frente e verso, cor) o que a configuração da impressora diz.</summary>
    private void FillFromDevMode()
    {
        if (_devMode is not { } dm || _caps is not { } caps) return;
        _loading = true;
        try
        {
            PaperBox.Items.Clear();
            foreach (var paper in caps.Papers) PaperBox.Items.Add($"{paper.Name}  ({paper.WidthMm:0} × {paper.HeightMm:0} mm)");
            int paperIndex = caps.Papers.ToList().FindIndex(p => p.Id == dm.PaperSize);
            if (paperIndex < 0) paperIndex = caps.Papers.ToList().FindIndex(p => p.Id == 9); // A4
            PaperBox.SelectedIndex = caps.Papers.Count == 0 ? -1 : Math.Max(0, paperIndex);
            PaperBox.IsEnabled = caps.Papers.Count > 0;

            DuplexBox.IsEnabled = caps.Duplex;
            DuplexBox.SelectedIndex = caps.Duplex ? Math.Clamp(dm.Duplex - 1, 0, 2) : 0;
            ToolTipService.SetToolTip(DuplexBox, caps.Duplex ? null : "Esta impressora não imprime frente e verso sozinha.");

            ColorBox.IsEnabled = caps.Color;
            ColorBox.SelectedIndex = caps.Color && dm.Color ? 0 : 1;
            ToolTipService.SetToolTip(ColorBox, caps.Color ? null : "Esta impressora só imprime em preto e branco.");
        }
        finally
        {
            _loading = false;
        }
    }

    /// <summary>A configuração com as escolhas da tela (papel e frente e verso).</summary>
    private PrintDevMode? CurrentDevMode()
    {
        if (_devMode is not { } source) return null;
        var dm = source.Clone();
        if (_caps is { } caps && PaperBox.SelectedIndex >= 0 && PaperBox.SelectedIndex < caps.Papers.Count)
            dm.PaperSize = caps.Papers[PaperBox.SelectedIndex].Id;
        if (DuplexBox.IsEnabled) dm.Duplex = (short)(DuplexBox.SelectedIndex + 1);
        return dm;
    }

    /// <summary>Mede a folha (papel inteiro e margens da impressora) e refaz a prévia.</summary>
    private async Task RemeasureAsync()
    {
        if (_printer is not { } printer || CurrentDevMode() is not { } dm) return;
        int version = _printerVersion;
        PreviewRing.IsActive = true;
        try
        {
            var sheet = await Task.Run(() => WindowsPrinters.Measure(printer.Name, WindowsPrinters.Validate(printer.Name, dm)));
            if (version != _printerVersion) return;
            if (sheet.DpiX <= 0 || sheet.DpiY <= 0 || sheet.PhysicalWidth <= 0 || sheet.PhysicalHeight <= 0)
                throw new InvalidOperationException("A impressora não informou o tamanho da folha.");
            _sheet = sheet;
            await UpdatePreviewAsync();
        }
        catch (Exception ex)
        {
            if (version != _printerVersion) return;
            PreviewRing.IsActive = false;
            ShowError("Não foi possível medir a folha", ex.Message);
        }
    }

    private async void PaperBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading) return;
        await RemeasureAsync();
    }

    private void DuplexBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading) return;
        UpdateSummary();
    }

    /// <summary>Opções que só o fabricante da impressora oferece (qualidade, bandeja…), na janela do próprio driver.</summary>
    private async void Preferences_Click(object sender, RoutedEventArgs e)
    {
        if (_printer is not { } printer || CurrentDevMode() is not { } dm || App.MainWindow is null) return;
        nint owner = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
        PrintDevMode? edited;
        try
        {
            edited = WindowsPrinters.EditPreferences(owner, printer.Name, dm);
        }
        catch (Exception ex)
        {
            ShowError("Não foi possível abrir as preferências", ex.Message);
            return;
        }
        if (edited is null) return; // cancelou
        _devMode = edited;
        FillFromDevMode();
        UpdateSummary();
        await RemeasureAsync();
    }

    // ---------------- Páginas ----------------

    private bool CustomPages => PagesChoice.SelectedIndex == PagesChoice.Items.Count - 1;
    private bool CurrentPageOnly => _currentPage > 0 && PagesChoice.SelectedIndex == 1;

    /// <summary>As páginas escolhidas (índices de 0, na ordem). Erro de escrita no intervalo = FormatException.</summary>
    private List<int> SelectedPages()
    {
        List<int> pages;
        if (CurrentPageOnly) pages = [_currentPage - 1];
        else if (CustomPages)
        {
            var seen = new HashSet<int>();
            pages = PageRanges.Parse(RangesBox.Text, _pageCount).SelectMany(g => g).Where(seen.Add).ToList();
        }
        else pages = Enumerable.Range(0, _pageCount).ToList();
        var parity = (PageParity)Math.Max(0, ParityBox.SelectedIndex);
        pages = PrintLayout.Parity(pages, parity);
        if (pages.Count == 0)
            throw new FormatException(parity == PageParity.Even ? "Não há páginas pares na escolha." : "Nenhuma página escolhida.");
        return pages;
    }

    private async void PagesChoice_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready) return;
        RangesBox.IsEnabled = CustomPages;
        if (CustomPages) RangesBox.Focus(FocusState.Programmatic);
        _previewPos = 0;
        UpdateSummary();
        await UpdatePreviewAsync();
    }

    private async void RangesBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_ready) return;
        _previewPos = 0;
        UpdateSummary();
        await UpdatePreviewAsync();
    }

    // ---------------- Outras opções ----------------

    private async void Option_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _loading) return;
        if (ReferenceEquals(sender, ParityBox)) _previewPos = 0;
        UpdateSummary();
        await UpdatePreviewAsync();
    }

    private async void Option_Click(object sender, RoutedEventArgs e)
    {
        if (!_ready) return;
        UpdateSummary();
        if (ReferenceEquals(sender, CommentsBox)) await UpdatePreviewAsync();
    }

    private void Option_NumberChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (!_ready) return;
        if (double.IsNaN(sender.Value) || sender.Value < 1) sender.Value = 1;
        UpdateSummary();
    }

    private int Copies => double.IsNaN(CopiesBox.Value) ? 1 : Math.Clamp((int)Math.Round(CopiesBox.Value), 1, 999);

    /// <summary>Quantas páginas e folhas de papel vão sair.</summary>
    private void UpdateSummary()
    {
        CollateBox.IsEnabled = Copies > 1;
        List<int> pages;
        try
        {
            pages = SelectedPages();
        }
        catch (FormatException ex)
        {
            SummaryText.Text = ex.Message;
            return;
        }
        bool duplex = DuplexBox.IsEnabled && DuplexBox.SelectedIndex > 0;
        int sides = PrintLayout.Sequence(pages, Copies, CollateBox.IsChecked == true, duplex).Count;
        int sheets = duplex ? (sides + 1) / 2 : sides;
        string p = pages.Count == 1 ? "1 página" : $"{pages.Count} páginas";
        string c = Copies == 1 ? "" : $" × {Copies} cópias";
        SummaryText.Text = $"{p}{c} = {sheets} {(sheets == 1 ? "folha" : "folhas")} de papel";
    }

    // ---------------- Prévia ----------------

    private async void Prev_Click(object sender, RoutedEventArgs e)
    {
        _previewPos--;
        await UpdatePreviewAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        _previewPos++;
        await UpdatePreviewAsync();
    }

    /// <summary>
    /// Desenha a folha: papel no formato certo, margens que a impressora não alcança (tracejado) e a página no lugar
    /// e do jeito (girada, cinza) que vai sair. Mudanças rápidas seguidas: só a última é desenhada.
    /// </summary>
    private async Task UpdatePreviewAsync()
    {
        int version = ++_previewVersion;
        if (_sheet is not { } sheet) return;

        double scale = Math.Min(PreviewWidth / sheet.PhysicalWidth, PreviewHeight / sheet.PhysicalHeight) * PreviewFill;
        double paperW = sheet.PhysicalWidth * scale, paperH = sheet.PhysicalHeight * scale;
        PaperSheet.Width = paperW + 2;
        PaperSheet.Height = paperH + 2;
        PaperCanvas.Width = paperW;
        PaperCanvas.Height = paperH;
        PaperCanvas.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, paperW, paperH) };
        Canvas.SetLeft(PrintableFrame, sheet.OffsetX * scale);
        Canvas.SetTop(PrintableFrame, sheet.OffsetY * scale);
        PrintableFrame.Width = sheet.PrintableWidth * scale;
        PrintableFrame.Height = sheet.PrintableHeight * scale;

        List<int> pages;
        try
        {
            pages = SelectedPages();
        }
        catch (FormatException)
        {
            PreviewImage.Source = null;
            PreviewPageText.Text = "Confira as páginas";
            PrevButton.IsEnabled = NextButton.IsEnabled = false;
            PreviewRing.IsActive = false;
            return;
        }
        _previewPos = Math.Clamp(_previewPos, 0, pages.Count - 1);
        int index = pages[_previewPos];
        PrevButton.IsEnabled = _previewPos > 0;
        NextButton.IsEnabled = _previewPos < pages.Count - 1;
        PreviewPageText.Text = pages.Count == 1 ? $"Página {index + 1}" : $"Página {index + 1}  ({_previewPos + 1} de {pages.Count})";

        PreviewRing.IsActive = true;
        try
        {
            bool fit = SizeBox.SelectedIndex != 1, color = ColorBox.SelectedIndex != 1;
            var pdf = await _job.DocumentAsync(CommentsBox.IsChecked == true);
            if (version != _previewVersion) return;
            double raster = XamlRoot?.RasterizationScale ?? 1;
            double dpi = scale * sheet.DpiX * raster;
            var (pixels, w, h, place) = await PdfPrintJob.RenderForSheetAsync(pdf, index, sheet, dpi, fit, color);
            if (version != _previewVersion) return;

            var bitmap = new WriteableBitmap(w, h);
            using (var stream = bitmap.PixelBuffer.AsStream()) stream.Write(pixels, 0, Math.Min(pixels.Length, w * h * 4));
            bitmap.Invalidate();
            PreviewImage.Source = bitmap;
            PreviewImage.Width = Math.Max(1, place.Width * scale);
            PreviewImage.Height = Math.Max(1, place.Height * scale);
            Canvas.SetLeft(PreviewImage, (place.X + sheet.OffsetX) * scale);
            Canvas.SetTop(PreviewImage, (place.Y + sheet.OffsetY) * scale);
        }
        catch (Exception ex)
        {
            if (version != _previewVersion) return;
            PreviewImage.Source = null;
            PreviewPageText.Text = "Sem prévia: " + ex.Message;
        }
        finally
        {
            if (version == _previewVersion) PreviewRing.IsActive = false;
        }
    }

    // ---------------- Imprimir ----------------

    private async void Primary_Click(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        args.Cancel = true; // a tela fica aberta mostrando o andamento; fecha sozinha no fim
        if (_busy || _printer is not { } printer || CurrentDevMode() is not { } dm) return;
        ErrorBar.IsOpen = false;
        List<int> pages;
        try
        {
            pages = SelectedPages();
        }
        catch (FormatException ex)
        {
            ShowError("Confira as páginas", ex.Message);
            return;
        }

        string? output = null;
        if (printer.WritesFile)
        {
            string ext = printer.FileExtension;
            output = ext == ".pdf" ? await FilePickers.SavePdfAsync(_job.Title) : await FilePickers.SaveFileAsync(_job.Title + ext);
            if (output is null) return;
        }

        var choice = new PrintChoice(printer, dm, pages, Copies, CollateBox.IsChecked == true, SizeBox.SelectedIndex != 1,
            ColorBox.SelectedIndex != 1, CommentsBox.IsChecked == true, output);
        App.Settings.LastPrinter = printer.Name;
        App.Settings.Save();

        SetBusy(true);
        var cancel = _cancel = new CancellationTokenSource();
        var progress = new Progress<(int Done, int Total)>(p =>
        {
            ProgressBar.Value = p.Total == 0 ? 1 : p.Done / (double)p.Total;
            ProgressText.Text = p.Done >= p.Total
                ? "Entregando à impressora…"
                : $"Preparando a folha {p.Done + 1} de {p.Total}…";
        });
        try
        {
            await _job.PrintAsync(choice, progress, cancel.Token);
            Outcome = PrintTaskCompletion.Submitted;
            Sent = choice;
            SetBusy(false);
            Hide();
        }
        catch (OperationCanceledException)
        {
            Outcome = PrintTaskCompletion.Canceled;
            SetBusy(false);
            Hide(); // parou pelo Cancelar: nada foi para a fila
        }
        catch (Exception ex)
        {
            SetBusy(false);
            ShowError("A impressão falhou", ex.Message + " Nada foi enviado. Confira a impressora e tente de novo.");
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
        }
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        OptionsScroll.IsEnabled = !busy;
        PrevButton.IsEnabled = NextButton.IsEnabled = !busy;
        IsPrimaryButtonEnabled = !busy;
        ProgressPanel.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        ProgressBar.Value = 0;
        ProgressText.Text = "Começando…";
        CloseButtonText = busy ? "Parar" : "Cancelar";
    }

    /// <summary>Fechar durante a impressão = parar: espera desistir (nada fica na fila) e aí fecha.</summary>
    private void Dialog_Closing(ContentDialog sender, ContentDialogClosingEventArgs args)
    {
        if (!_busy) return;
        args.Cancel = true;
        ProgressText.Text = "Parando…";
        _cancel?.Cancel();
    }

    private void ShowError(string title, string message)
    {
        ErrorBar.Title = title;
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
    }
}
