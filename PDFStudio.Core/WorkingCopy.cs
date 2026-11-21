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
    private readonly List<string?> _labels = []; // nome da alteração que criou cada versão (ex.: "Excluir páginas")
    private int _current;   // versão mostrada
    private int _saved;     // versão igual ao arquivo em disco (-1 = nenhuma)
    private int _counter;
    /// <summary>Arquivo aberto enquanto a cópia existe: mostra a outras instâncias que a pasta está em uso
    /// (o Windows o apaga sozinho quando o app fecha, mesmo se ele travar).</summary>
    private readonly FileStream _inUse;
    private const string InUseFile = ".em-uso";

    public WorkingCopy(string originalPath, string? tempRoot = null)
    {
        OriginalPath = Path.GetFullPath(originalPath);
        _folder = Path.Combine(tempRoot ?? DefaultRoot, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_folder);
        _inUse = new FileStream(Path.Combine(_folder, InUseFile), FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        string first = NextFile();
        File.Copy(OriginalPath, first);
        _versions.Add(first);
        _labels.Add(null);
    }

    /// <summary>Arquivo que o usuário abriu (ou o último "Salvar como").</summary>
    public string OriginalPath { get; private set; }

    /// <summary>Versão atual, que deve ser mostrada na tela.</summary>
    public string CurrentPath => _versions[_current];

    public bool IsDirty => _current != _saved;
    public bool CanUndo => _current > 0;
    public bool CanRedo => _current < _versions.Count - 1;

    /// <summary>Nome da alteração que Desfazer vai desfazer (null se não houver ou não tiver nome).</summary>
    public string? UndoLabel => CanUndo ? _labels[_current] : null;

    /// <summary>Nome da alteração que Refazer vai refazer.</summary>
    public string? RedoLabel => CanRedo ? _labels[_current + 1] : null;

    /// <summary>
    /// Aplica uma alteração: <paramref name="operation"/> recebe (versão atual, arquivo novo) e deve gravar o novo.
    /// As versões "refazer" pendentes são descartadas. Se a operação falhar, nada muda.
    /// </summary>
    public void Apply(Action<string, string> operation, string? label = null)
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
            _labels.RemoveAt(i);
            if (_saved == i) _saved = -1; // a versão salva não existe mais no histórico
        }
        _versions.Add(next);
        _labels.Add(label);
        _current = _versions.Count - 1;

        if (_versions.Count > MaxVersions)
        {
            TryDelete(_versions[0]);
            _versions.RemoveAt(0);
            _labels.RemoveAt(0);
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
        _inUse.Dispose();
        try { Directory.Delete(_folder, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Apaga pastas de trabalho esquecidas (ex.: o app foi fechado à força). Elas podem ter versões SEM a senha do
    /// PDF (depois de "Remover senha"), então saem assim que nenhum PDF Studio aberto as usa — não espera um dia.
    /// </summary>
    public static void CleanupStale(string? tempRoot = null)
    {
        string root = tempRoot ?? DefaultRoot;
        if (!Directory.Exists(root)) return;
        foreach (var dir in Directory.GetDirectories(root))
        {
            try
            {
                if (Directory.GetCreationTimeUtc(dir) > DateTime.UtcNow.AddMinutes(-1)) continue; // acabou de ser criada
                if (InUse(dir)) continue;
                Directory.Delete(dir, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// Apaga cópias temporárias soltas que ficaram para trás (app fechado à força): testes de senha, cópias sem
    /// comentários e arquivos de impressão em %TEMP%\PDFStudio. Podem estar sem a senha do PDF original.
    /// Só as com mais de <paramref name="olderThan"/> (padrão: 10 minutos); arquivos ainda abertos não saem.
    /// </summary>
    public static int CleanupLooseCopies(string? tempRoot = null, TimeSpan? olderThan = null)
    {
        string root = tempRoot ?? Path.Combine(Path.GetTempPath(), "PDFStudio");
        if (!Directory.Exists(root)) return 0;
        var limit = DateTime.UtcNow - (olderThan ?? TimeSpan.FromMinutes(10));
        var files = Directory.GetFiles(root, "senha-*.pdf").Concat(Directory.GetFiles(root, "teste-senha-*.pdf"));
        foreach (var sub in new[] { "sem-comentarios", "impressao" })
            if (Directory.Exists(Path.Combine(root, sub))) files = files.Concat(Directory.GetFiles(Path.Combine(root, sub)));
        int removed = 0;
        foreach (var file in files.ToList())
        {
            try
            {
                if (File.GetLastWriteTimeUtc(file) > limit) continue;
                File.Delete(file);
                removed++;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return removed;
    }

    /// <summary>Outra instância do app está usando a pasta (o arquivo de "em uso" existe e está travado)?</summary>
    private static bool InUse(string dir)
    {
        string marker = Path.Combine(dir, InUseFile);
        if (!File.Exists(marker)) return false;
        try
        {
            using var probe = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
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
