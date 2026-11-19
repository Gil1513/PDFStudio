using System.Runtime.InteropServices;

namespace PDFStudio.Core;

/// <summary>De onde veio a fonte usada para escrever no PDF.</summary>
public enum FontSource
{
    /// <summary>A própria fonte original (instalada no Windows ou baixada antes pelo app).</summary>
    Original,
    /// <summary>Fonte "gêmea" que vem com o app: mesmas medidas da original (ex.: Carlito no lugar de Calibri).</summary>
    Twin,
    /// <summary>Fonte do Windows parecida (ex.: Arial no lugar de Helvetica).</summary>
    Similar,
    /// <summary>Nenhuma parecida: Arial.</summary>
    Fallback,
}

/// <summary>A fonte escolhida para substituir <paramref name="Original"/>.</summary>
public sealed record FontChoice(string Original, string Use, FontSource Source)
{
    /// <summary>Vale a pena procurar a original no Google Fonts (não temos nem ela nem uma gêmea).</summary>
    public bool CanTryDownload => Source is FontSource.Similar or FontSource.Fallback && Original.Trim().Length > 0;
}

/// <summary>
/// Etapa 5.10: fontes que o app usa além das do Windows. As "gêmeas" vêm com o app (pasta Fonts, licença SIL OFL 1.1)
/// e as baixadas do Google Fonts ficam em %LOCALAPPDATA%\PDFStudio\Fonts. Nenhuma é instalada no Windows: são
/// registradas só para este programa (GDI "privado"), e assim o resto do app as enxerga como fontes comuns.
/// </summary>
public static class FontLibrary
{
    /// <summary>Fontes gêmeas: mesmas larguras de letra da original (o texto ocupa o mesmo espaço).</summary>
    public static readonly IReadOnlyDictionary<string, string> Twins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Calibri"] = "Carlito",
        ["Cambria"] = "Caladea",
        ["Arial"] = "Liberation Sans",
        ["Helvetica"] = "Liberation Sans",
        ["Arial MT"] = "Liberation Sans",
        ["Times New Roman"] = "Liberation Serif",
        ["Times"] = "Liberation Serif",
        ["Times Roman"] = "Liberation Serif",
        ["Courier New"] = "Liberation Mono",
        ["Courier"] = "Liberation Mono",
    };

    public static string AppFontsFolder => Path.Combine(AppContext.BaseDirectory, "Fonts");

    public static string DownloadedFontsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDFStudio", "Fonts");

    private static bool _scanned;
    private static readonly HashSet<string> Registered = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object Gate = new();

    /// <summary>Registra (uma vez) as fontes da pasta do app e as baixadas. Chamado antes de listar ou usar fontes.</summary>
    public static void EnsureRegistered()
    {
        // Tudo dentro da trava: outra thread não pode ver "já registrado" antes de as fontes estarem
        // registradas (senão ela guardaria uma lista de fontes sem as gêmeas).
        lock (Gate)
        {
            if (_scanned) return;
            foreach (var folder in new[] { AppFontsFolder, DownloadedFontsFolder })
                if (Directory.Exists(folder))
                    foreach (var file in Directory.EnumerateFiles(folder, "*.ttf")) Register(file);
            _available = null;
            _scanned = true;
        }
    }

    /// <summary>Deixa a fonte do arquivo disponível só para este programa. Devolve false se o Windows recusar.</summary>
    public static bool Register(string file)
    {
        if (!OperatingSystem.IsWindows()) return false;
        lock (Gate)
        {
            if (Registered.Contains(file)) return true;
            if (AddFontResourceEx(file, FR_PRIVATE, IntPtr.Zero) == 0) return false;
            Registered.Add(file);
            return true;
        }
    }

    /// <summary>
    /// Que fonte usar no lugar de <paramref name="family"/>: a própria (se o app tem), a gêmea, uma parecida do
    /// Windows ou Arial. <paramref name="isAvailable"/> serve para os testes (padrão: lista real de fontes).
    /// </summary>
    public static FontChoice Resolve(string family, Func<string, bool>? isAvailable = null)
    {
        family = family.Trim();
        isAvailable ??= IsAvailable;
        if (family.Length > 0 && isAvailable(family)) return new(family, family, FontSource.Original);
        if (Twins.TryGetValue(family, out var twin) && isAvailable(twin)) return new(family, twin, FontSource.Twin);

        string lower = family.ToLowerInvariant().Replace(" ", "");
        string? similar = lower switch
        {
            _ when lower.StartsWith("helvetica") || lower.StartsWith("arial") => "Arial",
            _ when lower.StartsWith("times") || lower.Contains("roman") => "Times New Roman",
            _ when lower.StartsWith("courier") => "Courier New",
            _ when lower.Contains("mono") || lower.Contains("code") => "Consolas",
            _ when lower.Contains("serif") && !lower.Contains("sans") => "Georgia",
            _ => null,
        };
        if (similar is not null)
        {
            if (isAvailable(similar)) return new(family, similar, FontSource.Similar);
            if (Twins.TryGetValue(similar, out var similarTwin) && isAvailable(similarTwin)) return new(family, similarTwin, FontSource.Twin);
        }
        return new(family, isAvailable("Arial") ? "Arial" : "Liberation Sans", FontSource.Fallback);
    }

    /// <summary>
    /// A fonte existe de verdade para o app (conferida na lista real: o Windows "aceita" nomes como Helvetica
    /// trocando por outra fonte) e pode ser gravada no PDF.
    /// </summary>
    public static bool IsAvailable(string family)
    {
        HashSet<string> names;
        lock (Gate) names = _available ??= new HashSet<string>(SystemFonts.Installed(), StringComparer.OrdinalIgnoreCase);
        return names.Contains(family) && PdfPageContent.CanUseFont(family);
    }

    private static HashSet<string>? _available;

    /// <summary>Esquece a lista de fontes (depois de baixar uma nova).</summary>
    public static void Refresh()
    {
        lock (Gate) _available = null;
    }

    /// <summary>Arquivo de uma fonte do app ou baixada (para a caixa de edição mostrar a mesma letra), ou null.</summary>
    public static string? FileOf(string family)
    {
        string compact = family.Replace(" ", "");
        foreach (var folder in new[] { AppFontsFolder, DownloadedFontsFolder })
            if (Directory.Exists(folder))
                foreach (var file in Directory.EnumerateFiles(folder, "*.ttf"))
                {
                    string name = Path.GetFileNameWithoutExtension(file);
                    if (name.Equals(compact + "-Regular", StringComparison.OrdinalIgnoreCase)
                        || name.Equals(compact, StringComparison.OrdinalIgnoreCase)) return file;
                }
        return null;
    }

    private const uint FR_PRIVATE = 0x10;

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int AddFontResourceEx(string name, uint flags, IntPtr reserved);
}
