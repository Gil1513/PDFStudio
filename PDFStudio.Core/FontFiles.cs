using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace PDFStudio.Core;

/// <summary>Tipo do arquivo de fonte, pelo começo dele.</summary>
public enum FontFileKind
{
    /// <summary>TrueType (.ttf, ou .otf com contornos TrueType): o PDF Studio consegue gravar no PDF.</summary>
    TrueType,
    /// <summary>OpenType com contornos PostScript (CFF, "OTTO"): o PDFsharp não consegue gravar.</summary>
    PostScript,
    /// <summary>Coleção (.ttc): várias fontes num arquivo só.</summary>
    Collection,
    /// <summary>Não é uma fonte.</summary>
    Invalid,
}

/// <summary>O que o arquivo diz de si: família ("Helvetica") e estilo ("Bold", "Regular"…).</summary>
public sealed record FontFileInfo(FontFileKind Kind, string Family, string Style);

/// <summary>Resultado de "Adicionar fonte…": famílias que entraram e avisos sobre arquivos que não serviram.</summary>
public sealed record FontAddResult(IReadOnlyList<string> Families, IReadOnlyList<string> Problems);

/// <summary>
/// "Adicionar fonte…": o usuário escolhe um arquivo de fonte que já tem (.ttf, .otf ou um .zip com fontes) e o
/// PDF Studio passa a usá-lo, sem instalar no Windows. A fonte é copiada para a pasta de fontes do app
/// (a mesma das baixadas do Google Fonts). A licença da fonte é responsabilidade de quem a adiciona.
/// </summary>
public static class FontFiles
{
    /// <summary>Extensões aceitas no seletor de arquivos.</summary>
    public static readonly string[] Extensions = [".ttf", ".otf", ".zip"];

    private const int MaxFontBytes = 30 * 1024 * 1024; // nenhuma fonte de verdade chega perto disso
    private const int MaxFontsInZip = 200;
    private const long MaxZipBytes = 300L * 1024 * 1024; // soma das fontes de um .zip

    /// <summary>
    /// Adiciona as fontes do arquivo <paramref name="file"/> (fonte avulsa ou .zip). <paramref name="folder"/>:
    /// pasta de destino (padrão: a pasta de fontes do app; os testes usam outra).
    /// </summary>
    public static FontAddResult Add(string file, string? folder = null)
    {
        folder ??= FontLibrary.DownloadedFontsFolder;
        var families = new List<string>();
        var problems = new List<string>();
        foreach (var (name, data) in Read(file, problems))
        {
            var info = Inspect(data);
            switch (info.Kind)
            {
                case FontFileKind.PostScript:
                    problems.Add($"{name}: fonte OpenType com contornos PostScript (CFF). O PDF Studio só consegue gravar fontes TrueType; procure a versão .ttf dela.");
                    continue;
                case FontFileKind.Collection:
                    problems.Add($"{name}: coleção de fontes (.ttc). Use os arquivos .ttf separados.");
                    continue;
                case FontFileKind.Invalid:
                    problems.Add($"{name}: não é um arquivo de fonte que o PDF Studio consiga ler.");
                    continue;
            }
            string? saved = Save(data, info, folder);
            if (saved is null || !FontLibrary.Register(saved))
            {
                problems.Add($"{name}: o Windows não aceitou esta fonte.");
                continue;
            }
            if (!families.Contains(info.Family, StringComparer.OrdinalIgnoreCase)) families.Add(info.Family);
        }
        if (families.Count > 0) FontLibrary.Refresh();
        return new FontAddResult(families, problems);
    }

    /// <summary>Lê o tipo, a família e o estilo da fonte (tabela "name"). Nunca lança: arquivo estranho = Invalid.</summary>
    public static FontFileInfo Inspect(byte[] data)
    {
        try
        {
            if (data.Length < 12) return new(FontFileKind.Invalid, "", "");
            uint version = BinaryPrimitives.ReadUInt32BigEndian(data);
            var kind = version switch
            {
                0x00010000 or 0x74727565 => FontFileKind.TrueType, // 1.0 ou 'true'
                0x4F54544F => FontFileKind.PostScript, // 'OTTO'
                0x74746366 => FontFileKind.Collection, // 'ttcf'
                _ => FontFileKind.Invalid,
            };
            // Só as TrueType são usadas; das outras basta saber o tipo (para explicar por que não servem).
            if (kind != FontFileKind.TrueType) return new(kind, "", "");
            var (family, style) = ReadNames(data);
            return family.Length == 0 ? new(FontFileKind.Invalid, "", "") : new(kind, family, style.Length == 0 ? "Regular" : style);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or ArgumentException)
        {
            return new(FontFileKind.Invalid, "", "");
        }
    }

