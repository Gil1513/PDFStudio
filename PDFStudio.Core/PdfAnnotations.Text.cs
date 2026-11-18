using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

// Etapa 4.3: caixa de texto (/FreeText) e nota (/Text, o "post-it").
public static partial class PdfAnnotations
{
    /// <summary>Espaço entre a borda da caixa de texto e o texto, em pontos.</summary>
    public const double TextPadding = 4;

    /// <summary>Distância entre linhas, em múltiplos do tamanho da fonte.</summary>
    public const double LineSpacing = 1.2;

    /// <summary>Lado do ícone da nota, em pontos.</summary>
    public const double NoteIconSize = 20;

    /// <summary>
    /// Caixa de texto na posição da tela (x, y, largura, altura em pontos). O texto quebra linhas sozinho;
    /// se não couber, a caixa cresce para baixo. Retorna a altura final.
    /// A borda só é desenhada quando <see cref="AnnotationStyle.LineWidth"/> &gt; 0.
    /// </summary>
    public static double AddTextBox(string input, int pageIndex, double x, double y, double width, double height, string text,
        double fontSize, AnnotationStyle style, string author, string output)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Digite o texto da caixa.", nameof(text));
        if (fontSize <= 0) throw new ArgumentOutOfRangeException(nameof(fontSize));
        text = NormalizeNewLines(text);
        width = Math.Max(width, fontSize * 2 + 2 * TextPadding);
        double finalHeight = Math.Max(height, TextHeight(WrapText(text, fontSize, width - 2 * TextPadding).Count, fontSize));

        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var geometry = PageGeometry.Of(page);
            // Mantém a caixa dentro da página (sobe se passar do fim).
            double top = Math.Max(0, Math.Min(y, geometry.ViewHeight - finalHeight));
            double left = Math.Max(0, Math.Min(x, geometry.ViewWidth - width));
            var rect = geometry.ViewRectToPdf(left, top, width, finalHeight);

