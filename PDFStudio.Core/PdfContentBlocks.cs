using System.Text.RegularExpressions;
using Pig = UglyToad.PdfPig;

namespace PDFStudio.Core;

/// <summary>Texto, imagem ou desenho (linha, traço, retângulo… feito com comandos de desenho do PDF).</summary>
public enum BlockKind { Text, Image, Shape }

/// <summary>
/// Uma linha de texto de um bloco, na posição da tela (pontos a partir do canto superior esquerdo da página
/// visível). <see cref="Baseline"/> é o y da linha de base (onde as letras "assentam").
/// </summary>
public sealed record BlockLine(string Text, double X, double Y, double Width, double Height, double Baseline);

/// <summary>
/// Um bloco da página: um parágrafo (linhas seguidas com a mesma letra, alinhadas) ou uma imagem.
/// Para texto, guarda a fonte original (nome gravado no PDF), o nome da família sem o prefixo de subconjunto,
/// tamanho, cor, negrito/itálico — o que a Etapa 5.9 usa para editar o texto no mesmo estilo.
/// </summary>
public sealed record ContentBlock(
    BlockKind Kind, double X, double Y, double Width, double Height,
    IReadOnlyList<BlockLine> Lines,
    string FontName = "", string FontFamily = "", double FontSize = 0,
    AnnotationColor Color = default, bool Bold = false, bool Italic = false)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public string Text => string.Join("\n", Lines.Select(l => l.Text));
    public bool Contains(double x, double y) => x >= X && x <= Right && y >= Y && y <= Bottom;
}

/// <summary>
/// Etapa 5.8 ("Editar tudo"): acha os blocos de texto e as imagens da página com o PdfPig (Apache 2.0).
/// Texto girado em relação à página fica de fora (não dá para editar como bloco).
/// </summary>
public static class PdfContentBlocks
{
    // Parte da letra acima e abaixo da linha de base, em múltiplos do tamanho da fonte (igual a PdfTextLayout).
    private const double Ascent = 0.88, Descent = 0.24;

    private sealed record Word(string Text, double X, double Y, double W, double H, double Baseline,
        string Font, double Size, AnnotationColor Color);

    /// <summary>Blocos da página <paramref name="pageIndex"/> (começa em 0), de cima para baixo. PDF ilegível: lista vazia.</summary>
    public static List<ContentBlock> Detect(string path, int pageIndex)
    {
        try
        {
            using var doc = Pig.PdfDocument.Open(File.ReadAllBytes(path));
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return [];
            return Detect(doc.GetPage(pageIndex + 1));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return [];
        }
    }

    /// <summary>Blocos de uma página já aberta (para quem percorre o documento inteiro, como o PDF→Word).</summary>
    internal static List<ContentBlock> Detect(Pig.Content.Page page)
    {
        double pageHeight = page.Height;

        var words = new List<Word>();
        foreach (var w in page.GetWords())
        {
            if (string.IsNullOrWhiteSpace(w.Text) || w.Letters.Count == 0) continue;
            if (w.TextOrientation != Pig.Content.TextOrientation.Horizontal) continue;
            var first = w.Letters[0];
            double size = w.Letters.Max(l => l.PointSize);
            if (size <= 0) continue;
            double x1 = first.StartBaseLine.X, x2 = w.Letters[^1].EndBaseLine.X;
            if (x2 <= x1) continue;
            double baseline = pageHeight - first.StartBaseLine.Y;
            words.Add(new Word(w.Text, x1, baseline - Ascent * size, x2 - x1, (Ascent + Descent) * size, baseline,
                first.FontName ?? "", Math.Round(size, 1), ColorOf(first)));
        }

        var blocks = TextBlocks(words);
        foreach (var image in page.GetImages())
        {
            var b = image.Bounds;
            double w = b.Width, h = b.Height;
            if (w < 1 || h < 1) continue;
            blocks.Add(new ContentBlock(BlockKind.Image, b.Left, pageHeight - b.Top, w, h, []));
        }
        return blocks.OrderBy(b => b.Y).ThenBy(b => b.X).ToList();
    }

    /// <summary>Palavras → linhas (mesma altura, sem buraco grande) → parágrafos (linhas próximas, mesmo estilo).</summary>
    private static List<ContentBlock> TextBlocks(List<Word> words)
    {
        // 1) Linhas: mesma linha de base e palavras próximas (um buraco grande separa colunas).
        var lines = new List<List<Word>>();
        foreach (var w in words.OrderBy(w => Math.Round(w.Baseline)).ThenBy(w => w.X))
        {
            var line = lines.LastOrDefault(l => Math.Abs(l[0].Baseline - w.Baseline) < w.Size * 0.3
                && w.X - l[^1].X - l[^1].W < Math.Max(w.Size, l[^1].Size) * 1.5 && w.X >= l[^1].X);
            if (line is not null) line.Add(w);
            else lines.Add([w]);
        }

        // 2) Blocos: a linha seguinte entra no bloco se está logo abaixo, com a mesma letra e se sobrepõe na horizontal.
        var groups = new List<List<List<Word>>>();
        foreach (var line in lines.OrderBy(l => l[0].Baseline).ThenBy(l => l[0].X))
        {
            var style = Style(line);
            double left = line[0].X, right = line[^1].X + line[^1].W;
            var group = groups.LastOrDefault(g =>
            {
                var last = g[^1];
                var s = Style(last);
                double gap = line[0].Baseline - last[0].Baseline;
                double gLeft = g.Min(l => l[0].X), gRight = g.Max(l => l[^1].X + l[^1].W);
                return s.Font == style.Font && Math.Abs(s.Size - style.Size) < 0.6
                    && gap > 0 && gap < style.Size * 1.8
                    && left < gRight && right > gLeft;
            });
            if (group is not null) group.Add(line);
            else groups.Add([line]);
        }

        var result = new List<ContentBlock>();
        foreach (var g in groups)
        {
            var blockLines = g.Select(l =>
            {
                double x = l[0].X, r = l[^1].X + l[^1].W;
                double top = l.Min(w => w.Y), bottom = l.Max(w => w.Y + w.H);
                return new BlockLine(string.Join(" ", l.Select(w => w.Text)), x, top, r - x, bottom - top, l[0].Baseline);
            }).ToList();
            var (font, size, color) = Style(g[0]);
            var (family, bold, italic) = ParseFontName(font);
            double bx = blockLines.Min(l => l.X), by = blockLines.Min(l => l.Y);
            double br = blockLines.Max(l => l.X + l.Width), bb = blockLines.Max(l => l.Y + l.Height);
            result.Add(new ContentBlock(BlockKind.Text, bx, by, br - bx, bb - by, blockLines, font, family, size, color, bold, italic));
        }
        return result;
    }

