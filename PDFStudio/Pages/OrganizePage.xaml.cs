using System.Collections.ObjectModel;
using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

/// <summary>Uma miniatura na tela "Organizar páginas".</summary>
public sealed partial class PageItem : INotifyPropertyChanged
{
    private BitmapImage? _thumbnail;
    private int _rotation;
    private int _position;

    public PageItem(int sourceIndex) { SourceIndex = sourceIndex; }

    public int SourceIndex { get; }
    public BitmapImage? Thumbnail { get => _thumbnail; set { _thumbnail = value; Notify(nameof(Thumbnail)); } }

    /// <summary>Rotação extra (0/90/180/270) a aplicar ao salvar.</summary>
    public int Rotation { get => _rotation; set { _rotation = value; Notify(nameof(Rotation), nameof(DetailText), nameof(AccessibleName)); } }
    public int Position { get => _position; set { _position = value; Notify(nameof(Position), nameof(PositionText), nameof(AccessibleName)); } }

    public string PositionText => $"{Position}";
    public string DetailText => Rotation == 0 ? $"Original: pág. {SourceIndex + 1}" : $"Original: pág. {SourceIndex + 1} · {Rotation}°";
    public string AccessibleName => $"Posição {Position}, página original {SourceIndex + 1}" + (Rotation != 0 ? $", girada {Rotation} graus" : "");

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Notify(params string[] names) { foreach (var n in names) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(n)); }
}

/// <summary>Função 3: girar e reordenar páginas.</summary>
public sealed partial class OrganizePage : Page
{
    private const double ThumbnailScale = 0.3;

    public ObservableCollection<PageItem> Pages { get; } = [];
    private string? _file;
    private Action? _statusAction;
    private int _loadVersion;

    public OrganizePage()
    {
        InitializeComponent();
        NavigationCacheMode = NavigationCacheMode.Required;
        Pages.CollectionChanged += (_, _) => { Renumber(); UpdateState(); };
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);
        // Aberto pelo visualizador: já carrega o arquivo que estava aberto.
        if (e.Parameter is string path && path.Length > 0 && !string.Equals(path, _file, StringComparison.OrdinalIgnoreCase))
            await LoadAsync(path);
    }

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is not null) await LoadAsync(path);
    }

    private async Task LoadAsync(string path)
    {
        int version = ++_loadVersion;
        StatusBar.IsOpen = false;
        LoadingRing.IsActive = true;
        EmptyState.Visibility = Visibility.Collapsed;
        try
        {
            var doc = await PdfRenderer.LoadAsync(path);
            _file = path;
            Pages.Clear();
            for (int i = 0; i < doc.PageCount; i++) Pages.Add(new PageItem(i));
            LoadingRing.IsActive = false;
            UpdateState();

            // Gera as miniaturas em sequência, sem travar a interface.
            foreach (var item in Pages.ToList())
            {
                if (version != _loadVersion) return;
                item.Thumbnail = await PdfRenderer.RenderAsync(doc, item.SourceIndex, ThumbnailScale);
            }
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível abrir o PDF", ex.Message);
            if (Pages.Count == 0) EmptyState.Visibility = Visibility.Visible;
        }
        finally
        {
            LoadingRing.IsActive = false;
        }
    }

    // ---------- Rotação e ordem ----------

    private List<PageItem> Selected => PagesGrid.SelectedItems.Cast<PageItem>().ToList();

    private void RotateLeft_Click(object sender, RoutedEventArgs e) => Rotate(-90);
    private void RotateRight_Click(object sender, RoutedEventArgs e) => Rotate(90);

    private void Rotate(int degrees)
    {
        foreach (var item in Selected) item.Rotation = PdfOperations.Normalize(item.Rotation + degrees);
        UpdateState();
    }

    private void MoveBack_Click(object sender, RoutedEventArgs e) => MoveSelected(-1);
    private void MoveForward_Click(object sender, RoutedEventArgs e) => MoveSelected(+1);

    private void MoveSelected(int delta)
    {
        if (PagesGrid.SelectedItem is not PageItem item) return;
        int from = Pages.IndexOf(item), to = from + delta;
        if (to < 0 || to >= Pages.Count) return;
        Pages.Move(from, to);
        PagesGrid.SelectedItem = item;
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => PagesGrid.SelectAll();

    private void Reset_Click(object sender, RoutedEventArgs e)
    {
        var ordered = Pages.OrderBy(p => p.SourceIndex).ToList();
        Pages.Clear();
        foreach (var p in ordered) { p.Rotation = 0; Pages.Add(p); }
    }

    private void PagesGrid_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateState();

    private void Renumber()
    {
        for (int i = 0; i < Pages.Count; i++) Pages[i].Position = i + 1;
    }

    private bool IsModified => Pages.Select((p, i) => p.SourceIndex != i || p.Rotation != 0).Any(x => x);

    private void UpdateState()
    {
        int selected = PagesGrid.SelectedItems.Count;
        int index = PagesGrid.SelectedIndex;
        RotateLeftButton.IsEnabled = RotateRightButton.IsEnabled = selected > 0;
        MoveBackButton.IsEnabled = selected == 1 && index > 0;
        MoveForwardButton.IsEnabled = selected == 1 && index < Pages.Count - 1;
        SelectAllButton.IsEnabled = Pages.Count > 0;
        bool modified = IsModified;
        ResetButton.IsEnabled = modified;
        SaveButton.IsEnabled = modified && !SaveProgress.IsActive;
        SaveAsButton.IsEnabled = Pages.Count > 0 && !SaveProgress.IsActive;
        SummaryText.Text = _file is null
            ? "Nenhum arquivo aberto"
            : $"{Path.GetFileName(_file)} · {Pages.Count} páginas" + (modified ? " · alterações não salvas" : "");
    }

    // ---------- Salvar ----------

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_file is not null) await SaveAsync(_file);
    }

    private async void SaveAs_Click(object sender, RoutedEventArgs e)
    {
        if (_file is null) return;
        string? output = await FilePickers.SavePdfAsync(Path.GetFileNameWithoutExtension(_file) + "-organizado");
        if (output is not null) await SaveAsync(output);
    }

    private async Task SaveAsync(string output)
    {
        string input = _file!;
        var edits = Pages.Select(p => new PageEdit(p.SourceIndex, p.Rotation)).ToList();
        SaveProgress.IsActive = true;
        UpdateState();
        try
        {
            await Task.Run(() => PdfOperations.Reorganize(input, edits, output));
            await LoadAsync(output); // recarrega: o arquivo salvo vira o novo "original"
            ShowStatus(InfoBarSeverity.Success, "Páginas salvas", $"Arquivo salvo em {output}.",
                "Abrir no visualizador", () => App.MainWindow!.OpenInViewer(output));
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível salvar", ex.Message);
        }
        finally
        {
            SaveProgress.IsActive = false;
            UpdateState();
        }
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
