using System.Text.RegularExpressions;

namespace PDFStudio.Core;

/// <summary>
/// Etapa 5.10: baixa uma família do Google Fonts (fontes gratuitas, licenças SIL OFL / Apache) para
/// %LOCALAPPDATA%\PDFStudio\Fonts. Só é chamado depois que o usuário autoriza. Nada é instalado no Windows.
/// </summary>
public static class GoogleFonts
{
    private const string CssApi = "https://fonts.googleapis.com/css2";
    private const string FileHost = "https://fonts.gstatic.com/";
    private const long MaxFileSize = 20 * 1024 * 1024;

    /// <summary>Uma variante de fonte no CSS do Google Fonts.</summary>
    public sealed record Face(string Family, bool Italic, int Weight, string Url)
    {
        public string Style => (Weight >= 600, Italic) switch
        {
            (true, true) => "BoldItalic",
            (true, false) => "Bold",
            (false, true) => "Italic",
            _ => "Regular",
        };
    }

    /// <summary>
    /// Baixa normal, negrito, itálico e negrito itálico (o que existir). Devolve os arquivos gravados;
    /// lista vazia = a família não existe no Google Fonts.
    /// </summary>
    public static async Task<List<string>> DownloadAsync(string family, HttpClient http, CancellationToken cancel = default)
    {
        family = family.Trim();
        if (family.Length == 0 || family.Length > 60 || !Regex.IsMatch(family, @"^[\p{L}\p{N} ]+$"))
            return []; // nomes estranhos nem são procurados
        string query = family.Replace(' ', '+');
        // Pede as 4 variantes; se a família não tiver alguma, o Google recusa o pedido inteiro: tenta só a normal.
        string? css = await GetCssAsync(http, $"{CssApi}?family={query}:ital,wght@0,400;0,700;1,400;1,700", cancel)
            ?? await GetCssAsync(http, $"{CssApi}?family={query}", cancel);
        if (css is null) return [];

        string folder = FontLibrary.DownloadedFontsFolder;
        Directory.CreateDirectory(folder);
        var saved = new List<string>();
        foreach (var face in ParseCss(css).Where(f => f.Family.Equals(family, StringComparison.OrdinalIgnoreCase))
                     .GroupBy(f => f.Style).Select(g => g.First()))
        {
            byte[] data = await DownloadFileAsync(http, face.Url, cancel);
            string file = Path.Combine(folder, $"{family.Replace(" ", "")}-{face.Style}.ttf");
            await File.WriteAllBytesAsync(file, data, cancel);
            if (FontLibrary.Register(file)) saved.Add(file);
        }
        FontLibrary.Refresh();
        return saved;
    }

    /// <summary>Lê os blocos "@font-face" do CSS (só arquivos TrueType do servidor oficial de fontes do Google).</summary>
    public static List<Face> ParseCss(string css)
    {
        var faces = new List<Face>();
        foreach (Match block in Regex.Matches(css, @"@font-face\s*\{(?<body>[^}]*)\}"))
        {
            string body = block.Groups["body"].Value;
            string family = Regex.Match(body, @"font-family:\s*'(?<v>[^']+)'").Groups["v"].Value;
            bool italic = Regex.IsMatch(body, @"font-style:\s*italic");
            int weight = int.TryParse(Regex.Match(body, @"font-weight:\s*(?<v>\d+)").Groups["v"].Value, out int w) ? w : 400;
            string url = Regex.Match(body, @"src:\s*url\((?<v>[^)]+)\)\s*format\('truetype'\)").Groups["v"].Value;
            if (family.Length > 0 && url.StartsWith(FileHost, StringComparison.Ordinal) && url.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase))
                faces.Add(new Face(family, italic, weight, url));
        }
        return faces;
    }

    private static async Task<string?> GetCssAsync(HttpClient http, string url, CancellationToken cancel)
    {
        // Sem "User-Agent" de navegador, o Google responde com arquivos .ttf (que o PDF aceita), e não .woff2.
        using var response = await http.GetAsync(url, cancel);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(cancel);
    }

    private static async Task<byte[]> DownloadFileAsync(HttpClient http, string url, CancellationToken cancel)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancel);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaxFileSize) throw new InvalidDataException("Arquivo de fonte grande demais.");
        byte[] data = await response.Content.ReadAsByteArrayAsync(cancel);
        if (data.Length > MaxFileSize) throw new InvalidDataException("Arquivo de fonte grande demais.");
        // TrueType começa com 00 01 00 00 (ou "true"); "OTTO" (CFF) o PDF Studio não consegue gravar.
        bool trueType = data.Length > 12 && ((data[0] == 0 && data[1] == 1 && data[2] == 0 && data[3] == 0)
            || (data[0] == 't' && data[1] == 'r' && data[2] == 'u' && data[3] == 'e'));
        if (!trueType) throw new InvalidDataException("O arquivo baixado não é uma fonte TrueType.");
        return data;
    }
}
