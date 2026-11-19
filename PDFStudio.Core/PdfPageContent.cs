using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Fonts;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

/// <summary>Aparência de um texto escrito na página: fonte (nome da família no Windows), tamanho em pontos, cor e estilo.</summary>
public sealed record PageTextStyle(string FontFamily, double FontSize, AnnotationColor Color, bool Bold = false, bool Italic = false);

/// <summary>
/// Etapa 5: escrever no conteúdo da própria página (não é comentário: passa a fazer parte do desenho da página).
/// As posições são as da tela (pontos, a partir do canto superior esquerdo da página visível).
/// </summary>
public static class PdfPageContent
{
    /// <summary>
    /// Escreve <paramref name="text"/> com o canto superior esquerdo em (<paramref name="x"/>, <paramref name="y"/>).
    /// Cada quebra de linha digitada vira uma linha nova. A fonte é embutida no PDF só com as letras usadas.
    /// </summary>
    public static void AddText(string input, int pageIndex, double x, double y, string text, PageTextStyle style, string output)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Digite o texto.", nameof(text));
        var font = CreateFont(style);
        var lines = SplitLines(text);
        var brush = new XSolidBrush(XColor.FromArgb(style.Color.R, style.Color.G, style.Color.B));

        PdfAnnotations.Edit(input, output, doc =>
        {
            var page = doc.Pages[CheckPage(doc, pageIndex)];
            DrawInView(page, gfx =>
            {
                double lineHeight = font.GetHeight();
                for (int i = 0; i < lines.Count; i++)
                {
                    if (lines[i].Length == 0) continue;
                    gfx.DrawString(lines[i], font, brush, new XRect(x, y + i * lineHeight, 100000, lineHeight), XStringFormats.TopLeft);
                }
            });
        });
    }

    /// <summary>Formatos de imagem aceitos por <see cref="AddImage"/>.</summary>
    public static readonly string[] ImageExtensions = [".png", ".jpg", ".jpeg"];

    /// <summary>
    /// Coloca a imagem do arquivo <paramref name="imagePath"/> na página, ocupando o retângulo da tela
    /// (<paramref name="x"/>, <paramref name="y"/>, <paramref name="width"/>, <paramref name="height"/>, em pontos).
    /// A imagem fica "de pé" como a página aparece, mesmo em páginas giradas.
    /// </summary>
    public static void AddImage(string input, int pageIndex, double x, double y, double width, double height, string imagePath, string output)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width), "O tamanho da imagem precisa ser maior que zero.");
        using var image = OpenImage(imagePath);
        PdfAnnotations.Edit(input, output, doc =>
        {
            var page = doc.Pages[CheckPage(doc, pageIndex)];
            DrawInView(page, gfx => gfx.DrawImage(image, x, y, width, height));
        });
    }

    /// <summary>Tamanho natural da imagem em pontos (pela resolução gravada no arquivo; 96 ppp quando não tem).</summary>
    public static (double Width, double Height) ImageSize(string imagePath)
    {
        using var image = OpenImage(imagePath);
        return (image.PointWidth, image.PointHeight);
    }

    private static XImage OpenImage(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("A imagem não foi encontrada.", path);
        if (!ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new InvalidOperationException("Use uma imagem PNG ou JPG.");
        try
        {
            return XImage.FromStream(new MemoryStream(WithJfifHeader(File.ReadAllBytes(path))));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            throw new InvalidOperationException($"Não foi possível ler a imagem \"{Path.GetFileName(path)}\": {ex.Message}", ex);
        }
    }

    /// <summary>
    /// O leitor de JPEG do PDFsharp só reconhece arquivos que começam com o cabeçalho JFIF ou Exif; muitas fotos
    /// (ex.: os papéis de parede do Windows) não têm. Nesses casos acrescenta um cabeçalho JFIF (96 ppp) — a foto não muda.
    /// </summary>
    internal static byte[] WithJfifHeader(byte[] data)
    {
        bool jpeg = data.Length > 4 && data[0] == 0xFF && data[1] == 0xD8 && data[2] == 0xFF;
        if (!jpeg || data[3] is 0xE0 or 0xE1) return data;
        byte[] jfif = [0xFF, 0xE0, 0x00, 0x10, (byte)'J', (byte)'F', (byte)'I', (byte)'F', 0x00, 0x01, 0x01, 0x01, 0x00, 0x60, 0x00, 0x60, 0x00, 0x00];
        var result = new byte[data.Length + jfif.Length];
        result[0] = 0xFF;
        result[1] = 0xD8;
        jfif.CopyTo(result, 2);
        data.AsSpan(2).CopyTo(result.AsSpan(2 + jfif.Length));
        return result;
    }

    /// <summary>Largura e altura (pontos) que o texto ocupa com esse estilo.</summary>
    public static (double Width, double Height) MeasureText(string text, PageTextStyle style)
    {
        var font = CreateFont(style);
        var lines = SplitLines(text);
        var gfx = XGraphics.CreateMeasureContext(new XSize(10000, 10000), XGraphicsUnit.Point, XPageDirection.Downwards);
        double width = lines.Count == 0 ? 0 : lines.Max(l => l.Length == 0 ? 0 : gfx.MeasureString(l, font).Width);
        return (width, Math.Max(1, lines.Count) * font.GetHeight());
    }

    /// <summary>
    /// Diz se a fonte pode ser gravada no PDF (o PDFsharp embute fontes TrueType; algumas fontes do Windows não servem).
    /// </summary>
    public static bool CanUseFont(string family)
    {
        try
        {
            MeasureText("Ab", new PageTextStyle(family, 12, new AnnotationColor(0, 0, 0)));
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException)
        {
            return false;
        }
    }

    // ---------------- Infraestrutura ----------------

    internal static XFont CreateFont(PageTextStyle style)
    {
        if (style.FontSize <= 0) throw new ArgumentOutOfRangeException(nameof(style), "O tamanho da letra precisa ser maior que zero.");
        PdfFonts.Ensure();
        var fontStyle = (style.Bold, style.Italic) switch
        {
            (true, true) => XFontStyleEx.BoldItalic,
            (true, false) => XFontStyleEx.Bold,
            (false, true) => XFontStyleEx.Italic,
            _ => XFontStyleEx.Regular,
        };
        try
        {
            var font = new XFont(style.FontFamily, style.FontSize, fontStyle, new XPdfFontOptions(PdfFontEncoding.Unicode));
            _ = font.GetHeight(); // força carregar a fonte agora (erros aparecem aqui, não na hora de salvar)
            return font;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException and not InvalidOperationException)
        {
            throw new InvalidOperationException($"A fonte \"{style.FontFamily}\" não pode ser usada no PDF. Escolha outra.", ex);
        }
    }

    internal static List<string> SplitLines(string text) =>
        text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').Select(l => l.Replace('\t', ' ').TrimEnd()).ToList();

    internal static int CheckPage(PdfDocument doc, int index) =>
        index >= 0 && index < doc.PageCount ? index : throw new ArgumentOutOfRangeException(nameof(index), $"A página {index + 1} não existe.");

    /// <summary>
    /// Desenha na página com o PDFsharp usando as coordenadas da tela. O PDFsharp trata páginas giradas de um jeito
    /// próprio; por isso a rotação é tirada durante o desenho, a conversão tela → página é feita aqui e a rotação volta no fim.
    /// </summary>
    /// <param name="behind">true = desenha atrás do conteúdo que já existe (ex.: fundo, marca d'água "atrás").</param>
    /// <param name="artifact">
    /// Quando informado, o desenho fica num trecho próprio do conteúdo marcado como "artefato" do PDF
    /// (marca d'água, cabeçalho, fundo…) com a assinatura do PDF Studio, para poder ser removido depois.
    /// </param>
    internal static void DrawInView(PdfPage page, Action<XGraphics> draw, bool behind = false, ArtifactKind? artifact = null)
    {
        if (!behind) IsolateExistingContent(page);
        var before = ContentItems(page);
        var geometry = PageGeometry.Of(page);
        var media = page.MediaBox;
        int rotate = page.Rotate;
        page.Rotate = 0; // (não mexer em page.Orientation: no PDFsharp isso troca a largura pela altura da página)
        try
        {
            using (var gfx = XGraphics.FromPdfPage(page, behind ? XGraphicsPdfPageOptions.Prepend : XGraphicsPdfPageOptions.Append))
            {
                // Coordenadas do PDFsharp: origem no canto superior esquerdo da página sem rotação, y para baixo.
                XPoint ToGfx(double vx, double vy)
                {
                    var p = geometry.ViewToPdf(vx, vy);
                    return new XPoint(p.X - media.X1, media.Y2 - p.Y);
                }
                var o = ToGfx(0, 0);
                var ex = ToGfx(1, 0);
                var ey = ToGfx(0, 1);
                gfx.MultiplyTransform(new XMatrix(ex.X - o.X, ex.Y - o.Y, ey.X - o.X, ey.Y - o.Y, o.X, o.Y));
                draw(gfx);
            }
        }
        finally
        {
            page.Rotate = rotate;
        }
        if (artifact is { } kind)
            foreach (var added in ContentItems(page).Except(before))
                if (added is PdfDictionary stream)
                    SetContent(stream, Encoding.ASCII.GetBytes(ArtifactPrefix(kind) + "\n").Concat(Content(stream)).Concat(Encoding.ASCII.GetBytes("\nEMC\n")).ToArray());
    }

    // ---------------- Trechos do conteúdo da página ----------------

    /// <summary>Tipos de conteúdo que o app coloca e sabe remover depois.</summary>
    public enum ArtifactKind { Watermark, Header, Footer, PageNumber, Background }

    private const string Signature = "/PDFStudio";

    /// <summary>Início do trecho marcado: <c>/Artifact &lt;&lt;/PDFStudio /Watermark /Type /Pagination /Subtype /Watermark&gt;&gt; BDC</c>.</summary>
    private static string ArtifactPrefix(ArtifactKind kind)
    {
        string type = kind == ArtifactKind.Background ? "/Background" : "/Pagination";
        string subtype = kind switch
        {
            ArtifactKind.Watermark => " /Subtype /Watermark",
            ArtifactKind.Header => " /Subtype /Header",
            ArtifactKind.Footer or ArtifactKind.PageNumber => " /Subtype /Footer",
            _ => "",
        };
        return $"/Artifact <<{Signature} /{kind} /Type {type}{subtype}>> BDC";
    }

    /// <summary>Quantos trechos do tipo <paramref name="kind"/> (colocados pelo PDF Studio) a página tem.</summary>
    internal static int CountArtifacts(PdfPage page, ArtifactKind kind) =>
        ContentItems(page).OfType<PdfDictionary>().Count(s => IsArtifact(s, kind));

    /// <summary>Remove da página os trechos do tipo <paramref name="kind"/> colocados pelo PDF Studio.</summary>
    internal static int RemoveArtifacts(PdfPage page, ArtifactKind kind)
    {
        if (page.Elements["/Contents"] is null) return 0;
        var contents = page.Contents.Elements;
        int removed = 0;
        for (int i = contents.Count - 1; i >= 0; i--)
            if (contents.GetDictionary(i) is { } stream && IsArtifact(stream, kind))
            {
                contents.RemoveAt(i);
                removed++;
            }
        return removed;
    }

    private static bool IsArtifact(PdfDictionary stream, ArtifactKind kind)
    {
        if (stream.Stream is null) return false;
        byte[] start = Content(stream);
        string prefix = $"/Artifact <<{Signature} /{kind} ";
        return start.Length >= prefix.Length && Encoding.ASCII.GetString(start, 0, prefix.Length) == prefix;
    }

    /// <summary>
    /// Fecha o conteúdo original entre "q … Q" (uma vez por página), para que um PDF que deixou o estado gráfico
    /// alterado no fim (escala, deslocamento) não desloque o que o app desenha por cima.
    /// </summary>
    private static void IsolateExistingContent(PdfPage page)
    {
        if (page.Elements["/Contents"] is null) return;
        var contents = page.Contents.Elements;
        if (contents.Count == 0) return;
        if (contents.GetDictionary(0) is { Stream: not null } first && Encoding.ASCII.GetString(Content(first)).StartsWith("q % PDFStudio")) return;
        // O PDFsharp exige objetos PdfContent na lista /Contents (dicionários comuns quebram o XGraphics).
        page.Contents.PrependContent().CreateStream(Encoding.ASCII.GetBytes("q % PDFStudio\n"));
        page.Contents.AppendContent().CreateStream(Encoding.ASCII.GetBytes("\nQ % PDFStudio\n"));
    }

    private static List<PdfItem> ContentItems(PdfPage page)
    {
        if (page.Elements["/Contents"] is null) return [];
        var contents = page.Contents.Elements;
        var list = new List<PdfItem>();
        for (int i = 0; i < contents.Count; i++)
            if (contents.GetDictionary(i) is { } d) list.Add(d);
        return list;
    }

    private static byte[] Content(PdfDictionary stream) => stream.Stream.UnfilteredValue;

    private static void SetContent(PdfDictionary stream, byte[] data)
    {
        stream.Elements.Remove("/Filter");
        stream.Elements.Remove("/DecodeParms");
        stream.Stream.Value = data;
    }
}