            var annot = NewAnnotation(doc, page, "/FreeText", rect, style, author, text);
            annot.Elements.Remove("/C"); // na caixa de texto, /C seria a cor de fundo; a cor do texto vai em /DA
            annot.Elements["/DA"] = new PdfString($"/Helv {N(fontSize)} Tf {style.Color.Pdf} rg");
            annot.Elements["/BS"] = BorderStyle(doc, style.LineWidth);
            WriteTextBoxAppearance(doc, annot, geometry.Rotation, width, finalHeight, text, fontSize, style);
        });
        return finalHeight;
    }

    /// <summary>
    /// Nota (ícone de balão) com o canto superior esquerdo em (x, y) da tela. O texto aparece ao abrir a nota
    /// no leitor de PDF e no painel de comentários.
    /// </summary>
    public static void AddNote(string input, int pageIndex, double x, double y, string text, AnnotationColor color,
        string author, string output)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Digite o texto da nota.", nameof(text));
        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var geometry = PageGeometry.Of(page);
            double s = NoteIconSize;
            double left = Math.Max(0, Math.Min(x, geometry.ViewWidth - s)), top = Math.Max(0, Math.Min(y, geometry.ViewHeight - s));
            var rect = geometry.ViewRectToPdf(left, top, s, s);

            var annot = NewAnnotation(doc, page, "/Text", rect, new AnnotationStyle(color), author, NormalizeNewLines(text));
            annot.Elements["/Name"] = new PdfName("/Comment");
            annot.Elements["/Open"] = new PdfBoolean(false);

            // Balão com três linhas, desenhado "de pé" mesmo em página girada.
            string ops =
                $"{color.Pdf} rg 0.2 0.2 0.2 RG 1 w " +
                "1.5 18.5 m 18.5 18.5 l 18.5 6 l 10 6 l 5 1.5 l 6 6 l 1.5 6 l h B " +
                "0.2 0.2 0.2 RG 1.2 w 4.5 14.5 m 15.5 14.5 l 4.5 11.5 m 15.5 11.5 l 4.5 8.5 m 12 8.5 l S";
            SetAppearance(doc, annot, s, s, ops, matrix: UprightMatrix(geometry.Rotation));
        });
    }

    // ---------------- Texto: medida, quebra de linha e aparência ----------------

    /// <summary>Altura necessária para <paramref name="lines"/> linhas, já com as margens.</summary>
    public static double TextHeight(int lines, double fontSize) => Math.Max(1, lines) * fontSize * LineSpacing + 2 * TextPadding;

    /// <summary>
    /// Quebra o texto em linhas que cabem em <paramref name="maxWidth"/> pontos (Helvetica/Arial no tamanho dado).
    /// Respeita as quebras de linha digitadas; palavras maiores que a largura são cortadas.
    /// </summary>
    public static List<string> WrapText(string text, double fontSize, double maxWidth)
    {
        var lines = new List<string>();
        var measure = Measurer(fontSize);
        foreach (string paragraph in NormalizeNewLines(text).Split('\n'))
        {
            string current = "";
            foreach (string word in paragraph.Split(' '))
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (measure(candidate) <= maxWidth) { current = candidate; continue; }
                if (current.Length > 0) lines.Add(current);
                current = word;
                // Palavra sozinha maior que a linha: corta letra a letra.
                while (current.Length > 1 && measure(current) > maxWidth)
                {
                    int fit = 1;
                    while (fit < current.Length && measure(current[..(fit + 1)]) <= maxWidth) fit++;
                    lines.Add(current[..fit]);
                    current = current[fit..];
                }
            }
            lines.Add(current);
        }
        return lines;
    }

    private static Func<string, double> Measurer(double fontSize)
    {
        if (OperatingSystem.IsWindows() && GlobalFontSettings.FontResolver is null)
            GlobalFontSettings.UseWindowsFontsUnderWindows = true;
        // Arial tem as mesmas larguras de letra que a Helvetica usada na aparência.
        var font = new XFont("Arial", fontSize);
        var gfx = XGraphics.CreateMeasureContext(new XSize(2000, 2000), XGraphicsUnit.Point, XPageDirection.Downwards);
        return s => s.Length == 0 ? 0 : gfx.MeasureString(WinAnsiText(s), font).Width;
    }

    private static void WriteTextBoxAppearance(PdfDocument doc, PdfDictionary annot, int rotation, double width, double height,
        string text, double fontSize, AnnotationStyle style)
    {
        var lines = WrapText(text, fontSize, width - 2 * TextPadding);
        var ops = new StringBuilder();
        if (style.LineWidth > 0)
        {
            double lw = style.LineWidth;
            ops.Append($"{style.Color.Pdf} RG {N(lw)} w {N(lw / 2)} {N(lw / 2)} {N(width - lw)} {N(height - lw)} re S ");
        }
        double leading = fontSize * LineSpacing;
        double baseline = height - TextPadding - fontSize * 0.93; // 0,93 ≈ altura das letras maiúsculas + acentos
        ops.Append($"BT /Helv {N(fontSize)} Tf {style.Color.Pdf} rg {N(leading)} TL {N(TextPadding)} {N(baseline)} Td");
        for (int i = 0; i < lines.Count; i++)
        {
            if (i > 0) ops.Append(" T*");
            ops.Append(' ').Append(PdfLiteral(WinAnsiBytes(lines[i]))).Append(" Tj");
        }
        ops.Append(" ET");

        var helv = new PdfDictionary(doc);
        helv.Elements["/Type"] = new PdfName("/Font");
        helv.Elements["/Subtype"] = new PdfName("/Type1");
        helv.Elements["/BaseFont"] = new PdfName("/Helvetica");
        helv.Elements["/Encoding"] = new PdfName("/WinAnsiEncoding");
        var fonts = new PdfDictionary(doc);
        fonts.Elements["/Helv"] = helv;
        var resources = new PdfDictionary(doc);
        resources.Elements["/Font"] = fonts;

        SetAppearance(doc, annot, width, height, ops.ToString(), style.Opacity, resources, UprightMatrix(rotation));
    }

    /// <summary>
    /// Matriz da aparência que desfaz a rotação da página, para o conteúdo aparecer "de pé" na tela.
    /// A página girada 90° (horário) é compensada girando o desenho 90° no sentido anti-horário.
    /// </summary>
    private static string? UprightMatrix(int rotation) => rotation switch
    {
        90 => "0 1 -1 0 0 0",
        180 => "-1 0 0 -1 0 0",
        270 => "0 -1 1 0 0 0",
        _ => null,
    };

    private static string NormalizeNewLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    // A fonte padrão Helvetica usa a codificação WinAnsi (Windows-1252): cobre o português (á, ã, ç, é, õ…).
    private static readonly Dictionary<char, byte> WinAnsiExtras = new()
    {
        ['€'] = 0x80, ['‚'] = 0x82, ['ƒ'] = 0x83, ['„'] = 0x84, ['…'] = 0x85, ['†'] = 0x86, ['‡'] = 0x87, ['ˆ'] = 0x88,
        ['‰'] = 0x89, ['Š'] = 0x8A, ['‹'] = 0x8B, ['Œ'] = 0x8C, ['Ž'] = 0x8E, ['‘'] = 0x91, ['’'] = 0x92, ['“'] = 0x93,
        ['”'] = 0x94, ['•'] = 0x95, ['–'] = 0x96, ['—'] = 0x97, ['˜'] = 0x98, ['™'] = 0x99, ['š'] = 0x9A, ['›'] = 0x9B,
        ['œ'] = 0x9C, ['ž'] = 0x9E, ['Ÿ'] = 0x9F,
    };

    /// <summary>Converte para WinAnsi; letras que a fonte padrão não tem viram "?".</summary>
    private static byte[] WinAnsiBytes(string s)
    {
        var bytes = new byte[s.Length];
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bytes[i] = c switch
            {
                '\t' => (byte)' ',
                >= ' ' and <= '~' => (byte)c,
                >= ' ' and <= 'ÿ' => (byte)c,
                _ when WinAnsiExtras.TryGetValue(c, out byte b) => b,
                _ => (byte)'?',
            };
        }
        return bytes;
    }

    /// <summary>O texto como será desenhado (com "?" no lugar do que a fonte não tem), para medir.</summary>
    private static string WinAnsiText(string s) =>
        new(WinAnsiBytes(s).Select(b => b < 0x80 || b >= 0xA0 ? (char)b : WinAnsiExtras.First(kv => kv.Value == b).Key).ToArray());

    /// <summary>String literal do PDF: (texto) com parênteses e barras escapados; bytes acima de 127 em octal.</summary>
    private static string PdfLiteral(byte[] bytes)
    {
        var sb = new StringBuilder("(");
        foreach (byte b in bytes)
        {
            if (b is (byte)'(' or (byte)')' or (byte)'\\') sb.Append('\\').Append((char)b);
            else if (b < 0x20 || b > 0x7E) sb.Append('\\').Append(Convert.ToString(b, 8).PadLeft(3, '0'));
            else sb.Append((char)b);
        }
        return sb.Append(')').ToString();
    }

    /// <summary>Cor do texto gravada em /DA ("... 1 0 0 rg"), usada quando o comentário não tem /C.</summary>
    private static AnnotationColor? ColorFromDA(string da)
    {
        var parts = da.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int rg = Array.LastIndexOf(parts, "rg");
        if (rg < 3) return null;
        try
        {
            double r = double.Parse(parts[rg - 3], CultureInfo.InvariantCulture);
            double g = double.Parse(parts[rg - 2], CultureInfo.InvariantCulture);
            double b = double.Parse(parts[rg - 1], CultureInfo.InvariantCulture);
            return new AnnotationColor(ToByte(r), ToByte(g), ToByte(b));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
