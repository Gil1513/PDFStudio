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
/// <paramref name="forImages"/>: a ação trabalha com imagens (PNG/JPG), não com PDFs. <paramref name="extension"/>: tipo
/// do arquivo criado ("" = vários arquivos por entrada, ex.: uma imagem por página).
public sealed class BatchAction(string key, string glyph, string title, string description, string suffix,
    bool forImages = false, string extension = ".pdf")
{
    public bool ForImages { get; } = forImages;
    public string Extension { get; } = extension;
    public string Key { get; } = key;
    public string Glyph { get; } = glyph;
    public string Title { get; } = title;
    public string Description { get; } = description;
    public string Suffix { get; } = suffix;
}

/// <summary>Abrir a tela de lote com um arquivo na lista e uma ação já escolhida (ex.: "Impressão em lote").</summary>
public sealed record BatchRequest(string? Path, string Action);

/// <summary>Um arquivo do lote, com a situação dele (esperando, processando, pronto, erro).</summary>
public sealed partial class BatchFileEntry(string path, int pageCount) : INotifyPropertyChanged
{
    private string _statusText = "";
    private string _statusGlyph = "";
    private Brush? _statusBrush;

    public string Path { get; } = path;
    /// <summary>Número de páginas; -1 = não dá para saber (o arquivo tem senha para abrir).</summary>
    public int PageCount { get; } = pageCount;
    public string? Output { get; set; }
    public string Name => System.IO.Path.GetFileName(Path);
    /// <summary>É uma imagem (para "Imagens para PDF"), não um PDF.</summary>
    public bool IsImage => PdfBatch.IsImage(Path);
    /// <summary>Ícone da linha: imagem ou PDF.</summary>
    public string FileGlyph => IsImage ? "" : "";
    public string Details => (IsImage ? "imagem" : PageCount < 0 ? "com senha" : $"{PageCount} {(PageCount == 1 ? "página" : "páginas")}") +
                             $" · {System.IO.Path.GetDirectoryName(Path)}";
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
/// do visualizador, com a prévia do primeiro arquivo). 8.2: colocar/remover senha, assinar (o mesmo .pfx) e imprimir
/// (todos juntos numa só janela de impressão). 8.3: converter para Word e para imagem, OCR e imagens para PDF (a lista
/// aceita imagens; cada ação usa só os arquivos do tipo dela). 8.4: numeração Bates, excluir páginas em branco,
/// formulários para CSV (uma planilha para o lote todo) e renomear (cópias com nome pelo conteúdo). 8.5: dividir (a cada
/// N páginas ou por intervalos) e recortar margens (intervalos que passam do fim de um arquivo são cortados nele).
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
        new("colocarsenha", "", "Colocar senha", "A mesma senha em todos os arquivos.", "-protegido"),
        new("removersenha", "", "Remover senha", "Tira a senha (a mesma em todos).", "-sem-senha"),
        new("assinar", "", "Assinar", "O mesmo certificado .pfx em todos.", "-assinado"),
        new("imprimir", "", "Imprimir", "Todos de uma vez, numa só impressão.", ""),
        new("paraword", "", "Converter para Word", "Um arquivo .docx editável por PDF.", "", extension: ".docx"),
        new("paraimagem", "", "Converter para imagem", "Cada página vira um PNG ou JPG.", "", extension: ""),
        new("ocr", "", "Reconhecer texto (OCR)", "Texto pesquisável nos PDFs escaneados.", "-ocr"),
        new("imagemparapdf", "", "Imagens para PDF", "Cada imagem (PNG ou JPG) vira um PDF.", "", forImages: true),
        new("bates", "", "Numeração Bates", "ABC000001, ABC000002… seguindo de um arquivo para o outro.", "-bates"),
        new("paginasbrancas", "", "Excluir páginas em branco", "Tira as páginas vazias (ex.: verso de escaneados).", "-sem-brancas"),
        new("formcsv", "", "Formulários para planilha", "Os campos preenchidos de todos os PDFs num CSV.", "", extension: ""),
        new("dividir", "", "Dividir", "Cada PDF em partes: a cada N páginas ou por intervalos.", "", extension: ""),
        new("recortar", "", "Recortar", "As mesmas margens cortadas em todos os PDFs.", "-recortado"),
        new("renomear", "", "Renomear", "Nome pelo título, autor, data ou 1ª linha (cópias).", "", extension: ""),
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
        // Vindo do visualizador: o arquivo aberto já entra na lista (e, se pedido, a ação já fica escolhida).
        string? path = e.Parameter switch { string p => p, BatchRequest r => r.Path, _ => null };
        if (e.Parameter is BatchRequest { Action: var key } && Actions.FirstOrDefault(a => a.Key == key) is { } action)
            ActionList.SelectedItem = action;
        if (path is not null && File.Exists(path) && Files.All(f => !SamePath(f.Path, path))) await AddFilesAsync([path]);
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
        SaveOptions.Visibility = action.Key == "imprimir" ? Visibility.Collapsed : Visibility.Visible; // imprimir não cria arquivos
        StartButton.Content = action.Key == "imprimir" ? "Imprimir…" : "Iniciar";
        SuffixBox.Visibility = action.Extension.Length == 0 ? Visibility.Collapsed : Visibility.Visible; // imagens: nome por página
        AddFilesButton.Label = action.ForImages ? "Adicionar imagens" : "Adicionar PDFs";
        if (SuffixBox.Text == _autoSuffix) SuffixBox.Text = action.Suffix; // o usuário não trocou: segue a ação
        _autoSuffix = action.Suffix;
        foreach (var f in Files) f.SetStatus("", "", "TextFillColorSecondaryBrush"); // nova ação: situação zerada
    }

    // ---------------- Arquivos ----------------

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await AddFilesAsync(SelectedAction.ForImages
        ? await FilePickers.PickFilesAsync(ImagesToPdf.Extensions)
        : await FilePickers.PickPdfsAsync());

    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        if (await FilePickers.PickFolderAsync() is not { } folder) return;
        await AddFilesAsync(FilesIn(folder));
    }

    /// <summary>O arquivo é do tipo da ação escolhida (PDF, ou imagem para "Imagens para PDF")?</summary>
    private bool FitsAction(string path) => SelectedAction.ForImages
        ? PdfBatch.IsImage(path)
        : Path.GetExtension(path).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    /// <summary>Os arquivos de uma pasta (só ela, sem as subpastas) do tipo da ação escolhida, em ordem alfabética.</summary>
    private IEnumerable<string> FilesIn(string folder)
    {
        try { return Directory.GetFiles(folder).Where(FitsAction).Order(StringComparer.CurrentCultureIgnoreCase).ToList(); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return []; }
    }

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        int added = 0;
        foreach (var path in paths)
        {
            if (Files.Any(f => SamePath(f.Path, path))) continue; // já está na lista
            if (PdfBatch.IsImage(path))
            {
                Files.Add(new BatchFileEntry(path, 0)); // imagem: vira uma página
                added++;
                continue;
            }
            try
            {
                int count = await Task.Run(() => PdfOperations.GetPageCount(path));
                Files.Add(new BatchFileEntry(path, count));
                added++;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Com senha para abrir não dá para contar as páginas, mas o arquivo entra (ex.: para "Remover senha").
                if (await Task.Run(() => SafeInspect(path)) == PasswordState.OpenPassword)
                {
                    Files.Add(new BatchFileEntry(path, -1));
                    added++;
                }
                else failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        if (failed.Count > 0)
            ShowStatus(InfoBarSeverity.Warning, "Alguns arquivos não foram adicionados", string.Join("\n", failed.Take(8)));
        else if (added == 0 && paths.Any())
            ShowStatus(InfoBarSeverity.Informational, "Nada novo para adicionar", "Esses arquivos já estão na lista.");
    }

    private static PasswordState? SafeInspect(string path)
    {
        try { return PdfPasswords.Inspect(path); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return null; }
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
            if (item is StorageFolder folder) paths.AddRange(FilesIn(folder.Path));
            else if (item is StorageFile file && FitsAction(file.Path)) paths.Add(file.Path);
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
        bool busy = _cancel is not null || _printing;
        EmptyState.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        RemoveButton.IsEnabled = !busy && FilesList.SelectedItems.Count > 0;
        ClearButton.IsEnabled = !busy && Files.Count > 0;
        AddFilesButton.IsEnabled = AddFolderButton.IsEnabled = ActionList.IsEnabled = !busy;
        StartButton.IsEnabled = !busy && Files.Count > 0;
        StopButton.Visibility = _cancel is not null ? Visibility.Visible : Visibility.Collapsed;
        if (!busy)
            SummaryText.Text = Files.Count == 0 ? "" :
                $"{Files.Count} {(Files.Count == 1 ? "arquivo" : "arquivos")}, {Files.Sum(f => Math.Max(0, f.PageCount))} páginas no total" +
                (Files.Count(f => f.IsImage) is > 0 and var images ? $", {images} {(images == 1 ? "imagem" : "imagens")}" : "") +
                (Files.Any(f => f.PageCount < 0) ? " (sem contar os que têm senha)" : "");
    }

    // ---------------- Executar ----------------

    private void Stop_Click(object sender, RoutedEventArgs e) => _cancel?.Cancel();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (Files.Count == 0 || _cancel is not null || _printing) return;
        if (SelectedAction.Key == "imprimir")
        {
            await PrintAllAsync();
            return;
        }
        var action = SelectedAction;
        var entries = Files.Where(f => f.IsImage == action.ForImages).ToList(); // cada ação usa só os arquivos do tipo dela
        if (entries.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "Nenhum arquivo serve para esta ação",
                action.ForImages ? "Adicione imagens (PNG ou JPG)." : "Adicione PDFs.");
            return;
        }
        string suffix = action.Extension.Length == 0 ? "" : SuffixBox.Text.Trim();
        var inputs = entries.Select(f => f.Path).ToList();
        if (action.Extension.Length > 0 && PdfBatch.NeedsSuffix(inputs, _outputFolder, suffix, action.Extension))
        {
            ShowStatus(InfoBarSeverity.Warning, "Escreva algo em \"Acrescentar ao nome\"",
                "Os arquivos novos vão para a mesma pasta dos originais e precisam de um nome diferente (ex.: \"-novo\").");
            return;
        }

        var runner = await AskRunnerAsync(action);
        if (runner is null) return;

        var cancel = _cancel = new CancellationTokenSource();
        foreach (var f in Files.Except(entries))
            f.SetStatus(action.ForImages ? "Fica de fora: não é imagem" : "Fica de fora: não é PDF", "", "TextFillColorSecondaryBrush");
        foreach (var f in entries) { f.Output = null; f.SetStatus("Na fila", "", "TextFillColorSecondaryBrush"); }
        StatusBar.IsOpen = false;
        Progress.Visibility = Visibility.Visible;
        Progress.Value = 0;
        Progress.Maximum = inputs.Count;
        UpdateState();

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
            results = await runner(inputs, folder, suffix, progress, cancel.Token);
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
            if (r.Ok && r.Output is null) entries[i].SetStatus($"Pulado · {r.Detail}", "", "TextFillColorSecondaryBrush");
            else if (r.Ok) entries[i].SetStatus(r.Detail is { Length: > 0 } d ? $"Pronto · {d}" : "Pronto", "", "SystemFillColorSuccessBrush");
            else entries[i].SetStatus(r.Error!, "", "SystemFillColorCriticalBrush");
        }
        UpdateState();
        if (results.Count > 0) ShowResult(results, entries.Count);
    }

    private void ShowResult(List<BatchItemResult> results, int total)
    {
        int ok = results.Count(r => r.Output is not null), skipped = results.Count(r => r.Ok && r.Output is null);
        int failed = results.Count(r => !r.Ok);
        bool stopped = results.Count < total;
        string? folder = _outputFolder ?? (results.FirstOrDefault(r => r.Output is not null)?.Output is { } first ? Path.GetDirectoryName(first) : null);
        Action? open = folder is null ? null : () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true });
        string made = SelectedAction.Key switch
        {
            "paraimagem" => $"{ok} {(ok == 1 ? "PDF convertido" : "PDFs convertidos")} em imagens",
            "dividir" => $"{ok} {(ok == 1 ? "PDF dividido" : "PDFs divididos")} em partes",
            "renomear" => $"{ok} {(ok == 1 ? "cópia renomeada" : "cópias renomeadas")} (os originais ficaram como estavam)",
            "formcsv" when _lastCsv is not null => $"Planilha \"{Path.GetFileName(_lastCsv)}\" criada com {ok} {(ok == 1 ? "linha" : "linhas")}",
            "formcsv" => "Nenhum dos PDFs tem formulário: nenhuma planilha foi criada",
            _ => $"{ok} {(ok == 1 ? "arquivo criado" : "arquivos criados")}",
        };
        string message = made + (skipped > 0 ? $", {skipped} {(skipped == 1 ? "pulado" : "pulados")} (nada a fazer)" : "") +
                         (failed > 0 ? $", {failed} com erro (veja na lista)" : "") +
                         (stopped ? $", {total - results.Count} não processados" : "") + ".";
        var severity = failed > 0 || stopped || ok == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Success;
        ShowStatus(severity, stopped ? "Lote parado" : "Lote concluído", message, open is null ? null : "Abrir pasta", open);
    }

    /// <summary>Roda a ação nos arquivos (em segundo plano) e devolve o resultado de cada um.</summary>
    private delegate Task<List<BatchItemResult>> BatchRunner(IReadOnlyList<string> inputs, string? folder, string suffix,
        IProgress<BatchProgress> progress, CancellationToken cancel);

    /// <summary>Opções da ação e o jeito de rodar (um arquivo por entrada, ou várias imagens por PDF). null = cancelado.</summary>
    private async Task<BatchRunner?> AskRunnerAsync(BatchAction action)
    {
        switch (action.Key)
        {
            case "paraword": return Sync(PdfBatch.ToWord(), ".docx");
            case "imagemparapdf": return await AskImagesToPdfAsync() is { } op ? Sync(op, ".pdf") : null;
            case "paraimagem": return await AskPageImagesAsync();
            case "ocr": return await AskOcrAsync();
            case "bates": return await AskBatesAsync() is { } bates ? Sync(PdfBatch.Bates(bates.Options, bates.First), ".pdf") : null;
            case "paginasbrancas": return await AskBlankPagesAsync();
            case "formcsv": return FormsToCsv();
            case "renomear": return await AskRenameAsync();
            case "dividir": return await AskSplitAsync();
            case "recortar": return await AskCropAsync() is { } crop ? Sync(crop, ".pdf") : null;
            default: return await AskOptionsAsync(action) is { } operation ? Sync(operation, ".pdf") : null;
        }
    }

    private static BatchRunner Sync(Func<string, string, string?> operation, string extension) =>
        (inputs, folder, suffix, progress, cancel) => Task.Run(() => PdfBatch.Run(inputs, folder, suffix, operation, progress, cancel, extension));

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

        switch (action.Key)
        {
            case "colocarsenha":
            {
                var d = Prepare(new ProtectDialog(
                    "Todos os arquivos serão gravados com esta senha (criptografia AES de 128 bits). Guarde a senha: sem ela não é possível recuperar os arquivos.",
                    "Senha atual (só para arquivos que já têm proteção)", showWhere: false));
                if (await d.ShowAsync() != ContentDialogResult.Primary || d.Options is not { } options) return null;
                return PdfBatch.Protect(options, d.CurrentPassword);
            }
            case "removersenha":
                return await AskRemovalPasswordAsync() is { } password ? PdfBatch.RemovePassword(password) : null;
            case "assinar":
                return await AskSignatureAsync() is { } sign ? PdfBatch.Sign(sign.Request, sign.Page) : null;
        }

        var first = Files.FirstOrDefault(f => f.PageCount >= 0) ?? Files[0]; // prévia: o primeiro que abre sem senha
        var (preview, widthPt, heightPt) = await PreviewAsync(first.Path, transparent: action.Key == "fundo");
        int pages = Math.Max(1, Files.Max(f => f.PageCount));

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

    // ---------------- Converter, OCR e imagens para PDF (Etapa 8.3) ----------------

    /// <summary>Erro claro para PDF com senha (o motor do Windows daria uma mensagem confusa).</summary>
    private static void RefuseLocked(string input, bool needsCopy = false)
    {
        if (SafeInspect(input) == PasswordState.OpenPassword)
            throw new PdfPasswordException("Tem senha para abrir: remova a senha antes.");
        if (needsCopy && !PdfPasswords.Permissions(input).Copy)
            throw new PdfPasswordException("Quem criou o PDF não permite copiar o conteúdo.");
    }

    private async Task<Func<string, string, string?>?> AskImagesToPdfAsync()
    {
        // Mesma ordem dos valores de ImagePageSize e ImageMargin.
        var size = new ComboBox { Header = "Tamanho da página", MinWidth = 200, Items = { "Tamanho da imagem", "A4", "Carta" }, SelectedIndex = 1 };
        var margin = new ComboBox { Header = "Margem", MinWidth = 160, Items = { "Sem margem", "Pequena (1 cm)", "Grande (2 cm)" }, SelectedIndex = 0 };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Imagens para PDF",
            Content = new StackPanel
            {
                Spacing = 12,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Cada imagem vira um PDF de uma página, com o mesmo nome (foto.jpg → foto.pdf). Para juntar várias imagens num PDF só, use \"Imagem para PDF\" na tela inicial.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { size, margin } },
                },
            },
            PrimaryButtonText = "Criar PDFs",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return PdfBatch.ImageToPdf((ImagePageSize)size.SelectedIndex, (ImageMargin)margin.SelectedIndex);
    }

    /// <summary>PDF→imagem: formato e qualidade; todas as páginas de cada PDF, com o nome "contrato-pagina-01.png".</summary>
    private async Task<BatchRunner?> AskPageImagesAsync()
    {
        var png = new RadioButton { Content = "PNG (sem perda, melhor para texto)", IsChecked = true, GroupName = "loteformato" };
        var jpg = new RadioButton { Content = "JPG (arquivo menor, melhor para fotos)", GroupName = "loteformato" };
        var resolution = new ComboBox { Header = "Qualidade", MinWidth = 260, Items = { "Tela (96 dpi)", "Padrão (150 dpi)", "Impressão (300 dpi)" }, SelectedIndex = 1 };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Converter para imagem",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Todas as páginas de cada PDF viram imagens, com nomes como \"contrato-pagina-01.png\". Nenhum arquivo existente é substituído.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    png, jpg, resolution,
                },
            },
            PrimaryButtonText = "Converter",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var format = jpg.IsChecked == true ? PageImageFormat.Jpeg : PageImageFormat.Png;
        int dpi = PageImages.Resolutions[Math.Max(0, resolution.SelectedIndex)];

        return (inputs, folder, _, progress, cancel) => Task.Run(() => PdfBatch.RunToFolderAsync(inputs, folder, async (input, temp, token) =>
        {
            RefuseLocked(input, needsCopy: true);
            var doc = await PdfRenderer.LoadAsync(input);
            int count = (int)doc.PageCount;
            string name = Path.GetFileName(input);
            for (int i = 0; i < count; i++)
            {
                token.ThrowIfCancellationRequested();
                await PdfRenderer.SavePageImageAsync(doc, i, dpi, format, Path.Combine(temp, PageImages.FileName(name, i + 1, count, format)));
            }
            return $"{count} {(count == 1 ? "imagem" : "imagens")}";
        }, progress, cancel));
    }

    /// <summary>OCR em lote: idioma e quais páginas. Cada PDF ganha uma cópia com o texto (arquivo já com texto é pulado).</summary>
    private async Task<BatchRunner?> AskOcrAsync()
    {
        var languages = OcrService.Languages;
        if (languages.Count == 0)
        {
            ShowStatus(InfoBarSeverity.Warning, "O Windows não tem o reconhecimento de texto instalado",
                "Em Configurações → Hora e idioma → Idioma e região, abra as opções de Português (Brasil) e instale " +
                "\"Reconhecimento óptico de caracteres\". Depois tente de novo.");
            return null;
        }
        var language = new ComboBox { Header = "Idioma do texto", MinWidth = 260 };
        foreach (var l in languages) language.Items.Add(l.DisplayName);
        string? preferred = OcrService.DefaultLanguage?.LanguageTag;
        language.SelectedIndex = Math.Max(0, languages.ToList().FindIndex(l => l.LanguageTag == preferred));
        var onlyScanned = new RadioButton { GroupName = "loteocr", Content = "Só as páginas sem texto (recomendado)", IsChecked = true };
        var all = new RadioButton { GroupName = "loteocr", Content = "Todas as páginas" };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Reconhecer texto (OCR)",
            Content = new StackPanel
            {
                Spacing = 8,
                Children =
                {
                    new TextBlock
                    {
                        Text = "O texto das páginas escaneadas passa a poder ser pesquisado, selecionado e copiado; a aparência não muda. " +
                               "O reconhecimento é feito no seu computador. PDFs que já têm texto em todas as páginas são pulados.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 440, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    language, onlyScanned, all,
                },
            },
            PrimaryButtonText = "Reconhecer",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        string tag = languages[Math.Max(0, language.SelectedIndex)].LanguageTag;
        bool onlyMissing = onlyScanned.IsChecked == true;

        return (inputs, folder, suffix, progress, cancel) => Task.Run(() => PdfBatch.RunAsync(inputs, folder, suffix, async (input, output, token) =>
        {
            RefuseLocked(input);
            var pages = onlyMissing ? PdfOcr.PagesWithoutText(input) : Enumerable.Range(0, PdfOperations.GetPageCount(input)).ToList();
            if (pages.Count == 0) return "já tem texto em todas as páginas";
            var engine = Windows.Media.Ocr.OcrEngine.TryCreateFromLanguage(new Windows.Globalization.Language(tag))
                ?? throw new InvalidOperationException("O Windows não abriu o reconhecimento para esse idioma.");
            var doc = await PdfRenderer.LoadAsync(input);
            var found = new Dictionary<int, IReadOnlyList<OcrWord>>();
            int words = 0;
            foreach (int page in pages)
            {
                token.ThrowIfCancellationRequested();
                var list = await OcrService.RecognizeAsync(doc, page, engine);
                if (list.Count > 0) found[page] = list;
                words += list.Count;
            }
            if (words == 0) return "nenhum texto encontrado";
            PdfOcr.AddTextLayer(input, found, output);
            return $"{words} palavras em {found.Count} {(found.Count == 1 ? "página" : "páginas")}";
        }, progress, cancel));
    }

    // ---------------- Bates, páginas em branco, formulários e renomear (Etapa 8.4) ----------------

    private sealed record BatesChoice(BatesOptions Options, long First);

    /// <summary>Prefixo, algarismos, número inicial, posição e tamanho, com o exemplo do primeiro número.</summary>
    private async Task<BatesChoice?> AskBatesAsync()
    {
        var prefix = new TextBox { Header = "Prefixo", PlaceholderText = "ex.: ABC", Width = 170, MaxLength = 20, Text = App.Settings.BatesPrefix ?? "" };
        var suffix = new TextBox { Header = "Final (opcional)", Width = 170, MaxLength = 20 };
        var digits = new NumberBox { Header = "Algarismos", Minimum = 1, Maximum = PdfBates.MaxDigits, Value = 6, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 170 };
        var first = new NumberBox { Header = "Começar em", Minimum = 1, Maximum = 999_999_999_999, Value = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 170 };
        // Mesma ordem de BatesPosition.
        var position = new ComboBox { Header = "Posição", MinWidth = 170, SelectedIndex = 0, Items = { "Inferior direito", "Inferior centro", "Inferior esquerdo", "Superior direito", "Superior centro", "Superior esquerdo" } };
        var size = new NumberBox { Header = "Tamanho da letra", Minimum = 6, Maximum = 36, Value = 10, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 170 };
        var example = new TextBlock { FontFamily = new FontFamily("Consolas"), FontSize = 16, IsTextSelectionEnabled = true };
        BatesOptions Options() => new(prefix.Text.Trim(), double.IsNaN(digits.Value) ? 6 : (int)digits.Value, suffix.Text.Trim(),
            (BatesPosition)Math.Max(0, position.SelectedIndex), FontSize: double.IsNaN(size.Value) ? 10 : size.Value);
        long First() => double.IsNaN(first.Value) ? 1 : (long)first.Value;
        int pages = Files.Sum(f => Math.Max(0, f.PageCount));
        void Update()
        {
            var o = Options();
            example.Text = pages > 1 ? $"{PdfBates.Label(o, First())} … {PdfBates.Label(o, First() + pages - 1)}" : PdfBates.Label(o, First());
        }
        prefix.TextChanged += (_, _) => Update();
        suffix.TextChanged += (_, _) => Update();
        digits.ValueChanged += (_, _) => Update();
        first.ValueChanged += (_, _) => Update();
        Update();
        var dialog = Prepare(new ContentDialog
        {
            Title = "Numeração Bates",
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Cada página recebe um número único, que continua de um arquivo para o outro na ordem da lista. Rodar de novo troca a numeração antiga (não acumula).",
                            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                        },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { prefix, suffix } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { digits, first } },
                        new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { position, size } },
                        new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Exemplo", Opacity = 0.7, FontSize = 12 }, example } },
                    },
                },
            },
            PrimaryButtonText = "Numerar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        App.Settings.BatesPrefix = prefix.Text.Trim();
        App.Settings.Save();
        return new BatesChoice(Options(), First());
    }

    /// <summary>Tolerância (quanto de "sujeira" uma página pode ter e ainda contar como em branco).</summary>
    private async Task<BatchRunner?> AskBlankPagesAsync()
    {
        var tolerance = new Slider
        {
            Header = "Tolerância", Minimum = 0, Maximum = 1, StepFrequency = 0.05, Value = BlankPages.DefaultTolerancePercent,
            Width = 360, HorizontalAlignment = HorizontalAlignment.Left,
        };
        var value = new TextBlock { Opacity = 0.8 };
        void Update() => value.Text = tolerance.Value == 0
            ? "0%: só páginas totalmente brancas"
            : $"{tolerance.Value.ToString("0.##", System.Globalization.CultureInfo.CurrentCulture)}% da página pode ter manchas ou pontinhos";
        tolerance.ValueChanged += (_, _) => Update();
        Update();
        var dialog = Prepare(new ContentDialog
        {
            Title = "Excluir páginas em branco",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Cada PDF ganha uma cópia sem as páginas em branco. As bordas da folha (sombra do scanner) não contam. " +
                               "Se páginas com pouco texto sumirem, diminua a tolerância; se versos escaneados ficarem, aumente.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    tolerance, value,
                },
            },
            PrimaryButtonText = "Excluir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        double tol = tolerance.Value;
        var operation = PdfBatch.RemoveBlankPages(async (input, token) =>
        {
            RefuseLocked(input);
            return await PdfRenderer.InkRatiosAsync(input, token);
        }, tol);
        return (inputs, folder, suffix, progress, cancel) => Task.Run(() => PdfBatch.RunAsync(inputs, folder, suffix, operation, progress, cancel));
    }

    private string? _lastCsv; // planilha criada pelo último "Formulários para planilha"

    /// <summary>
    /// Uma planilha só para o lote todo. Na lista, cada PDF que entrou nela aparece "Pronto" com o número de campos
    /// (e "Abrir pasta" leva à planilha).
    /// </summary>
    private BatchRunner FormsToCsv() => async (inputs, folder, _, progress, cancel) =>
    {
        _lastCsv = null;
        var (results, csv) = await Task.Run(() => PdfBatch.ExportFormsCsv(inputs, folder, progress, cancel));
        _lastCsv = csv;
        return csv is null ? results : results.Select(r => r.Ok && r.Detail != "sem formulário" ? r with { Output = csv } : r).ToList();
    };

    /// <summary>Modelo do nome, com o exemplo do primeiro arquivo.</summary>
    private async Task<BatchRunner?> AskRenameAsync()
    {
        var template = new TextBox { Header = "Modelo do nome", Text = App.Settings.RenameTemplate ?? "{titulo}", Width = 420, HorizontalAlignment = HorizontalAlignment.Left };
        var example = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = 420, IsTextSelectionEnabled = true };
        var first = Files.FirstOrDefault(f => !f.IsImage && f.PageCount >= 0);
        RenameInfo? info = null;
        if (first is not null)
            try { string path = first.Path; info = await Task.Run(() => PdfRename.Info(path)); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
        int total = Files.Count(f => !f.IsImage);
        void Update() => example.Text = info is null ? "" : $"{info.FileName} → {PdfRename.NameFor(template.Text, info, 1, total)}.pdf";
        template.TextChanged += (_, _) => Update();
        Update();
        var dialog = Prepare(new ContentDialog
        {
            Title = "Renomear",
            Content = new ScrollViewer
            {
                Content = new StackPanel
                {
                    Spacing = 10,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "Cada PDF ganha uma cópia com o novo nome; os originais ficam como estão. Nome repetido ganha \" (2)\".",
                            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                        },
                        template,
                        new TextBlock
                        {
                            Text = "{titulo}  título do documento\n{autor}  autor\n{data}  data de criação (2025-03-14)\n" +
                                   "{linha}  primeira linha da 1ª página\n{nome}  nome atual\n{n}  número na lista (001, 002…)",
                            FontFamily = new FontFamily("Consolas"), FontSize = 12, Opacity = 0.8,
                        },
                        new TextBlock
                        {
                            Text = "Se uma informação faltar (ex.: PDF sem título), fica o nome atual.",
                            TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                        },
                        new StackPanel { Spacing = 4, Children = { new TextBlock { Text = "Exemplo", Opacity = 0.7, FontSize = 12 }, example } },
                    },
                },
            },
            PrimaryButtonText = "Renomear",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = string.IsNullOrWhiteSpace(template.Text);
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        string chosen = template.Text.Trim();
        App.Settings.RenameTemplate = chosen;
        App.Settings.Save();
        return (inputs, folder, _, progress, cancel) => Task.Run(() => PdfBatch.CopyRenamed(inputs, folder, chosen, progress, cancel));
    }

    // ---------------- Dividir e recortar (Etapa 8.5) ----------------

    /// <summary>A cada N páginas, ou pelos mesmos intervalos em todos os arquivos. As partes vão para a pasta de saída.</summary>
    private async Task<BatchRunner?> AskSplitAsync()
    {
        var every = new RadioButton { GroupName = "lotedividir", Content = "A cada", IsChecked = true, MinWidth = 0 };
        var count = new NumberBox { Minimum = 1, Maximum = 9999, Value = 1, SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline, Width = 130 };
        var byRanges = new RadioButton { GroupName = "lotedividir", Content = "Por intervalos (os mesmos em todos os arquivos)" };
        var ranges = new TextBox { PlaceholderText = "Ex.: 1-2, 3-", Width = 300, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = false };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed,
        };
        every.Checked += (_, _) => { count.IsEnabled = true; ranges.IsEnabled = false; };
        byRanges.Checked += (_, _) => { count.IsEnabled = false; ranges.IsEnabled = true; };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Dividir em lote",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Cada PDF vira vários arquivos, com nomes como \"contrato_paginas_1-2.pdf\". Os originais ficam como estão.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal, Spacing = 8,
                        Children = { every, count, new TextBlock { Text = "página(s)", VerticalAlignment = VerticalAlignment.Center } },
                    },
                    byRanges, ranges,
                    new TextBlock
                    {
                        Text = "\"3-\" vai da 3ª página até o fim. Num arquivo menor, o que passa do fim é ignorado. " +
                               "Um PDF que daria uma parte só, com todas as páginas, é pulado.",
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    error,
                },
            },
            PrimaryButtonText = "Dividir",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        dialog.PrimaryButtonClick += (_, args) =>
        {
            if (byRanges.IsChecked != true) return;
            try { PageRanges.ParseClipped(ranges.Text, 0); }
            catch (FormatException ex)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        var operation = byRanges.IsChecked == true
            ? PdfBatch.Split(0, ranges.Text.Trim())
            : PdfBatch.Split(double.IsNaN(count.Value) ? 1 : (int)count.Value, null);
        return (inputs, folder, _, progress, cancel) => Task.Run(() => PdfBatch.RunToFolderAsync(inputs, folder, operation, progress, cancel));
    }

    /// <summary>A mesma janela de recorte do visualizador, com a prévia do primeiro arquivo.</summary>
    private async Task<Func<string, string, string?>?> AskCropAsync()
    {
        var first = Files.FirstOrDefault(f => !f.IsImage && f.PageCount >= 0) ?? Files[0];
        var (preview, widthPt, heightPt) = await PreviewAsync(first.Path, transparent: false);
        int pages = Math.Max(1, Files.Max(f => f.PageCount));
        var d = Prepare(new CropPagesDialog(pages, 1, isSigned: false, preview, widthPt, heightPt, batch: true));
        if (await d.ShowAsync() != ContentDialogResult.Primary) return null;
        string? ranges = d.Ranges;
        return d.RemoveCrop ? PdfBatch.RemoveCrop(ranges) : PdfBatch.Crop(d.Margins, ranges);
    }

    // ---------------- Senha, assinatura e impressão (Etapa 8.2) ----------------

    private async Task<string?> AskRemovalPasswordAsync()
    {
        var box = new PasswordBox { Header = "Senha", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Remover senha",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Digite a senha dos arquivos. Se tiverem também uma senha de permissões, use essa. Ela é tentada em todos; os arquivos com outra senha aparecem com erro na lista.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    box,
                },
            },
            PrimaryButtonText = "Remover",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        dialog.PrimaryButtonClick += (_, args) => args.Cancel = box.Password.Length == 0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Password : null;
    }

    private sealed record SignChoice(SignatureRequest Request, BatchSignaturePage Page);

    /// <summary>Certificado (.pfx e senha, conferidos antes de começar), visível ou não, página, canto, motivo e local.</summary>
    private async Task<SignChoice?> AskSignatureAsync()
    {
        string? pfx = App.Settings.LastPfxPath is { } last && File.Exists(last) ? last : null;
        var pfxText = new TextBlock { Text = pfx is null ? "Nenhum certificado escolhido" : Path.GetFileName(pfx), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, MaxWidth = 260 };
        var pick = new Button { Content = "Escolher .pfx…" };
        var password = new PasswordBox { Header = "Senha do certificado", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var kind = new RadioButtons
        {
            Header = "Tipo",
            SelectedIndex = 1,
            Items = { new RadioButton { Content = "Invisível (só nos dados do PDF)" }, new RadioButton { Content = "Visível (carimbo na página)" } },
        };
        var page = new ComboBox { Header = "Página", Items = { "Primeira de cada arquivo", "Última de cada arquivo" }, SelectedIndex = 1, MinWidth = 220 };
        var corner = new ComboBox { Header = "Canto", Items = { "Inferior direito", "Inferior esquerdo", "Superior direito", "Superior esquerdo" }, SelectedIndex = 0, MinWidth = 180 };
        var place = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { page, corner } };
        kind.SelectionChanged += (_, _) => place.Visibility = kind.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        var reason = new TextBox { Header = "Razão (opcional)", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var location = new TextBox { Header = "Localização (opcional)", Width = 360, HorizontalAlignment = HorizontalAlignment.Left };
        var error = new TextBlock
        {
            Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"],
            TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left, Visibility = Visibility.Collapsed,
        };
        pick.Click += async (_, _) =>
        {
            if (await FilePickers.PickFileAsync(".pfx", ".p12") is not { } chosen) return;
            pfx = chosen;
            pfxText.Text = Path.GetFileName(chosen);
            error.Visibility = Visibility.Collapsed;
        };
        var panel = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { pick, pfxText } },
                password, kind, place, reason, location,
                new TextBlock
                {
                    Text = "Arquivos que já têm assinatura são pulados: gravá-los de novo invalidaria a assinatura anterior.",
                    TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                },
                error,
            },
        };
        var dialog = Prepare(new ContentDialog
        {
            Title = "Assinar em lote",
            Content = new ScrollViewer { Content = panel },
            PrimaryButtonText = "Assinar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });

        CertificateSummary? cert = null;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            if (pfx is null)
            {
                error.Text = "Escolha o arquivo do certificado (.pfx).";
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
                return;
            }
            var deferral = args.GetDeferral();
            string file = pfx, pass = password.Password;
            try
            {
                cert = await Task.Run(() => PdfSigning.InspectCertificate(file, pass));
                string? problem = !cert.HasPrivateKey ? "Este certificado não contém a chave privada e não pode ser usado para assinar."
                    : !cert.IsValidNow ? $"O certificado está fora do prazo de validade ({cert.NotBefore:dd/MM/yyyy} a {cert.NotAfter:dd/MM/yyyy})."
                    : null;
                if (problem is not null)
                {
                    error.Text = problem;
                    error.Visibility = Visibility.Visible;
                    args.Cancel = true;
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                error.Text = ex.Message;
                error.Visibility = Visibility.Visible;
                args.Cancel = true;
            }
            finally
            {
                deferral.Complete();
            }
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary || cert is null || pfx is null) return null;

        App.Settings.LastPfxPath = pfx;
        App.Settings.Save();
        var request = new SignatureRequest
        {
            InputPath = "",
            OutputPath = "",
            PfxPath = pfx,
            PfxPassword = password.Password,
            Visible = kind.SelectedIndex == 1,
            Position = (SignaturePosition)corner.SelectedIndex,
            Reason = string.IsNullOrWhiteSpace(reason.Text) ? null : reason.Text.Trim(),
            Location = string.IsNullOrWhiteSpace(location.Text) ? null : location.Text.Trim(),
        };
        return new SignChoice(request, page.SelectedIndex == 0 ? BatchSignaturePage.First : BatchSignaturePage.Last);
    }

    private bool _printing;

    /// <summary>
    /// Imprimir em lote: confere cada arquivo (senha, permissão de imprimir), junta os que podem num PDF temporário e
    /// abre uma única janela de impressão do Windows (impressora, cópias, frente e verso valem para todos).
    /// </summary>
    private async Task PrintAllAsync()
    {
        var newSheet = new CheckBox { Content = "Cada arquivo começa numa folha nova (para imprimir frente e verso)", IsChecked = true };
        var ask = Prepare(new ContentDialog
        {
            Title = $"Imprimir {Files.Count} {(Files.Count == 1 ? "arquivo" : "arquivos")}",
            Content = new StackPanel
            {
                Spacing = 10,
                Children =
                {
                    new TextBlock
                    {
                        Text = "Os arquivos vão juntos, na ordem da lista, para uma só impressão. Na tela seguinte você escolhe a impressora, as cópias, o papel e frente e verso.",
                        TextWrapping = TextWrapping.Wrap, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                    newSheet,
                    new TextBlock
                    {
                        Text = "Com essa opção, um arquivo com número ímpar de páginas ganha uma página em branco no fim (só na impressão).",
                        TextWrapping = TextWrapping.Wrap, Opacity = 0.7, FontSize = 12, MaxWidth = 420, HorizontalAlignment = HorizontalAlignment.Left,
                    },
                },
            },
            PrimaryButtonText = "Continuar",
            CloseButtonText = "Cancelar",
            DefaultButton = ContentDialogButton.Primary,
        });
        if (await ask.ShowAsync() != ContentDialogResult.Primary) return;

        _printing = true;
        UpdateState();
        StatusBar.IsOpen = false;
        string? merged = null;
        bool started = false; // a janela de impressão abriu: o fim dela libera a tela e apaga o temporário
        try
        {
            var entries = Files.ToList();
            var ok = new List<string>();
            foreach (var f in entries)
            {
                string? problem = await Task.Run(() => PdfBatch.CannotPrint(f.Path));
                if (problem is null)
                {
                    ok.Add(f.Path);
                    f.SetStatus("Na impressão", "", "AccentTextFillColorPrimaryBrush");
                }
                else f.SetStatus(problem, "", "SystemFillColorCriticalBrush");
            }
            if (ok.Count == 0)
            {
                ShowStatus(InfoBarSeverity.Warning, "Nada para imprimir", "Nenhum dos arquivos pode ser impresso (veja o motivo na lista).");
                return;
            }

            merged = Path.Combine(PdfPrintJob.TempFolder(), $"lote-{Guid.NewGuid():N}.pdf");
            bool pad = newSheet.IsChecked == true;
            string target = merged;
            int pages = await Task.Run(() => PdfBatch.MergeForPrint(ok, target, pad));
            SummaryText.Text = $"{ok.Count} {(ok.Count == 1 ? "arquivo" : "arquivos")}, {pages} páginas na impressão";

            int skipped = entries.Count - ok.Count;
            var job = new PdfPrintJob(merged, null, $"PDFs em lote ({ok.Count} {(ok.Count == 1 ? "arquivo" : "arquivos")})", DispatcherQueue, (severity, title, message) =>
                ShowStatus(severity, title, skipped > 0 && severity == InfoBarSeverity.Success
                    ? $"{message} {skipped} {(skipped == 1 ? "arquivo ficou" : "arquivos ficaram")} de fora (veja na lista)." : message));
            string temp = merged;
            started = true;
            var included = entries.Where(f => ok.Contains(f.Path)).ToList();
            job.Ended += () =>
            {
                bool sent = job.Completion == Windows.Graphics.Printing.PrintTaskCompletion.Submitted;
                foreach (var f in included)
                    if (sent) f.SetStatus("Enviado para a impressora", "", "SystemFillColorSuccessBrush");
                    else f.SetStatus("", "", "TextFillColorSecondaryBrush"); // cancelado: nada foi impresso
                _printing = false;
                UpdateState();
                try { File.Delete(temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            };
            await job.ShowAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível preparar a impressão", ex.Message);
        }
        finally
        {
            if (!started)
            {
                _printing = false;
                UpdateState();
                if (merged is not null) try { File.Delete(merged); } catch (IOException) { } catch (UnauthorizedAccessException) { }
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
