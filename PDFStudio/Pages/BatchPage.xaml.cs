using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Core;
using PDFStudio.Dialogs;
using PDFStudio.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PDFStudio.Pages;

/// <summary>Uma ação que dá para aplicar em lote (item da lista da esquerda).</summary>
public sealed class BatchAction(string key, string glyph, string title, string description, string suffix)
{
    public string Key { get; } = key;
    public string Glyph { get; } = glyph;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Suffix { get; } = suffix;
}

/// <summary>Um arquivo do lote, com a situação dele (esperando, processando, pronto, erro).</summary>
public sealed partial class BatchFileEntry(string path, int pageCount) : INotifyPropertyChanged
{
    private string _statusText = "";
    private string _statusGlyph = "";
    private Brush? _statusBrush;

    public string Path { get; } = path;
    public int PageCount { get; } = pageCount;
    public string? Output { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
    public string Details => $"{PageCount} {(PageCount == 1 ? "página" : "páginas")} · {System.IO.Path.GetDirectoryName(Path)}";
    public string StatusText => _statusText;
    public string StatusGlyph => _statusGlyph;
    public Brush? StatusBrush => _statusBrush;
    public string AccessibleName => _statusText.Length == 0 ? Name : $"{Name}: {_statusText}";

    public void SetStatus(string text, string glyph, string brushKey)
    {
        _statusText = text;
        _statusGlyph = glyph;
        _statusBrush = (Brush)Application.Current.Resources[brushKey];
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusBrush)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AccessibleName)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// Etapa 8: PDFs em lote. A mesma ação em vários arquivos; os originais não mudam e nada é sobrescrito.
/// 8.1: comprimir, marca d'água, fundo, cabeçalho/rodapé e número de página (as janelas de opções são as mesmas
/// do visualizador, com a prévia do primeiro arquivo).
/// </summary>
public sealed partial class BatchPage : Page
{
    public ObservableCollection<BatchFileEntry> Files { get; } = [];

    public List<BatchAction> Actions { get; } =
    [
        new("comprimir", "", "Comprimir", "Arquivos menores, fotos reduzidas.", "-comprimido"),
        new("marcadagua", "", "Marca d'água", "Texto ou imagem por cima ou por baixo.", "-marca"),
        new("fundo", "", "Fundo", "Cor ou imagem atrás das páginas.", "-fundo"),
        new("cabecalho", "", "Cabeçalho e rodapé", "Texto no alto e no pé das páginas.", "-cabecalho"),
        new("numeropagina", "", "Número de página", "\"Página 1 de 10\" no rodapé.", "-numerado"),
    ];

    private string? _outputFolder;           // null = a mesma pasta de cada arquivo
    private string _autoSuffix = "";         // sufixo sugerido pela ação (troca junto se o usuário não mexeu)
    private CancellationTokenSource? _cancel;
    private Action? _statusAction;

    public BatchPage()
    {
        InitializeComponent();
        Files.CollectionChanged += (_, _) => UpdateState();
        ActionList.SelectedIndex = 0;
        UpdateState();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        App.MainWindow!.SetSubtitle("PDFs em lote");
        // Vindo do visualizador: o arquivo aberto já entra na lista.
        if (e.Parameter is string path && File.Exists(path) && Files.All(f => !SamePath(f.Path, path))) await AddFilesAsync([path]);
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        _cancel?.Cancel(); // saiu da tela: o arquivo da vez termina e o lote para
    }

    private BatchAction SelectedAction => ActionList.SelectedItem as BatchAction ?? Actions[0];

    private void ActionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var action = SelectedAction;
        CompressLevelBox.Visibility = action.Key == "comprimir" ? Visibility.Visible : Visibility.Collapsed;
        if (SuffixBox.Text == _autoSuffix) SuffixBox.Text = action.Suffix; // o usuário não trocou: segue a ação
        _autoSuffix = action.Suffix;
        foreach (var f in Files) f.SetStatus("", "", "TextFillColorSecondaryBrush"); // nova ação: situação zerada
    }