    /// <summary>Os arquivos de fonte de dentro de <paramref name="file"/>: ele mesmo, ou as fontes de um .zip.</summary>
    private static List<(string Name, byte[] Data)> Read(string file, List<string> problems)
    {
        var list = new List<(string, byte[])>();
        if (!Path.GetExtension(file).Equals(".zip", StringComparison.OrdinalIgnoreCase))
        {
            if (new FileInfo(file).Length > MaxFontBytes) problems.Add($"{Path.GetFileName(file)}: arquivo grande demais para ser uma fonte.");
            else list.Add((Path.GetFileName(file), File.ReadAllBytes(file)));
            return list;
        }
        try
        {
            using var zip = ZipFile.OpenRead(file);
            long total = 0;
            int fonts = 0;
            foreach (var entry in zip.Entries)
            {
                string ext = Path.GetExtension(entry.Name);
                if (!ext.Equals(".ttf", StringComparison.OrdinalIgnoreCase) && !ext.Equals(".otf", StringComparison.OrdinalIgnoreCase)) continue;
                if (entry.Length > MaxFontBytes)
                {
                    problems.Add($"{entry.Name}: arquivo grande demais para ser uma fonte.");
                    continue;
                }
                if (++fonts > MaxFontsInZip || (total += entry.Length) > MaxZipBytes)
                {
                    problems.Add($"{Path.GetFileName(file)}: fontes demais dentro do .zip; só as primeiras foram lidas.");
                    break;
                }
                // Só a memória: o nome de dentro do .zip nunca vira caminho no disco.
                using var stream = entry.Open();
                using var copy = new MemoryStream();
                var buffer = new byte[81920];
                int read;
                while ((read = stream.Read(buffer)) > 0)
                {
                    copy.Write(buffer, 0, read);
                    if (copy.Length > MaxFontBytes) throw new InvalidDataException(); // o .zip mentiu o tamanho
                }
                list.Add((entry.Name, copy.ToArray()));
            }
        }
        catch (InvalidDataException)
        {
            problems.Add($"{Path.GetFileName(file)}: o arquivo .zip está danificado.");
            return list;
        }
        if (list.Count == 0) problems.Add($"{Path.GetFileName(file)}: não há fontes .ttf ou .otf dentro do .zip.");
        return list;
    }

    /// <summary>
    /// Grava como "Família-Estilo.ttf" (ex.: "Helvetica-Regular.ttf"); assim a caixa de edição acha o arquivo
    /// pelo nome. Se a mesma fonte já está na pasta (mesmo conteúdo), usa a que está lá.
    /// </summary>
    private static string? Save(byte[] data, FontFileInfo info, string folder)
    {
        Directory.CreateDirectory(folder);
        foreach (var existing in Directory.EnumerateFiles(folder, "*.ttf"))
            if (new FileInfo(existing).Length == data.Length && File.ReadAllBytes(existing).AsSpan().SequenceEqual(data)) return existing;

        string baseName = $"{Safe(info.Family)}-{Safe(info.Style)}";
        string path = Path.Combine(folder, baseName + ".ttf");
        for (int n = 2; File.Exists(path); n++) path = Path.Combine(folder, $"{baseName}-{n}.ttf");
        File.WriteAllBytes(path, data);
        return path;
    }

    private static string Safe(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
            if (char.IsLetterOrDigit(c)) sb.Append(c);
        return sb.Length == 0 ? "Fonte" : sb.ToString();
    }

    /// <summary>
    /// Família e estilo da tabela "name". Usa os nomes 1 e 2 (os que o Windows usa para listar a fonte;
    /// "Helvetica Light" fica como família própria), preferindo os nomes em inglês do Windows.
    /// </summary>
    private static (string Family, string Style) ReadNames(byte[] data)
    {
        int tables = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(4));
        for (int i = 0; i < tables; i++)
        {
            var entry = data.AsSpan(12 + i * 16, 16);
            if (Encoding.ASCII.GetString(entry[..4]) != "name") continue;
            int start = (int)BinaryPrimitives.ReadUInt32BigEndian(entry[8..]);
            int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + 2));
            int strings = start + BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(start + 4));
            var found = new Dictionary<int, (int Rank, string Text)>();
            for (int r = 0; r < count; r++)
            {
                var rec = data.AsSpan(start + 6 + r * 12, 12);
                int platform = BinaryPrimitives.ReadUInt16BigEndian(rec);
                int encoding = BinaryPrimitives.ReadUInt16BigEndian(rec[2..]);
                int language = BinaryPrimitives.ReadUInt16BigEndian(rec[4..]);
                int nameId = BinaryPrimitives.ReadUInt16BigEndian(rec[6..]);
                int length = BinaryPrimitives.ReadUInt16BigEndian(rec[8..]);
                int offset = BinaryPrimitives.ReadUInt16BigEndian(rec[10..]);
                if (nameId is not (1 or 2)) continue;
                var bytes = data.AsSpan(strings + offset, length);
                (int rank, string? text) = (platform, encoding) switch
                {
                    (3, 1 or 10) => (language == 0x409 ? 3 : 2, Encoding.BigEndianUnicode.GetString(bytes)),
                    (1, 0) => (1, Encoding.Latin1.GetString(bytes)),
                    _ => (0, null),
                };
                if (text is null) continue;
                text = text.Trim('\0', ' ');
                if (text.Length > 0 && (!found.TryGetValue(nameId, out var have) || rank > have.Rank)) found[nameId] = (rank, text);
            }
            return (found.GetValueOrDefault(1).Text ?? "", found.GetValueOrDefault(2).Text ?? "");
        }
        return ("", "");
    }
}
