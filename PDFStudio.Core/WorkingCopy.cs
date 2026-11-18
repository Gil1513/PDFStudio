namespace PDFStudio.Core;

/// <summary>
/// Cópia de trabalho de um PDF em edição. Cada alteração gera uma nova versão numa pasta temporária;
/// o arquivo original só muda ao salvar. Guardar as versões dá o desfazer/refazer.
/// </summary>
public sealed class WorkingCopy : IDisposable
{
    /// <summary>Quantas versões guardar (as mais antigas deixam de poder ser desfeitas).</summary>
    public const int MaxVersions = 100;

    private static readonly string DefaultRoot = Path.Combine(Path.GetTempPath(), "PDFStudio", "work");

    private readonly string _folder;
    private readonly List<string> _versions = [];
    private int _current;   // versão mostrada
    private int _saved;     // versão igual ao arquivo em disco (-1 = nenhuma)
    private int _counter;

    public WorkingCopy(string originalPath, string? tempRoot = null)
    {
        OriginalPath = Path.GetFullPath(originalPath);
        _folder = Path.Combine(tempRoot ?? DefaultRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        string first = NextFile();
        File.Copy(OriginalPath, first);
        _versions.Add(first);
    }

    /// <summary>Arquivo que o usuário abriu (ou o último "Salvar como").</summary>
    public string OriginalPath { get; private set; }

    /// <summary>Versão atual, que deve ser mostrada na tela.</summary>
    public string CurrentPath => _versions[_current];

    public bool IsDirty => _current != _saved;
    public bool CanUndo => _current > 0;
    public bool CanRedo => _current < _versions.Count - 1;

    /// <summary>
    /// Aplica uma alteração: <paramref name="operation"/> recebe (versão atual, arquivo novo) e deve gravar o novo.
    /// As versões "refazer" pendentes são descartadas. Se a operação falhar, nada muda.
    /// </summary>
    public void Apply(Action<string, string> operation)
    {
        string next = NextFile();
        try
        {
            operation(CurrentPath, next);
            if (!File.Exists(next)) throw new InvalidOperationException("A alteração não gerou um arquivo.");
        }
        catch
        {
            TryDelete(next);
            throw;
        }

        // Descarta o que poderia ser refeito.
        for (int i = _versions.Count - 1; i > _current; i--)
        {
            TryDelete(_versions[i]);
            _versions.RemoveAt(i);
            if (_saved == i) _saved = -1; // a versão salva não existe mais no histórico
        }
        _versions.Add(next);
        _current = _versions.Count - 1;

        if (_versions.Count > MaxVersions)
        {
            TryDelete(_versions[0]);
            _versions.RemoveAt(0);
            _current--;
            _saved = _saved > 0 ? _saved - 1 : -1;
        }
    }

    public void Undo() { if (CanUndo) _current--; }
    public void Redo() { if (CanRedo) _current++; }

    /// <summary>Grava a versão atual por cima do arquivo original.</summary>
    public void Save() => SaveAs(OriginalPath);

    /// <summary>Grava a versão atual em outro arquivo, que passa a ser o "original".</summary>
    public void SaveAs(string path)
    {
        string full = Path.GetFullPath(path);
        string temp = full + ".tmp";
        File.Copy(CurrentPath, temp, overwrite: true);
        File.Move(temp, full, overwrite: true);
        OriginalPath = full;
        _saved = _current;
    }

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Apaga pastas de trabalho esquecidas (ex.: o app foi fechado à força) com mais de um dia.</summary>
    public static void CleanupStale(string? tempRoot = null)
    {
        string root = tempRoot ?? DefaultRoot;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                if (Directory.GetLastWriteTimeUtc(dir) < DateTime.UtcNow.AddDays(-1)) Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private string NextFile() => Path.Combine(_folder, $"v{_counter++}.pdf");

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
