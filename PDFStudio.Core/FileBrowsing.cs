namespace PDFStudio.Core;

/// <summary>Um item da lista da tela de abrir/salvar: pasta ou arquivo.</summary>
public sealed record FileEntry(string Path, string Name, bool IsFolder, DateTime Modified, long Size);

/// <summary>Um lugar fixo da lateral (Área de Trabalho, Documentos, um disco…).</summary>
public sealed record FilePlace(string Name, string Path, FilePlaceKind Kind);

public enum FilePlaceKind { Desktop, Documents, Downloads, Pictures, Drive }

/// <summary>
/// Contas da tela própria de abrir/salvar arquivos e escolher pasta (no lugar das janelas do Windows):
/// listar uma pasta, filtrar, conferir o nome digitado e montar o caminho final.
/// </summary>
public static class FileBrowsing
{
    private static readonly string[] ReservedNames =
        ["CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
         "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"];

    /// <summary>
    /// Pastas e arquivos de <paramref name="folder"/>: pastas primeiro, depois arquivos, cada grupo em ordem de nome
    /// (números em ordem natural: "2" antes de "10"). Itens ocultos e de sistema ficam de fora.
    /// <paramref name="extensions"/> vazio = todos os arquivos; <paramref name="includeFiles"/> false = só pastas.
    /// <paramref name="search"/>: só os nomes que contêm o texto (sem diferenciar maiúsculas e acentos).
    /// </summary>
    public static List<FileEntry> List(string folder, IReadOnlyCollection<string> extensions, bool includeFiles = true, string? search = null)
    {
        var dir = new DirectoryInfo(folder);
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            RecurseSubdirectories = false,
        };
        var folders = new List<FileEntry>();
        var files = new List<FileEntry>();
        foreach (var info in dir.EnumerateFileSystemInfos("*", options))
        {
            if (!NameMatches(info.Name, search)) continue;
            if (info is DirectoryInfo d)
                folders.Add(new FileEntry(d.FullName, d.Name, true, d.LastWriteTime, 0));
            else if (includeFiles && info is FileInfo f && HasExtension(f.Name, extensions))
                files.Add(new FileEntry(f.FullName, f.Name, false, f.LastWriteTime, f.Length));
        }
        var order = Comparer<FileEntry>.Create((a, b) => NaturalCompare(a.Name, b.Name));
        folders.Sort(order);
        files.Sort(order);
        folders.AddRange(files);
        return folders;
    }

    /// <summary>O arquivo tem uma das extensões (vazio = qualquer uma). Aceita ".pdf" ou "pdf".</summary>
    public static bool HasExtension(string name, IReadOnlyCollection<string> extensions)
    {
        if (extensions.Count == 0) return true;
        string ext = System.IO.Path.GetExtension(name);
        return extensions.Any(e => string.Equals(ext, e.StartsWith('.') ? e : "." + e, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>O nome contém o texto procurado (sem diferenciar maiúsculas e acentos). Texto vazio = sim.</summary>
    public static bool NameMatches(string name, string? search)
    {
        if (string.IsNullOrWhiteSpace(search)) return true;
        return System.Globalization.CultureInfo.InvariantCulture.CompareInfo.IndexOf(name, search.Trim(),
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace) >= 0;
    }

    /// <summary>Compara nomes com os números pelo valor ("pagina2" antes de "pagina10").</summary>
    public static int NaturalCompare(string a, string b)
    {
        int i = 0, j = 0;
        while (i < a.Length && j < b.Length)
        {
            if (char.IsDigit(a[i]) && char.IsDigit(b[j]))
            {
                int si = i, sj = j;
                while (i < a.Length && char.IsDigit(a[i])) i++;
                while (j < b.Length && char.IsDigit(b[j])) j++;
                string na = a[si..i].TrimStart('0'), nb = b[sj..j].TrimStart('0');
                if (na.Length != nb.Length) return na.Length.CompareTo(nb.Length);
                int c = string.CompareOrdinal(na, nb);
                if (c != 0) return c;
            }
            else
            {
                int c = string.Compare(a[i].ToString(), b[j].ToString(), System.Globalization.CultureInfo.CurrentCulture,
                    System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace);
                if (c != 0) return c;
                i++;
                j++;
            }
        }
        int rest = (a.Length - i).CompareTo(b.Length - j);
        return rest != 0 ? rest : string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Confere um nome de arquivo (sem pasta). Devolve a mensagem do problema, ou null se estiver bom.
    /// </summary>
    public static string? NameProblem(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "Escreva um nome.";
        if (name.IndexOfAny(System.IO.Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0)
            return "O nome não pode ter estes sinais: \\ / : * ? \" < > |";
        if (name.EndsWith('.') || name.EndsWith(' ')) return "O nome não pode terminar com ponto ou espaço.";
        string stem = name.Split('.')[0].Trim();
        if (ReservedNames.Contains(stem, StringComparer.OrdinalIgnoreCase)) return $"“{stem}” é um nome reservado do Windows.";
        if (name.Length > 200) return "O nome é longo demais.";
        return null;
    }

    /// <summary>
    /// Caminho final para salvar: <paramref name="typed"/> pode ser só o nome (fica em <paramref name="folder"/>),
    /// um caminho relativo ou completo. Sem a extensão, ela é acrescentada. Erro (nome inválido, pasta que não
    /// existe) = <see cref="ArgumentException"/> com a mensagem para mostrar.
    /// </summary>
    public static string SavePath(string folder, string typed, string extension)
    {
        typed = typed.Trim().Trim('"');
        if (typed.Length == 0) throw new ArgumentException("Escreva um nome para o arquivo.");
        string full;
        try
        {
            full = System.IO.Path.GetFullPath(System.IO.Path.IsPathRooted(typed) ? typed : System.IO.Path.Combine(folder, typed));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Esse nome de arquivo não é válido.");
        }
        string name = System.IO.Path.GetFileName(full);
        if (NameProblem(name) is { } problem) throw new ArgumentException(problem);
        if (extension.Length > 0)
        {
            string ext = extension.StartsWith('.') ? extension : "." + extension;
            if (!name.EndsWith(ext, StringComparison.OrdinalIgnoreCase)) full += ext;
        }
        string? dir = System.IO.Path.GetDirectoryName(full);
        if (dir is null || !Directory.Exists(dir)) throw new ArgumentException("A pasta escrita não existe.");
        if (Directory.Exists(full)) throw new ArgumentException("Já existe uma pasta com esse nome.");
        return full;
    }

    /// <summary>
    /// Cria uma pasta nova dentro de <paramref name="parent"/>. Se o nome já existir, acrescenta " (2)", " (3)"…
    /// Devolve o caminho criado. Nome inválido = <see cref="ArgumentException"/>.
    /// </summary>
    public static string CreateFolder(string parent, string name)
    {
        name = name.Trim();
        if (NameProblem(name) is { } problem) throw new ArgumentException(problem);
        string path = System.IO.Path.Combine(parent, name);
        for (int n = 2; Directory.Exists(path) || File.Exists(path); n++)
            path = System.IO.Path.Combine(parent, $"{name} ({n})");
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Pasta de cima (null no disco, ex.: "C:\").</summary>
    public static string? Parent(string folder) => Directory.GetParent(System.IO.Path.TrimEndingDirectorySeparator(folder))?.FullName;

    /// <summary>Tamanho para mostrar: "830 bytes", "12 KB", "1,4 MB".</summary>
    public static string FormatSize(long bytes)
    {
        var culture = System.Globalization.CultureInfo.GetCultureInfo("pt-BR");
        if (bytes < 1024) return bytes == 1 ? "1 byte" : $"{bytes} bytes";
        double kb = bytes / 1024.0;
        if (kb < 1024) return $"{Math.Ceiling(kb).ToString("0", culture)} KB";
        double mb = kb / 1024;
        if (mb < 1024) return $"{mb.ToString(mb < 10 ? "0.0" : "0", culture)} MB";
        double gb = mb / 1024;
        return $"{gb.ToString(gb < 10 ? "0.0" : "0", culture)} GB";
    }

    /// <summary>Lugares da lateral: Área de Trabalho, Documentos, Downloads, Imagens e os discos prontos.</summary>
    public static List<FilePlace> Places()
    {
        var list = new List<FilePlace>();
        void Add(string name, string? path, FilePlaceKind kind)
        {
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path)) list.Add(new FilePlace(name, path, kind));
        }
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        Add("Área de Trabalho", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), FilePlaceKind.Desktop);
        Add("Documentos", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), FilePlaceKind.Documents);
        Add("Downloads", System.IO.Path.Combine(profile, "Downloads"), FilePlaceKind.Downloads);
        Add("Imagens", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), FilePlaceKind.Pictures);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                string letter = drive.Name.TrimEnd('\\');
                string label = string.IsNullOrWhiteSpace(drive.VolumeLabel)
                    ? drive.DriveType == DriveType.Removable ? "Pen drive" : drive.DriveType == DriveType.Network ? "Rede" : "Disco local"
                    : drive.VolumeLabel;
                list.Add(new FilePlace($"{label} ({letter})", drive.RootDirectory.FullName, FilePlaceKind.Drive));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return list;
    }
}

/// <summary>Voltar e avançar entre pastas, como num navegador.</summary>
public sealed class FolderHistory
{
    private readonly List<string> _items = [];
    private int _position = -1;

    public string? Current => _position >= 0 ? _items[_position] : null;
    public bool CanGoBack => _position > 0;
    public bool CanGoForward => _position < _items.Count - 1;

    /// <summary>Entrou numa pasta: o "avançar" que havia se perde. A mesma pasta de novo não repete.</summary>
    public void Visit(string folder)
    {
        if (Current is { } current && string.Equals(current, folder, StringComparison.OrdinalIgnoreCase)) return;
        if (_position < _items.Count - 1) _items.RemoveRange(_position + 1, _items.Count - _position - 1);
        _items.Add(folder);
        _position = _items.Count - 1;
    }

    public string? Back() => CanGoBack ? _items[--_position] : null;
    public string? Forward() => CanGoForward ? _items[++_position] : null;
}
