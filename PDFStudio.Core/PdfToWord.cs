using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using Pig = UglyToad.PdfPig;

namespace PDFStudio.Core;

/// <summary>O que foi para o Word: páginas, parágrafos e imagens.</summary>
public sealed record PdfToWordResult(int Pages, int Paragraphs, int Images);

/// <summary>
/// Etapa 7.8: PDF → Word (.docx). Um .docx é um arquivo zip com alguns XML dentro; ele é escrito aqui mesmo,
/// sem biblioteca. Cada bloco de texto da página (o mesmo que o "Editar tudo" encontra) vira um parágrafo com a
/// mesma fonte, tamanho, cor, negrito e itálico, e as imagens entram na mesma ordem em que aparecem.
/// Limites: tabelas, colunas lado a lado e desenhos (linhas, formas) não são remontados; páginas escaneadas
/// entram como imagem (faça o OCR antes se quiser o texto).
/// </summary>
public static class PdfToWord
{
    private const double EmuPerPoint = 12700, TwipsPerPoint = 20;
    private const double MinMargin = 18, MaxMargin = 90, TopMargin = 36;

    private abstract record Item(double X, double Y, double Width, double Height);
    private sealed record TextItem(ContentBlock Block) : Item(Block.X, Block.Y, Block.Width, Block.Height);
    private sealed record ImageItem(double X, double Y, double Width, double Height, byte[] Bytes, string Extension) : Item(X, Y, Width, Height);

    /// <summary>Converte <paramref name="input"/> e grava o .docx em <paramref name="output"/>.</summary>
    public static PdfToWordResult Convert(string input, string output, string? password = null, IProgress<int>? progress = null)
    {
        var options = new Pig.ParsingOptions { Password = password ?? "" };
        using var doc = Pig.PdfDocument.Open(File.ReadAllBytes(input), options);

        var pages = new List<(double Width, double Height, List<Item> Items)>();
        for (int n = 1; n <= doc.NumberOfPages; n++)
        {
            var page = doc.GetPage(n);
            var items = new List<Item>();
            items.AddRange(PdfContentBlocks.Detect(page).Where(b => b.Kind == BlockKind.Text).Select(b => new TextItem(b)));
            items.AddRange(Images(page));
            pages.Add((page.Width, page.Height, items.OrderBy(i => Math.Round(i.Y)).ThenBy(i => i.X).ToList()));
            progress?.Report(n * 90 / Math.Max(1, doc.NumberOfPages));
        }

        // Tamanho e margens do documento pela primeira página; a margem acompanha onde o conteúdo começa.
        double pageW = pages.Count > 0 ? pages[0].Width : 595, pageH = pages.Count > 0 ? pages[0].Height : 842;
        var lefts = pages.SelectMany(p => p.Items).Select(i => i.X).Where(x => x >= 0).ToList();
        double margin = Math.Clamp(lefts.Count > 0 ? lefts.Min() : 72, MinMargin, MaxMargin);
        double contentW = Math.Max(72, pageW - 2 * margin), contentH = Math.Max(72, pageH - 2 * TopMargin);

        var body = new StringBuilder();
        var media = new List<(string Name, byte[] Bytes)>();
        int paragraphs = 0;
        for (int p = 0; p < pages.Count; p++)
        {
            if (p > 0) body.Append("<w:p><w:r><w:br w:type=\"page\"/></w:r></w:p>");
            double previousBottom = TopMargin;
            foreach (var item in pages[p].Items)
            {
                double before = Math.Clamp(item.Y - previousBottom, 0, 200);
                double indent = Math.Clamp(item.X - margin, 0, contentW * 0.8);
                if (item is TextItem t)
                {
                    bool centered = Math.Abs(t.X + t.Width / 2 - pageW / 2) < 8 && t.Width < contentW * 0.8 && t.X > margin + 20;
                    body.Append(Paragraph(t.Block, before, centered ? 0 : indent, centered));
                    paragraphs++;
                }
                else if (item is ImageItem img)
                {
                    double scale = Math.Min(1, Math.Min((contentW - indent) / img.Width, contentH / img.Height));
                    media.Add(($"imagem{media.Count + 1}.{img.Extension}", img.Bytes));
                    body.Append(Picture(media.Count, img.Width * scale, img.Height * scale, before, indent));
                }
                previousBottom = Math.Max(previousBottom, item.Y + item.Height);
            }
        }
        if (body.Length == 0) body.Append("<w:p/>");
        body.Append(Section(pageW, pageH, margin));

        Write(output, body.ToString(), media);
        progress?.Report(100);
        return new PdfToWordResult(pages.Count, paragraphs, media.Count);
    }

