using PdfSharp.Drawing;
using Pig = UglyToad.PdfPig;

namespace PDFStudio.Core;

/// <summary>
/// Uma palavra da página, com a posição na tela (pontos, a partir do canto superior esquerdo da página visível).
/// <see cref="Line"/> é a linha a que pertence, na ordem de leitura.
/// </summary>
public sealed record TextWord(string Text, double X, double Y, double Width, double Height, int Line)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    /// <summary>Distância de um ponto até a palavra (0 se estiver dentro).</summary>
    public double DistanceTo(double x, double y)
    {
        double dx = x < X ? X - x : x > Right ? x - Right : 0;
        double dy = y < Y ? Y - y : y > Bottom ? y - Bottom : 0;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// Leitura das palavras da página com o PdfPig (licença Apache 2.0), para selecionar texto
/// com o mouse (realçar, sublinhar, riscar).
/// </summary>
public static class PdfTextLayout
{
    // Parte da letra acima e abaixo da linha de base, em múltiplos do tamanho da fonte.
    private const double Ascent = 0.88, Descent = 0.24;

    /// <summary>
    /// Palavras da página <paramref name="pageIndex"/> (começa em 0), em ordem de leitura: linhas de cima para baixo,
    /// palavras da esquerda para a direita. Página sem texto (imagem escaneada) ou PDF com senha: lista vazia.
    /// </summary>
    public static List<TextWord> Words(string path, int pageIndex)
    {
        try
        {
            using var doc = Pig.PdfDocument.Open(File.ReadAllBytes(path));
            if (pageIndex < 0 || pageIndex >= doc.NumberOfPages) return [];
            var page = doc.GetPage(pageIndex + 1);
            // O PdfPig já devolve as posições como a página aparece (com recorte e rotação aplicados),
            // com origem embaixo à esquerda; basta inverter o eixo y.
            var geometry = new PageGeometry(0, 0, page.Width, page.Height, 0);

            var raw = new List<(string Text, double X, double Y, double W, double H)>();
            foreach (var word in page.GetWords())
            {
                if (string.IsNullOrWhiteSpace(word.Text) || word.Letters.Count == 0) continue;
                var corners = Corners(word);
                var view = corners.Select(c => geometry.PdfToView(c.X, c.Y)).ToList();
                double x1 = view.Min(p => p.X), y1 = view.Min(p => p.Y), x2 = view.Max(p => p.X), y2 = view.Max(p => p.Y);
                if (x2 - x1 < 0.1 || y2 - y1 < 0.1) continue;
                raw.Add((word.Text, x1, y1, x2 - x1, y2 - y1));
            }
            return InReadingOrder(raw);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return []; // PDF protegido ou que o PdfPig não consegue ler: simplesmente sem texto selecionável
        }
    }

    /// <summary>
    /// Os 4 cantos da palavra no espaço do PDF, pela linha de base e pelo tamanho da fonte
    /// (fica com altura uniforme, sem depender do desenho de cada letra).
    /// </summary>
    private static XPoint[] Corners(Pig.Content.Word word)
    {
        var first = word.Letters[0];
        var last = word.Letters[^1];
        double size = word.Letters.Max(l => l.PointSize);
        double sx = first.StartBaseLine.X, sy = first.StartBaseLine.Y, ex = last.EndBaseLine.X, ey = last.EndBaseLine.Y;
        double len = Math.Sqrt((ex - sx) * (ex - sx) + (ey - sy) * (ey - sy));
        if (size <= 0 || len <= 0)
        {
            var b = word.BoundingBox;
            return [new(b.BottomLeft.X, b.BottomLeft.Y), new(b.BottomRight.X, b.BottomRight.Y), new(b.TopRight.X, b.TopRight.Y), new(b.TopLeft.X, b.TopLeft.Y)];
        }
        double dx = (ex - sx) / len, dy = (ey - sy) / len; // direção do texto
        double ux = -dy, uy = dx;                          // "para cima" do texto
        return
        [
            new(sx - ux * Descent * size, sy - uy * Descent * size),
            new(ex - ux * Descent * size, ey - uy * Descent * size),
            new(ex + ux * Ascent * size, ey + uy * Ascent * size),
            new(sx + ux * Ascent * size, sy + uy * Ascent * size),
        ];
    }

    /// <summary>Agrupa em linhas (palavras na mesma altura) e ordena de cima para baixo, da esquerda para a direita.</summary>
    private static List<TextWord> InReadingOrder(List<(string Text, double X, double Y, double W, double H)> raw)
    {
        var lines = new List<List<(string Text, double X, double Y, double W, double H)>>();
        foreach (var w in raw.OrderBy(w => w.Y + w.H / 2))
        {
            double center = w.Y + w.H / 2;
            var line = lines.LastOrDefault();
            if (line is not null)
            {
                double lineCenter = line.Average(l => l.Y + l.H / 2), lineHeight = line.Min(l => l.H);
                if (Math.Abs(center - lineCenter) < Math.Min(lineHeight, w.H) * 0.5) { line.Add(w); continue; }
            }
            lines.Add([w]);
        }
        var result = new List<TextWord>();
        for (int i = 0; i < lines.Count; i++)
            foreach (var w in lines[i].OrderBy(w => w.X))
                result.Add(new TextWord(w.Text, w.X, w.Y, w.W, w.H, i));
        return result;
    }

    /// <summary>
    /// Palavras entre os pontos <paramref name="ax"/>,<paramref name="ay"/> (onde o arraste começou) e
    /// <paramref name="bx"/>,<paramref name="by"/> (onde terminou), como numa seleção de texto comum.
    /// </summary>
    public static List<TextWord> Select(IReadOnlyList<TextWord> words, double ax, double ay, double bx, double by)
    {
        if (words.Count == 0) return [];
        int a = Nearest(words, ax, ay), b = Nearest(words, bx, by);
        if (a > b) (a, b) = (b, a);
        return words.Skip(a).Take(b - a + 1).ToList();
    }

    private static int Nearest(IReadOnlyList<TextWord> words, double x, double y)
    {
        int best = 0;
        double bestDistance = double.MaxValue;
        for (int i = 0; i < words.Count; i++)
        {
            double d = words[i].DistanceTo(x, y);
            if (d < bestDistance) { bestDistance = d; best = i; }
        }
        return best;
    }

    /// <summary>Um retângulo por linha cobrindo as palavras selecionadas (x, y, largura, altura na tela).</summary>
    public static List<(double X, double Y, double Width, double Height)> LineRects(IEnumerable<TextWord> selected) =>
        selected.GroupBy(w => w.Line).OrderBy(g => g.Key).Select(g =>
        {
            double x1 = g.Min(w => w.X), y1 = g.Min(w => w.Y), x2 = g.Max(w => w.Right), y2 = g.Max(w => w.Bottom);
            return (x1, y1, x2 - x1, y2 - y1);
        }).ToList();

    /// <summary>O texto selecionado: palavras separadas por espaço, linhas por quebra de linha.</summary>
    public static string TextOf(IEnumerable<TextWord> selected) =>
        string.Join("\n", selected.GroupBy(w => w.Line).OrderBy(g => g.Key).Select(g => string.Join(" ", g.Select(w => w.Text))));
}
