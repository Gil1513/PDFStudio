using System.Collections.ObjectModel;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;

namespace PDFStudio.Pages;

/// <summary>Um PDF da lista de "Juntar".</summary>
public sealed class PdfFileEntry(string path, int pageCount)
{
    public string Path { get; } = path;
    public int PageCount { get; } = pageCount;
    public string Name => System.IO.Path.GetFileName(Path);
    public string Details => $"{PageCount} {(PageCount == 1 ? "página" : "páginas")} · {System.IO.Path.GetDirectoryName(Path)}";
}

/// <summary>Função 2: juntar vários PDFs e dividir um PDF.</summary>
public sealed partial class MergeSplitPage : Page
{
    public ObservableCollection<PdfFileEntry> Files { get; } = [];

    private string? _splitFile;
    private Action? _statusAction;

    public MergeSplitPage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Files.CollectionChanged += (_, _) => UpdateMergeState();
    }

    // =====================================================================
    // Alternância Juntar / Dividir
    // =====================================================================

    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // A tela inicial abre direto na aba certa ("Combinar PDFs" ou "Dividir PDF").
        if (e.Parameter is "split") ModeSelector.SelectedItem = SplitTab;
        else if (e.Parameter is "merge") ModeSelector.SelectedItem = MergeTab;
    }

    private void ModeSelector_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        bool merge = sender.SelectedItem == MergeTab;
        MergePanel.Visibility = merge ? Visibility.Visible : Visibility.Collapsed;
        SplitPanel.Visibility = merge ? Visibility.Collapsed : Visibility.Visible;
        StatusBar.IsOpen = false;
    }

    // =====================================================================
    // JUNTAR
    // =====================================================================

    private async void AddFiles_Click(object sender, RoutedEventArgs e) => await AddFilesAsync(await FilePickers.PickPdfsAsync());

    private async Task AddFilesAsync(IEnumerable<string> paths)
    {
        var failed = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                int count = await Task.Run(() => PdfOperations.GetPageCount(path));
                Files.Add(new PdfFileEntry(path, count));
            }
            catch (Exception ex)
            {
                failed.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }
        if (failed.Count > 0)
            ShowStatus(InfoBarSeverity.Warning, "Alguns arquivos não foram adicionados", string.Join("\n", failed));
    }

    private void Files_DragOver(object sender, DragEventArgs e)
    {
        // Arquivos vindos do Explorer. (A reordenação interna é tratada pela própria ListView.)
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "Adicionar à lista";
        }
    }

    private async void Files_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        var items = await e.DataView.GetStorageItemsAsync();
        await AddFilesAsync(items.OfType<StorageFile>()
            .Where(f => f.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
            .Select(f => f.Path));
    }

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateMergeState();

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (FilesList.SelectedItem is not PdfFileEntry item) return;
        int from = Files.IndexOf(item), to = from + delta;
        if (to < 0 || to >= Files.Count) return;
        Files.Move(from, to);
        FilesList.SelectedItem = item;
    }

    private void Remove_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in FilesList.SelectedItems.Cast<PdfFileEntry>().ToList()) Files.Remove(item);
    }

    private void Clear_Click(object sender, RoutedEventArgs e) => Files.Clear();

    private void UpdateMergeState()
    {
        int selected = FilesList.SelectedItems.Count;
        int index = FilesList.SelectedIndex;
        MoveUpButton.IsEnabled = selected == 1 && index > 0;
        MoveDownButton.IsEnabled = selected == 1 && index < Files.Count - 1;
        RemoveButton.IsEnabled = selected > 0;
        ClearButton.IsEnabled = Files.Count > 0;
        MergeButton.IsEnabled = Files.Count >= 2 && !MergeProgress.IsActive;
        MergeEmpty.Visibility = Files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        int pages = Files.Sum(f => f.PageCount);
        MergeSummary.Text = Files.Count == 0
            ? "Nenhum arquivo"
            : $"{Files.Count} {(Files.Count == 1 ? "arquivo" : "arquivos")} · {pages} páginas no total" +
              (Files.Count == 1 ? " · adicione ao menos mais um" : "");
    }

    private async void Merge_Click(object sender, RoutedEventArgs e)
    {
        string? output = await FilePickers.SavePdfAsync("documento-juntado");
        if (output is null) return;

        var inputs = Files.Select(f => f.Path).ToList();
        SetBusy(MergeButton, MergeProgress, true);
        try
        {
            int pages = await Task.Run(() => PdfOperations.Merge(inputs, output));
            ShowStatus(InfoBarSeverity.Success, "PDFs juntados",
                $"{inputs.Count} arquivos → {Path.GetFileName(output)} ({pages} páginas).",
                "Abrir no visualizador", () => App.MainWindow!.OpenInViewer(output));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível juntar", ex.Message);
        }
        finally
        {
            SetBusy(MergeButton, MergeProgress, false);
            UpdateMergeState();
        }
    }

    // =====================================================================
    // DIVIDIR
    // =====================================================================

    private async void PickSplitFile_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is null) return;
        try
        {
            int count = await Task.Run(() => PdfOperations.GetPageCount(path));
            _splitFile = path;
            SplitFileText.Text = $"{Path.GetFileName(path)} · {count} {(count == 1 ? "página" : "páginas")}";
            SplitFolderBox.Text = Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "_dividido");
            SplitButton.IsEnabled = true;
            StatusBar.IsOpen = false;
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível abrir o PDF", ex.Message);
        }
    }

    private void SplitMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RangesBox is null) return; // evento disparado durante InitializeComponent
        RangesBox.IsEnabled = SplitModeRadios.SelectedIndex == 1;
        if (RangesBox.IsEnabled) RangesBox.Focus(FocusState.Programmatic);
    }

    private async void PickSplitFolder_Click(object sender, RoutedEventArgs e)
    {
        string? folder = await FilePickers.PickFolderAsync();
        if (folder is not null) SplitFolderBox.Text = folder;
    }

    private async void Split_Click(object sender, RoutedEventArgs e)
    {
        if (_splitFile is null) return;
        string input = _splitFile, folder = SplitFolderBox.Text, ranges = RangesBox.Text;
        bool byRanges = SplitModeRadios.SelectedIndex == 1;

        SetBusy(SplitButton, SplitProgress, true);
        try
        {
            var files = await Task.Run(() => byRanges
                ? PdfOperations.SplitByRanges(input, ranges, folder)
                : PdfOperations.SplitEveryPage(input, folder));
            ShowStatus(InfoBarSeverity.Success, "PDF dividido",
                $"{files.Count} {(files.Count == 1 ? "arquivo criado" : "arquivos criados")} em {folder}.",
                "Abrir pasta", () => Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"")));
        }
        catch (FormatException ex)
        {
            ShowStatus(InfoBarSeverity.Warning, "Intervalo inválido", ex.Message);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível dividir", ex.Message);
        }
        finally
        {
            SetBusy(SplitButton, SplitProgress, false);
        }
    }

    // =====================================================================
    // Comum
    // =====================================================================

    private static void SetBusy(Button button, ProgressRing ring, bool busy)
    {
        ring.IsActive = busy;
        button.IsEnabled = !busy;
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