    /// <summary>
    /// Separa um bloco de texto em um bloco por linha (mesma fonte, tamanho e cor), para mexer só em parte dele.
    /// Imagem, desenho ou bloco de uma linha só voltam como estão.
    /// </summary>
    public static List<ContentBlock> SplitLines(ContentBlock block)
    {
        if (block.Kind != BlockKind.Text || block.Lines.Count < 2) return [block];
        return block.Lines.Select(l => block with { X = l.X, Y = l.Y, Width = l.Width, Height = l.Height, Lines = [l] }).ToList();
    }

    /// <summary>O estilo que mais aparece na linha (fonte, tamanho, cor).</summary>
    private static (string Font, double Size, AnnotationColor Color) Style(List<Word> line) =>
        line.GroupBy(w => (w.Font, w.Size, w.Color)).OrderByDescending(g => g.Sum(w => w.Text.Length)).First().Key;

    private static AnnotationColor ColorOf(Pig.Content.Letter letter)
    {
        try
        {
            var (r, g, b) = letter.Color.ToRGBValues();
            return new AnnotationColor((byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
        }
        catch
        {
            return new AnnotationColor(0, 0, 0);
        }
    }

    // Nomes de estilo em outras línguas (hebraico, português, espanhol, alemão, francês, italiano, holandês,
    // polonês, russo, turco, chinês e japonês). O holandês "Vet" (negrito) fica de fora: está dentro de "Helvetica".
    private const string LocalBold = "עבה|מודגש|Negrito|Negrita|Fett|Gras|Grassetto|Pogrubiony|Полужирный|Жирный|Kalın|粗体|粗體|太字";
    private const string LocalItalic = "נטוי|Itálico|Italico|Cursiva|Kursiv|Italique|Corsivo|Cursief|Kursywa|Курсив|İtalik|斜体|斜體";
    private const string LocalRegular = "רגיל|Обычный";

    /// <summary>
    /// "ABCDEF+Calibri-BoldItalic" → ("Calibri", negrito, itálico). Tira o prefixo de subconjunto (6 letras e "+"),
    /// os sufixos de estilo e separa nomes colados ("TimesNewRomanPSMT" → "Times New Roman").
    /// </summary>
    public static (string Family, bool Bold, bool Italic) ParseFontName(string name)
    {
        // Tira caracteres invisíveis (marcas de direção do hebraico/árabe, espaço de largura zero…): grudados no
        // nome ("Helvetica‭ ‬"), faziam o app não achar a fonte.
        string n = new string((name ?? "").Where(c => char.GetUnicodeCategory(c) != System.Globalization.UnicodeCategory.Format).ToArray()).Trim();
        n = Regex.Replace(n, "^[A-Z]{6}\\+", "");
        int cut = n.IndexOfAny([',', '-']);
        string family = cut > 0 ? n[..cut] : n;
        // Estilo escrito na língua do computador que fez o PDF ("Helveticaעבה" = Helvetica negrito, em hebraico).
        // Só vale no fim do nome (ou depois do "-"), para não confundir com pedaços do nome da família.
        var local = Regex.Match(family, $"(?:[\\s_]*(?:{LocalBold}|{LocalItalic}|{LocalRegular}))+$", RegexOptions.IgnoreCase);
        string style = local.Value + (cut > 0 ? n[cut..] : "");
        if (local.Success) family = family[..local.Index];
        bool bold = Regex.IsMatch(n, "Bold|Black|Heavy|Semibold|Demi", RegexOptions.IgnoreCase)
            || Regex.IsMatch(style, LocalBold, RegexOptions.IgnoreCase);
        bool italic = Regex.IsMatch(n, "Italic|Oblique|Inclined", RegexOptions.IgnoreCase)
            || Regex.IsMatch(style, LocalItalic, RegexOptions.IgnoreCase);
        family = Regex.Replace(family, "(PSMT|PS|MT)$", "");
        family = Regex.Replace(family, "(Bold|Italic|Oblique|Regular)+$", "", RegexOptions.IgnoreCase);
        family = Regex.Replace(family, "(?<=[a-z])(?=[A-Z])", " ").Trim(); // "TimesNewRoman" → "Times New Roman"
        return (family.Length == 0 ? n : family, bold, italic);
    }
}