    /// <summary>Imagens da página com os bytes: PNG quando o PdfPig consegue montar, JPEG quando já está gravado assim.</summary>
    private static IEnumerable<ImageItem> Images(Pig.Content.Page page)
    {
        foreach (var image in page.GetImages())
        {
            var b = image.Bounds;
            // A caixa pode vir girada: usa os cantos para achar a área ocupada.
            double[] xs = [b.TopLeft.X, b.TopRight.X, b.BottomLeft.X, b.BottomRight.X];
            double[] ys = [b.TopLeft.Y, b.TopRight.Y, b.BottomLeft.Y, b.BottomRight.Y];
            double w = xs.Max() - xs.Min(), h = ys.Max() - ys.Min();
            if (w < 4 || h < 4) continue;

            byte[]? bytes = null;
            string ext = "png";
            try
            {
                var raw = image.RawMemory.ToArray();
                if (raw.Length > 3 && raw[0] == 0xFF && raw[1] == 0xD8) { bytes = raw; ext = "jpeg"; }
                else if (image.TryGetPng(out var png) && png is { Length: > 0 }) bytes = png;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException) { }
            if (bytes is null) continue; // formato que não dá para levar (ex.: JPEG 2000, fax)
            yield return new ImageItem(xs.Min(), page.Height - ys.Max(), w, h, bytes, ext);
        }
    }

    private static string Paragraph(ContentBlock block, double before, double indent, bool centered)
    {
        var sb = new StringBuilder("<w:p><w:pPr>");
        sb.Append("<w:spacing w:before=\"").Append(Twips(before)).Append("\" w:after=\"0\"");
        if (block.Lines.Count > 1)
        {
            // Distância entre as linhas como no PDF (em relação ao "simples" do Word, ~1,17 × o tamanho).
            double pitch = (block.Lines[^1].Baseline - block.Lines[0].Baseline) / (block.Lines.Count - 1);
            int line = (int)Math.Round(Math.Clamp(240 * pitch / (block.FontSize * 1.17), 200, 480));
            sb.Append(" w:line=\"").Append(line).Append("\" w:lineRule=\"auto\"");
        }
        sb.Append("/>");
        if (indent > 0) sb.Append("<w:ind w:left=\"").Append(Twips(indent)).Append("\"/>");
        if (centered) sb.Append("<w:jc w:val=\"center\"/>");
        sb.Append("</w:pPr><w:r>").Append(RunProperties(block)).Append("<w:t xml:space=\"preserve\">")
          .Append(Escape(JoinLines(block.Lines.Select(l => l.Text)))).Append("</w:t></w:r></w:p>");
        return sb.ToString();
    }

    private static string RunProperties(ContentBlock block)
    {
        var sb = new StringBuilder("<w:rPr>");
        if (block.FontFamily.Length > 0)
        {
            string f = Escape(block.FontFamily);
            sb.Append($"<w:rFonts w:ascii=\"{f}\" w:hAnsi=\"{f}\" w:cs=\"{f}\"/>");
        }
        if (block.Bold) sb.Append("<w:b/>");
        if (block.Italic) sb.Append("<w:i/>");
        var c = block.Color;
        if (c.R != 0 || c.G != 0 || c.B != 0) sb.Append($"<w:color w:val=\"{c.R:X2}{c.G:X2}{c.B:X2}\"/>");
        if (block.FontSize > 0)
        {
            int half = (int)Math.Round(Math.Clamp(block.FontSize, 1, 1638) * 2);
            sb.Append($"<w:sz w:val=\"{half}\"/><w:szCs w:val=\"{half}\"/>");
        }
        return sb.Append("</w:rPr>").ToString();
    }