    // ---------------- Arquivos ----------------

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await AddFilesAsync(await FilePickers.PickPdfsAsync());

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FilePickers.PickFolderAsync() is not { } folder) return;
        await AddFilesAsync(PdfsIn(folder));
    }

    /// <summary>Os PDFs de uma pasta (só ela, sem as subpastas), em ordem alfabética.</summary>
    private static IEnumerable<string> PdfsIn(string folder)
    {
        try { return Directory.GetFiles(folder, "*.pdf").Order(StringComparer.CurrentCultureIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        int added = 0;
        foreach (var path in paths)
        {
            if (Files.Any(f => SamePath(f.Path, path))) continue; // já está na lista
            try
            {
                int count = await Task.Run(() => PdfOperations.GetPageCount(path));
                Files.Add(new BatchFileEntry(path, count));
                added++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        if (failed.Count > 0)
            ShowStatus(InfoBarSeverity.Warning, "Alguns arquivos não foram adicionados", string.Join("\n", failed.Take(8)));
        else if (added == 0 && paths.Any())
            ShowStatus(InfoBarSeverity.Informational, "Nada novo para adicionar", "Esses arquivos já estão na lista.");
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.DragUIOverride.Caption = "Adicionar ao lote";
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var paths = new List<string>();
        foreach (var item in await e.DataView.GetStorageItemsAsync())
        {
            if (item is StorageFolder folder) paths.AddRange(PdfsIn(folder.Path));
            else if (item is StorageFile file && file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase)) paths.Add(file.Path);
        }
        await AddFilesAsync(paths);
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in FilesList.SelectedItems.Cast<BatchFileEntry>().ToList()) Files.Remove(f);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Files.Clear();

    private async void PickFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FilePickers.PickFolderAsync() is not { } folder) return;
        _outputFolder = folder;
        OutputFolderBox.Text = folder;
    }

    private void UpdateState()
    {
        bool busy = _cancel is not null;
        EmptyState.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = !busy && FilesList.SelectedItems.Count > 0;
        ClearButton.IsEnabled = !busy && Files.Count > 0;
        AddFilesButton.IsEnabled = AddFolderButton.IsEnabled = ActionList.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && Files.Count > 0;
        StopButton.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
            SummaryText.Text = Files.Count == 0 ? "" :
                $"{Files.Count} {(Files.Count == 1 ? "arquivo" : "arquivos")}, {Files.Sum(f => f.PageCount)} páginas no total";
    }

    // ---------------- Executar ----------------

    private void Stop_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Files.Count == 0 || _cancel is not null) return;
        string suffix = SuffixBox.Text.Trim();
        var inputs = Files.Select(f => f.Path).ToList();
        if (PdfBatch.NeedsSuffix(inputs, _outputFolder, suffix))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escreva algo em \"Acrescentar ao nome\"",
                "Os arquivos novos vão para a mesma pasta dos originais e precisam de um nome diferente (ex.: \"-novo\").");
            return;
        }

        var action = SelectedAction;
        var operation = await AskOptionsAsync(action);
        if (operation is null) return;

        var cancel = _cancel = new CancellationTokenSource();
        foreach (var f in Files) { f.Output = null; f.SetStatus("Na fila", "", "TextFillColorSecondaryBrush"); }
        StatusBar.IsOpen = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Progress.Maximum = inputs.Count;
        UpdateState();

        var entries = Files.ToList();
        var progress = new Progress<BatchProgress>(p =>
        {
            Progress.Value = p.Done;
            if (p.Done < entries.Count)
            {
                entries[p.Done].SetStatus("Processando…", "", "AccentTextFillColorPrimaryBrush");
                SummaryText.Text = $"{p.Done + 1} de {p.Total}: {Path.GetFileName(p.CurrentFile)}";
            }
        });

        List<BatchItemResult> results;
        try
        {
            string? folder = _outputFolder;
            results = await Task.Run(() => PdfBatch.Run(inputs, folder, suffix, operation, progress, cancel.Token));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            results = [];
            ShowStatus(InfoBarSeverity.Error, "O lote não pôde ser executado", ex.Message);
        }
        finally
        {
            _cancel = null;
            cancel.Dispose();
            Progress.Visibility = Visibility.Collapsed;
        }

        for (int i = 0; i < entries.Count; i++)
        {
            if (i >= results.Count) { entries[i].SetStatus("Não processado", "", "TextFillColorSecondaryBrush"); continue; }
            var r = results[i];
            entries[i].Output = r.Output;
            if (r.Ok) entries[i].SetStatus(r.Detail is { Length: > 0 } d ? $"Pronto · {d}" : "Pronto", "", "SystemFillColorSuccessBrush");
            else entries[i].SetStatus(r.Error!, "", "SystemFillColorCriticalBrush");
        }
        UpdateState();
        if (results.Count > 0) ShowResult(results, entries.Count);
    }

    private void ShowResult(List<BatchItemResult> results, int total)
    {
        int ok = results.Count(r => r.Ok), failed = results.Count - ok;
        bool stopped = results.Count < total;
        string? folder = _outputFolder ?? (results.FirstOrDefault(r => r.Ok)?.Output is { } first ? Path.GetDirectoryName(first) : null);
        Action? open = folder is null ? null : () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        string message = $"{ok} {(ok == 1 ? "arquivo criado" : "arquivos criados")}" +
                         (failed > 0 ? $", {failed} com erro (veja na lista)" : "") +
                         (stopped ? $", {total - results.Count} não processados" : "") + ".";
        var severity = failed > 0 || stopped ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ShowStatus(severity, stopped ? "Lote parado" : "Lote concluído", message, open is null ? null : "Abrir pasta", open);
    }

    /// <summary>
    /// Mostra as opções da ação (as mesmas janelas do visualizador, com a prévia do primeiro arquivo) e devolve
    /// a operação pronta para cada arquivo. null = cancelado.
    /// </summary>
    private async Task<Func<string, string, string?>?> AskOptionsAsync(BatchAction action)
    {
        if (action.Key == "comprimir")
        {
            var level = CompressLevelBox.SelectedIndex switch { 0 => CompressLevel.Light, 2 => CompressLevel.Strong, _ => CompressLevel.Medium };
            return (input, output) =>
            {
                var r = PdfCompressor.Compress(input, output, level, ImageShrinker.Shrink);
                if (r.After >= r.Before) return "já estava compacto";
                return $"{DocumentPropertiesService.FormatSize(r.Before)} → {DocumentPropertiesService.FormatSize(r.After)}";
            };
        }

        var first = Files[0];
        var (preview, widthPt, heightPt) = await PreviewAsync(first.Path, transparent: action.Key == "fundo");
        int pages = Files.Max(f => f.PageCount);

        switch (action.Key)
        {
            case "marcadagua":
            {
                var fonts = await Task.Run(SystemFonts.Installed);
                var d = Prepare(new WatermarkDialog(pages, 1, preview, widthPt, heightPt, fonts, App.Settings.WatermarkFont, 0));
                if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
                string? ranges = d.Ranges;
                if (d.Remove) return (i, o) => PagesText(PdfWatermark.Remove(i, ranges, o), "sem marca");
                var options = d.Options;
                App.Settings.WatermarkFont = options.FontFamily;
                App.Settings.Save();
                return (i, o) => PagesText(PdfWatermark.Add(i, options, ranges, o), "com marca");
            }
            case "fundo":
            {
                var d = Prepare(new BackgroundDialog(pages, 1, preview, widthPt, heightPt, 0));
                if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
                string? ranges = d.Ranges;
                if (d.Remove) return (i, o) => PagesText(PdfBackground.Remove(i, ranges, o), "sem fundo");
                var options = d.Options;
                return (i, o) => PagesText(PdfBackground.Add(i, options, ranges, o), "com fundo");
            }
            default: // cabeçalho/rodapé e número de página usam a mesma janela
            {
                var fonts = await Task.Run(SystemFonts.Installed);
                var d = Prepare(new HeaderFooterDialog(pages, 1, preview, widthPt, heightPt, fonts, App.Settings.HeaderFooterFont, 0,
                    pageNumberOnly: action.Key == "numeropagina"));
                if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
                string? ranges = d.Ranges;
                if (d.Remove) return (i, o) => PagesText(PdfHeaderFooter.Remove(i, ranges, o), "limpas");
                var options = d.Options;
                App.Settings.HeaderFooterFont = options.FontFamily;
                App.Settings.Save();
                return (i, o) => PagesText(PdfHeaderFooter.Add(i, options, ranges, o), "alteradas");
            }
        }
    }

    private static string PagesText(int pages, string what) => $"{pages} {(pages == 1 ? "página" : "páginas")} {what}";

    /// <summary>Prévia da 1ª página do 1º arquivo, para a janela de opções (sem prévia se não der para abrir).</summary>
    private static async Task<(ImageSource? Image, double WidthPt, double HeightPt)> PreviewAsync(string path, bool transparent)
    {
        try
        {
            var doc = await PdfRenderer.LoadAsync(path);
            using var page = doc.GetPage(0);
            double w = page.Size.Width, h = page.Size.Height;
            var image = await PdfRenderer.RenderAsync(doc, 0, 300 / Math.Max(w, h), transparent);
            return (image, w * 0.75, h * 0.75);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return (null, 595, 842); // A4
        }
    }

    // ---------------- Auxiliares ----------------

    private T Prepare<T>(T dialog) where T : ContentDialog
    {
        dialog.XamlRoot = XamlRoot;
        dialog.RequestedTheme = App.Settings.Theme;
        return dialog;
    }

    private static bool SamePath(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

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
