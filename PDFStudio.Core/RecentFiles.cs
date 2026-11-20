using System.Text.Json;

namespace PDFStudio.Core;

/// <summary>Um arquivo aberto recentemente.</summary>
public sealed class RecentFileEntry
{
    public required string Path { get; set; }
    public DateTime LastOpened { get; set; }
    public bool Starred { get; set; }
    /// <summary>Última página vista (começa em 1; 0 = nunca guardada). O arquivo volta a abrir nela.</summary>
    public int LastPage { get; set; }
}

/// <summary>Uma pasta de onde arquivos foram abertos recentemente.</summary>
public sealed record RecentFolder(string Path, DateTime LastOpened, int FileCount);

/// <summary>
/// Lista de arquivos recentes e com estrela, gravada em JSON. Tolerante a arquivo
/// corrompido (começa vazia) e a arquivos que foram apagados (continuam na lista
/// até o usuário remover).
/// </summary>
public sealed class RecentFilesStore
{
    /// <summary>Máximo de arquivos SEM estrela mantidos; os com estrela nunca saem sozinhos.</summary>
    public const int MaxUnstarred = 100;

    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly string _storePath;
    private readonly List<RecentFileEntry> _entries;
    /// <summary>Protege a lista: "Remover ausentes" roda em segundo plano enquanto a tela lê a lista.</summary>
    private readonly object _lock = new();

    public event EventHandler? Changed;

    public RecentFilesStore(string storePath)
    {
        _storePath = storePath;
        _entries = Load(storePath);
    }

    /// <summary>Caminho padrão: %LOCALAPPDATA%\PDFStudio\recent.json.</summary>
    public static string DefaultPath =>
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDFStudio", "recent.json");

    /// <summary>Arquivos, do aberto mais recentemente para o mais antigo.</summary>
    public IReadOnlyList<RecentFileEntry> Entries
    {
        get { lock (_lock) return _entries.OrderByDescending(e => e.LastOpened).ToList(); }
    }

    public IReadOnlyList<RecentFileEntry> Starred => Entries.Where(e => e.Starred).ToList();

    /// <summary>Pastas dos arquivos recentes, da mais recente para a mais antiga.</summary>
    public IReadOnlyList<RecentFolder> Folders
    {
        get
        {
            lock (_lock)
                return _entries
                    .GroupBy(e => System.IO.Path.GetDirectoryName(e.Path) ?? "", PathComparer)
                    .Where(g => g.Key.Length > 0)
                    .Select(g => new RecentFolder(g.Key, g.Max(e => e.LastOpened), g.Count()))
                    .OrderByDescending(f => f.LastOpened)
                    .ToList();
        }
    }

    public RecentFileEntry? Find(string path)
    {
        lock (_lock) return _entries.FirstOrDefault(e => PathComparer.Equals(e.Path, path));
    }

    /// <summary>Registra que o arquivo foi aberto agora (mantém a estrela se já existia).</summary>
    public void Add(string path, DateTime? when = null)
    {
        path = System.IO.Path.GetFullPath(path);
        lock (_lock)
        {
            var entry = Find(path);
            if (entry is null)
            {
                entry = new RecentFileEntry { Path = path };
                _entries.Add(entry);
            }
            entry.LastOpened = when ?? DateTime.Now;
            Trim();
        }
        Save();
    }

    /// <summary>
    /// Guarda a página em que o arquivo está (para abrir nela da próxima vez). Grava só se mudou e não
    /// avisa "Changed": a lista de recentes na tela não muda por causa disso.
    /// </summary>
    public void SetLastPage(string path, int page)
    {
        lock (_lock)
        {
            var entry = Find(System.IO.Path.GetFullPath(path));
            if (entry is null || page < 1 || entry.LastPage == page) return;
            entry.LastPage = page;
        }
        Save(notify: false);
    }

    /// <summary>A página onde o arquivo estava da última vez (1 se não houver).</summary>
    public int LastPageOf(string path) => Math.Max(1, Find(System.IO.Path.GetFullPath(path))?.LastPage ?? 1);

    public void SetStarred(string path, bool starred)
    {
        lock (_lock)
        {
            var entry = Find(path);
            if (entry is null || entry.Starred == starred) return;
            entry.Starred = starred;
        }
        Save();
    }

    public void Remove(string path)
    {
        int removed;
        lock (_lock) removed = _entries.RemoveAll(e => PathComparer.Equals(e.Path, path));
        if (removed > 0) Save();
    }

    /// <summary>Remove os arquivos que não existem mais no disco. Retorna quantos saíram.</summary>
    public int RemoveMissing()
    {
        // Confere o disco fora da trava (pode ser lento em pastas de rede) e só depois tira da lista.
        string[] paths;
        lock (_lock) paths = _entries.Select(e => e.Path).ToArray();
        var missing = new HashSet<string>(paths.Where(p => !File.Exists(p)), PathComparer);
        int removed;
        lock (_lock) removed = _entries.RemoveAll(e => missing.Contains(e.Path));
        if (removed > 0) Save();
        return removed;
    }

    /// <summary>Limpa a lista. Com <paramref name="keepStarred"/>, os arquivos com estrela ficam.</summary>
    public void Clear(bool keepStarred = true)
    {
        int removed;
        lock (_lock) removed = _entries.RemoveAll(e => !keepStarred || !e.Starred);
        if (removed > 0) Save();
    }

    private void Trim()
    {
        var excess = _entries.Where(e => !e.Starred).OrderByDescending(e => e.LastOpened).Skip(MaxUnstarred).ToList();
        foreach (var e in excess) _entries.Remove(e);
    }

    private static List<RecentFileEntry> Load(string path)
    {
        try
        {
            if (!File.Exists(path)) return [];
            var list = JsonSerializer.Deserialize<List<RecentFileEntry>>(File.ReadAllText(path)) ?? [];
            // Remove itens inválidos e duplicados (fica o mais recente).
            return list.Where(e => !string.IsNullOrWhiteSpace(e.Path))
                .GroupBy(e => e.Path, PathComparer)
                .Select(g => g.OrderByDescending(e => e.LastOpened).First())
                .ToList();
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return []; // arquivo corrompido ou inacessível: começa do zero
        }
    }

    private void Save(bool notify = true)
    {
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_storePath)!);
                string temp = _storePath + ".tmp";
                File.WriteAllText(temp, JsonSerializer.Serialize(_entries, JsonOptions));
                File.Move(temp, _storePath, overwrite: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Não conseguir gravar a lista de recentes não deve atrapalhar o uso do app.
        }
        if (notify) Changed?.Invoke(this, EventArgs.Empty);
    }
}