    /// <summary>
    /// As linhas do bloco viram um parágrafo corrido (o Word quebra as linhas sozinho). Palavra dividida com hífen
    /// no fim da linha ("compu-" + "tador") volta a ficar inteira.
    /// </summary>
    public static string JoinLines(IEnumerable<string> lines)
    {
        var sb = new StringBuilder();
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0) continue;
            if (sb.Length > 0)
            {
                bool hyphen = sb.Length > 1 && sb[^1] == '-' && char.IsLetter(sb[^2]) && char.IsLower(line[0]);
                if (hyphen) sb.Length--;
                else sb.Append(' ');
            }
            sb.Append(line);
        }
        return sb.ToString();
    }

    private static string Picture(int id, double widthPt, double heightPt, double before, double indent)
    {
        long cx = (long)Math.Round(widthPt * EmuPerPoint), cy = (long)Math.Round(heightPt * EmuPerPoint);
        string ind = indent > 0 ? $"<w:ind w:left=\"{Twips(indent)}\"/>" : "";
        return $"<w:p><w:pPr><w:spacing w:before=\"{Twips(before)}\" w:after=\"0\"/>{ind}</w:pPr><w:r><w:drawing>" +
               $"<wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"{cx}\" cy=\"{cy}\"/>" +
               $"<wp:docPr id=\"{id}\" name=\"Imagem {id}\"/>" +
               "<wp:cNvGraphicFramePr><a:graphicFrameLocks xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" noChangeAspect=\"1\"/></wp:cNvGraphicFramePr>" +
               "<a:graphic xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\">" +
               "<a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
               "<pic:pic xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\">" +
               $"<pic:nvPicPr><pic:cNvPr id=\"{id}\" name=\"imagem{id}\"/><pic:cNvPicPr/></pic:nvPicPr>" +
               $"<pic:blipFill><a:blip r:embed=\"rIdImg{id}\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill>" +
               $"<pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"{cx}\" cy=\"{cy}\"/></a:xfrm><a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr>" +
               "</pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r></w:p>";
    }

    private static string Section(double pageW, double pageH, double margin)
    {
        string orient = pageW > pageH ? " w:orient=\"landscape\"" : "";
        return $"<w:sectPr><w:pgSz w:w=\"{Twips(pageW)}\" w:h=\"{Twips(pageH)}\"{orient}/>" +
               $"<w:pgMar w:top=\"{Twips(TopMargin)}\" w:right=\"{Twips(margin)}\" w:bottom=\"{Twips(TopMargin)}\" w:left=\"{Twips(margin)}\" " +
               "w:header=\"0\" w:footer=\"0\" w:gutter=\"0\"/></w:sectPr>";
    }

    private static string Twips(double points) => ((long)Math.Round(points * TwipsPerPoint)).ToString(CultureInfo.InvariantCulture);

    /// <summary>Texto seguro para XML: escapa &amp;, &lt;, &gt; e aspas e tira caracteres que o XML não aceita.</summary>
    internal static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { sb.Append(c).Append(text[++i]); continue; }
            if (!XmlConvert.IsXmlChar(c)) continue;
            sb.Append(c switch { '&' => "&amp;", '<' => "&lt;", '>' => "&gt;", '"' => "&quot;", _ => c.ToString() });
        }
        return sb.ToString();
    }

    private static void Write(string output, string body, List<(string Name, byte[] Bytes)> media)
    {
        string temp = output + ".tmp";
        using (var zip = ZipFile.Open(temp, ZipArchiveMode.Create))
        {
            void Add(string name, string xml)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open(), new UTF8Encoding(false));
                writer.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>\n");
                writer.Write(xml);
            }

            Add("[Content_Types].xml",
                "<Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
                "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/>" +
                "<Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
                "<Default Extension=\"png\" ContentType=\"image/png\"/>" +
                "<Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/>" +
                "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
                "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
                "</Types>");
            Add("_rels/.rels",
                "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
                "</Relationships>");

            var rels = new StringBuilder("<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
                "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>");
            for (int i = 0; i < media.Count; i++)
                rels.Append($"<Relationship Id=\"rIdImg{i + 1}\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/{media[i].Name}\"/>");
            Add("word/_rels/document.xml.rels", rels.Append("</Relationships>").ToString());

            // Padrão do documento: Calibri 11, sem espaço extra entre parágrafos (os espaços vêm do PDF).
            Add("word/styles.xml",
                "<w:styles xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:docDefaults>" +
                "<w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\"/><w:sz w:val=\"22\"/><w:szCs w:val=\"22\"/><w:lang w:val=\"pt-BR\"/></w:rPr></w:rPrDefault>" +
                "<w:pPrDefault><w:pPr><w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault>" +
                "</w:docDefaults></w:styles>");

            Add("word/document.xml",
                "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\" " +
                "xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" " +
                "xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\">" +
                "<w:body>" + body + "</w:body></w:document>");

            foreach (var (name, bytes) in media)
            {
                using var stream = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression).Open();
                stream.Write(bytes);
            }
        }
        File.Move(temp, output, overwrite: true);
    }
}
