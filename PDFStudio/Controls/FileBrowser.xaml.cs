using System.Collections.ObjectModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Core;
using Windows.System;

namespace PDFStudio.Controls;

public enum FileBrowserMode { Open, OpenMany, Save, Folder }

/// <summary>O que a tela de arquivos deve fazer.</summary>
/// <param name="Extensions">Tipos aceitos (".pdf"); vazio = qualquer arquivo.</param>
/// <param name="TypeName">Nome do tipo na lista ("Documento PDF").</param>
/// <param name="SuggestedName">Salvar: nome sugerido (sem pasta).</param>
/// <param name="StartFolder">Pasta inicial (null = a última usada, ou Documentos).</param>
public sealed record FileBrowserOptions(FileBrowserMode Mode, string Title, IReadOnlyList<string> Extensions, string TypeName,
    string? SuggestedName = null, string? StartFolder = null);

/// <summary>Uma linha da lista de arquivos.</summary>
public sealed class FileRow
{
    public required FileEntry Entry { get; init; }
    public string Name => Entry.Name;
    public string Glyph => Entry.IsFolder ? "" : FileBrowser.GlyphFor(Entry.Name);
    public Brush IconBrush => Entry.IsFolder ? FileBrowser.FolderBrush : FileBrowser.FileBrush(Entry.Name);
    public string ModifiedText => Entry.Modified.ToString("dd/MM/yyyy HH:mm");
    public string SizeText => Entry.IsFolder ? "" : FileBrowser.SizeText(Entry.Size);
}

/// <summary>Uma linha da lateral (lugares).</summary>
public sealed class PlaceRow
{
    public required FilePlace Place { get; init; }
    public string Name => Place.Name;
    public string Glyph => Place.Kind switch
    {
        FilePlaceKind.Desktop => "",
        FilePlaceKind.Documents => "",
        FilePlaceKind.Downloads => "",
        FilePlaceKind.Pictures => "",
        _ => "",
    };
}

/// <summary>
/// Tela própria de abrir arquivos, salvar e escolher pasta (no lugar das janelas do Windows). Fica numa camada
/// sobre a janela (Services/FileBrowserHost), e não num ContentDialog, porque é chamada também de dentro de
/// outras janelas do app (só pode haver um ContentDialog aberto por vez). O resultado sai em <see cref="Result"/>.
/// </summary>
public sealed partial class FileBrowser : UserControl
{
    internal static readonly Brush FolderBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE8, 0xB3, 0x3B));
    private static readonly Brush PdfBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xE5, 0x39, 0x35));
    private static readonly Brush ImageBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x43, 0xA0, 0x47));
    private static readonly Brush WordBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x1E, 0x88, 0xE5));
    private static readonly Brush OtherBrush = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x90, 0x90, 0x90));

    private readonly FileBrowserOptions _options;
    private readonly TaskCompletionSource<IReadOnlyList<string>?> _result = new();
    private readonly FolderHistory _history = new();
    private readonly ObservableCollection<FileRow> _rows = [];
    private readonly ObservableCollection<PlaceRow> _places = [];
    private string _folder = "";
    private int _listVersion;
    private string? _pendingOverwrite;
    private bool _settingName; // o nome foi posto pela seleção, não digitado

    public FileBrowser(FileBrowserOptions options)
    {
        InitializeComponent();
        _options = options;
        TitleText.Text = options.Title;
        FilesList.ItemsSource = _rows;
        PlacesList.ItemsSource = _places;
        MessageBar.IsOpen = false;

        bool many = options.Mode == FileBrowserMode.OpenMany;
        FilesList.SelectionMode = many ? ListViewSelectionMode.Extended : ListViewSelectionMode.Single;
        string pattern = options.Extensions.Count == 0 ? "*.*" : string.Join("; ", options.Extensions.Select(e => "*" + e));
        switch (options.Mode)
        {
            case FileBrowserMode.Save:
                ConfirmButton.Content = "Salvar";
                TypeBox.Items.Add($"{options.TypeName} ({pattern})");
                TypeBox.IsEnabled = false;
                NameBox.Text = options.SuggestedName ?? "";
                break;
            case FileBrowserMode.Folder:
                ConfirmButton.Content = "Escolher esta pasta";
                NameLabel.Text = "Pasta:";
                NameBox.IsReadOnly = true;
                TypeBox.Visibility = Visibility.Collapsed;
                break;
            default:
                ConfirmButton.Content = "Abrir";
                NameLabel.Text = many ? "Arquivos:" : "Nome do arquivo:";
                TypeBox.Items.Add($"{options.TypeName} ({pattern})");
                if (options.Extensions.Count > 0) TypeBox.Items.Add("Todos os arquivos (*.*)");
                break;
        }
        if (TypeBox.Items.Count > 0) TypeBox.SelectedIndex = 0;

        foreach (var place in FileBrowsing.Places()) _places.Add(new PlaceRow { Place = place });

        string start = options.StartFolder is { } s && Directory.Exists(s) ? s
            : App.Settings.LastFolder is { } last && Directory.Exists(last) ? last
            : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        Loaded += async (_, _) =>
        {
            await NavigateAsync(start);
            if (options.Mode == FileBrowserMode.Save)
            {
                NameBox.Focus(FocusState.Programmatic);
                // Seleciona só o nome, sem a extensão, para digitar por cima.
                int dot = NameBox.Text.LastIndexOf('.');
                NameBox.Select(0, dot > 0 ? dot : NameBox.Text.Length);
            }
            else FilesList.Focus(FocusState.Programmatic);
        };
    }

    /// <summary>Os caminhos escolhidos, ou null se cancelou.</summary>
    public Task<IReadOnlyList<string>?> Result => _result.Task;

    // ---------------- Aparência dos itens ----------------

    internal static string GlyphFor(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => "",
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => "",
        ".pfx" or ".p12" => "",
        ".ttf" or ".otf" => "",
        _ => "",
    };

    internal static Brush FileBrush(string name) => Path.GetExtension(name).ToLowerInvariant() switch
    {
        ".pdf" => PdfBrush,
        ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".tif" or ".tiff" or ".webp" => ImageBrush,
        ".docx" or ".doc" or ".rtf" => WordBrush,
        _ => OtherBrush,
    };

    internal static string SizeText(long bytes) => FileBrowsing.FormatSize(bytes);

    // ---------------- Navegação ----------------

    private IReadOnlyList<string> CurrentExtensions =>
        _options.Mode == FileBrowserMode.Folder ? []
        : TypeBox.SelectedIndex == 1 ? [] : _options.Extensions;

    /// <summary>Mostra a pasta. <paramref name="remember"/> false = veio do voltar/avançar (não mexe no histórico).</summary>
    private async Task NavigateAsync(string folder, bool remember = true)
    {
        int version = ++_listVersion;
        string search = SearchBox.Text;
        var extensions = CurrentExtensions;
        bool files = _options.Mode != FileBrowserMode.Folder;
        LoadingRing.IsActive = true;
        EmptyText.Visibility = Visibility.Collapsed;
        List<FileEntry>? entries = null;
        string? problem = null;
        try
        {
            folder = Path.GetFullPath(folder);
            entries = await Task.Run(() => FileBrowsing.List(folder, extensions, files, search));
        }
        catch (UnauthorizedAccessException)
        {
            problem = "Sem permissão para abrir esta pasta.";
        }
        catch (DirectoryNotFoundException)
        {
            problem = "Esta pasta não existe (mais).";
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or NotSupportedException)
        {
            problem = "Não foi possível abrir esta pasta: " + ex.Message;
        }
        if (version != _listVersion) return;
        LoadingRing.IsActive = false;

        if (problem is not null && _folder.Length > 0 && !string.Equals(folder, _folder, StringComparison.OrdinalIgnoreCase))
        {
            ShowMessage(InfoBarSeverity.Warning, problem); // fica na pasta em que estava
            PathBox.Text = _folder;
            return;
        }

        _folder = folder;
        if (remember) _history.Visit(folder);
        PathBox.Text = folder;
        _rows.Clear();
        foreach (var e in entries ?? []) _rows.Add(new FileRow { Entry = e });
        EmptyText.Text = problem ?? (search.Trim().Length > 0 ? "Nada com esse nome nesta pasta." : "Esta pasta está vazia.");
        EmptyText.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.IsEnabled = _history.CanGoBack;
        ForwardButton.IsEnabled = _history.CanGoForward;
        UpButton.IsEnabled = FileBrowsing.Parent(folder) is not null;
        NewFolderButton.IsEnabled = problem is null;
        PlacesList.SelectedItem = _places.FirstOrDefault(p => string.Equals(Path.TrimEndingDirectorySeparator(p.Place.Path),
            Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase));
        if (_options.Mode == FileBrowserMode.Folder) SetName(Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) is { Length: > 0 } n ? n : folder);
        UpdateConfirmText();
    }

    private async void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Back() is { } f) await NavigateAsync(f, remember: false);
    }

    private async void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Forward() is { } f) await NavigateAsync(f, remember: false);
    }

    private async void Up_Click(object sender, RoutedEventArgs e)
    {
        if (FileBrowsing.Parent(_folder) is { } parent) await NavigateAsync(parent);
    }

    private async void PlacesList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is PlaceRow row)
        {
            SearchBox.Text = "";
            await NavigateAsync(row.Place.Path);
        }
    }

    private async void PathBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        string typed = Environment.ExpandEnvironmentVariables(PathBox.Text.Trim().Trim('"'));
        if (Directory.Exists(typed)) await NavigateAsync(typed);
        else if (File.Exists(typed) && _options.Mode is FileBrowserMode.Open or FileBrowserMode.OpenMany && Accepts(typed))
            Finish([Path.GetFullPath(typed)]);
        else
        {
            ShowMessage(InfoBarSeverity.Warning, "Essa pasta não existe.");
            PathBox.Text = _folder;
        }
    }

    private async void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_folder.Length > 0) await NavigateAsync(_folder, remember: false);
    }

    private async void TypeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_folder.Length > 0) await NavigateAsync(_folder, remember: false);
    }

    // ---------------- Seleção ----------------

    private List<FileRow> SelectedRows => FilesList.SelectedItems.OfType<FileRow>().ToList();

    private void FilesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _pendingOverwrite = null;
        var files = SelectedRows.Where(r => !r.Entry.IsFolder).ToList();
        switch (_options.Mode)
        {
            case FileBrowserMode.Open:
            case FileBrowserMode.Save:
                if (files.Count == 1) SetName(files[0].Name);
                break;
            case FileBrowserMode.OpenMany:
                if (files.Count == 1) SetName(files[0].Name);
                else if (files.Count > 1) SetName(string.Join(" ", files.Select(f => $"\"{f.Name}\"")));
                break;
        }
        UpdateConfirmText();
    }

    private void SetName(string text)
    {
        _settingName = true;
        NameBox.Text = text;
        _settingName = false;
    }

    private void NameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _pendingOverwrite = null;
        if (!_settingName && MessageBar.IsOpen) MessageBar.IsOpen = false;
    }

    private void UpdateConfirmText()
    {
        if (_options.Mode != FileBrowserMode.Folder) return;
        var folders = SelectedRows.Where(r => r.Entry.IsFolder).ToList();
        ConfirmButton.Content = folders.Count == 1 ? $"Escolher “{Shorten(folders[0].Name)}”" : "Escolher esta pasta";
        SetName(folders.Count == 1 ? folders[0].Name : Path.GetFileName(Path.TrimEndingDirectorySeparator(_folder)) is { Length: > 0 } n ? n : _folder);
    }

    private static string Shorten(string s) => s.Length > 24 ? s[..22] + "…" : s;

    private async void FilesList_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if ((e.OriginalSource as FrameworkElement)?.DataContext is FileRow row) await ActivateAsync(row);
    }

    private async void FilesList_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter && FilesList.SelectedItem is FileRow row)
        {
            e.Handled = true;
            if (row.Entry.IsFolder && SelectedRows.Count == 1) await ActivateAsync(row);
            else await ConfirmAsync();
        }
        else if (e.Key == VirtualKey.Back)
        {
            e.Handled = true;
            Up_Click(sender, e);
        }
    }

    /// <summary>Duplo clique: pasta = entrar; arquivo = escolher.</summary>
    private async Task ActivateAsync(FileRow row)
    {
        if (row.Entry.IsFolder)
        {
            SearchBox.Text = "";
            await NavigateAsync(row.Entry.Path);
            return;
        }
        if (_options.Mode == FileBrowserMode.Save) SetName(row.Name);
        await ConfirmAsync();
    }

    // ---------------- Confirmar ----------------

    private async void Primary_Click(object sender, RoutedEventArgs e) => await ConfirmAsync();

    private async void NameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await ConfirmAsync();
    }

    private bool Accepts(string path) => FileBrowsing.HasExtension(path, CurrentExtensions);

    private async Task ConfirmAsync()
    {
        switch (_options.Mode)
        {
            case FileBrowserMode.Folder:
                var folder = SelectedRows.FirstOrDefault(r => r.Entry.IsFolder);
                Finish([folder?.Entry.Path ?? _folder]);
                return;

            case FileBrowserMode.Save:
                await ConfirmSaveAsync();
                return;

            default:
                await ConfirmOpenAsync();
                return;
        }
    }

    private async Task ConfirmOpenAsync()
    {
        var selected = SelectedRows;
        var files = selected.Where(r => !r.Entry.IsFolder).Select(r => r.Entry.Path).ToList();
        string typed = NameBox.Text.Trim();
        // O nome na caixa é o da seleção: usa a seleção.
        if (files.Count > 0 && (typed.Length == 0 || (files.Count == 1 ? typed == Path.GetFileName(files[0]) : typed.StartsWith('"'))))
        {
            Finish(files);
            return;
        }
        if (typed.Length == 0)
        {
            if (selected.Count == 1 && selected[0].Entry.IsFolder) await NavigateAsync(selected[0].Entry.Path);
            else ShowMessage(InfoBarSeverity.Informational, "Escolha um arquivo na lista.");
            return;
        }
        // Digitado: um ou mais nomes entre aspas, um nome solto ou um caminho.
        var names = typed.Contains('"')
            ? typed.Split('"', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
            : [typed];
        var paths = new List<string>();
        foreach (var name in names)
        {
            string full;
            try { full = Path.GetFullPath(Path.IsPathRooted(name) ? name : Path.Combine(_folder, name)); }
            catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
            {
                ShowMessage(InfoBarSeverity.Warning, $"“{name}” não é um nome válido.");
                return;
            }
            if (Directory.Exists(full) && names.Count == 1)
            {
                SetName("");
                await NavigateAsync(full);
                return;
            }
            if (!File.Exists(full))
            {
                ShowMessage(InfoBarSeverity.Warning, $"“{name}” não foi encontrado nesta pasta.");
                return;
            }
            paths.Add(full);
        }
        if (_options.Mode == FileBrowserMode.Open && paths.Count > 1)
        {
            ShowMessage(InfoBarSeverity.Warning, "Escolha um arquivo só.");
            return;
        }
        Finish(paths);
    }

    /// <param name="overwrite">A pessoa apertou "Substituir" no aviso (só então um arquivo existente é trocado).</param>
    private async Task ConfirmSaveAsync(bool overwrite = false)
    {
        string typed = NameBox.Text.Trim();
        // Uma pasta digitada (ou só a pasta selecionada sem nome) = entrar nela.
        if (typed.Length > 0)
        {
            string candidate = Path.IsPathRooted(typed) ? typed : Path.Combine(_folder, typed);
            if (Directory.Exists(candidate))
            {
                SetName(_options.SuggestedName ?? "");
                await NavigateAsync(candidate);
                return;
            }
        }
        string path;
        try
        {
            path = FileBrowsing.SavePath(_folder, typed, _options.Extensions.FirstOrDefault() ?? "");
        }
        catch (ArgumentException ex)
        {
            ShowMessage(InfoBarSeverity.Warning, ex.Message);
            return;
        }
        if (File.Exists(path) && !(overwrite && string.Equals(_pendingOverwrite, path, StringComparison.OrdinalIgnoreCase)))
        {
            _pendingOverwrite = path;
            ShowMessage(InfoBarSeverity.Warning, $"“{Path.GetFileName(path)}” já existe nesta pasta. Quer substituir?", overwrite: true);
            return;
        }
        Finish([path]);
    }

    private async void Overwrite_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingOverwrite is null) return;
        await ConfirmSaveAsync(overwrite: true);
    }

    // ---------------- Nova pasta ----------------

    private void NewFolderFlyout_Opened(object sender, object e)
    {
        NewFolderBox.Text = "Nova pasta";
        NewFolderError.Visibility = Visibility.Collapsed;
        NewFolderBox.Focus(FocusState.Programmatic);
        NewFolderBox.SelectAll();
    }

    private async void NewFolderBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await CreateFolderAsync();
    }

    private async void CreateFolder_Click(object sender, RoutedEventArgs e) => await CreateFolderAsync();

    private async Task CreateFolderAsync()
    {
        string created;
        try
        {
            created = FileBrowsing.CreateFolder(_folder, NewFolderBox.Text);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            NewFolderError.Text = ex is UnauthorizedAccessException ? "Sem permissão para criar pastas aqui." : ex.Message;
            NewFolderError.Visibility = Visibility.Visible;
            return;
        }
        NewFolderFlyout.Hide();
        SearchBox.Text = "";
        await NavigateAsync(created); // entra na pasta nova
    }

    // ---------------- Teclado, mensagens e fim ----------------

    private async void Card_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool alt = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Menu)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        if (e.Key == VirtualKey.Escape)
        {
            if (NewFolderFlyout.IsOpen) return;
            e.Handled = true;
            Finish(null);
        }
        else if (alt && e.Key == VirtualKey.Left) { e.Handled = true; if (_history.Back() is { } f) await NavigateAsync(f, false); }
        else if (alt && e.Key == VirtualKey.Right) { e.Handled = true; if (_history.Forward() is { } f) await NavigateAsync(f, false); }
        else if (alt && e.Key == VirtualKey.Up) { e.Handled = true; if (FileBrowsing.Parent(_folder) is { } p) await NavigateAsync(p); }
    }

    private void ShowMessage(InfoBarSeverity severity, string message, bool overwrite = false)
    {
        MessageBar.Severity = severity;
        MessageBar.Message = message;
        OverwriteButton.Visibility = overwrite ? Visibility.Visible : Visibility.Collapsed;
        MessageBar.IsOpen = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Finish(null);

    private void Finish(IReadOnlyList<string>? paths)
    {
        if (paths is { Count: > 0 })
        {
            string? folder = _options.Mode == FileBrowserMode.Folder ? paths[0] : Path.GetDirectoryName(paths[0]);
            if (folder is not null)
            {
                App.Settings.LastFolder = folder;
                App.Settings.Save();
            }
        }
        _result.TrySetResult(paths is { Count: > 0 } ? paths : null);
    }
}
