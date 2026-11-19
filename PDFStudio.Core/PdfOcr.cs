using PdfSharp.Drawing;
using PdfSharp.Pdf;
using Pig = UglyToad.PdfPig;

namespace PDFStudio.Core;

/// <summary>
/// Uma palavra reconhecida pelo OCR, com a posição na página como ela aparece na tela
/// (pontos, a partir do canto superior esquerdo).
/// </summary>
public sealed record OcrWord(string Text, double X, double Y, double Width, double Height);

/// <summary>
/// Etapa 7.7: OCR. O reconhecimento em si é feito no app (motor de OCR do Windows); aqui fica o que dá para
/// testar: quais páginas não têm texto e a gravação do texto reconhecido como uma camada invisível por cima
/// da página. A página continua com a mesma aparência, mas o texto passa a poder ser pesquisado, selecionado e copiado.
/// </summary>
public static class PdfOcr
{
    /// <summary>Página com menos letras que isso é tratada como "só imagem" (um número de página solto não conta).</summary>
    public const int MinLetters = 20;

    /// <summary>Índices (de 0 em diante) das páginas que não têm texto, como documentos escaneados.</summary>
    public static List<int> PagesWithoutText(string path)
    {
        var result = new List<int>();
        using var doc = Pig.PdfDocument.Open(File.ReadAllBytes(path));
        int index = 0;
        foreach (var page in doc.GetPages())
        {
            if (page.Letters.Count(l => !string.IsNullOrWhiteSpace(l.Value)) < MinLetters) result.Add(index);
            index++;
        }
        return result;
    }

    /// <summary>
    /// Grava as palavras de cada página (chave = índice da página) como texto invisível, cada uma esticada ou
    /// encolhida para ocupar exatamente o lugar dela na imagem (assim a seleção cai em cima da palavra certa).
    /// </summary>
    public static void AddTextLayer(string input, IReadOnlyDictionary<int, IReadOnlyList<OcrWord>> pages, string output)
    {
        PdfFonts.Ensure(); // prepara o leitor de fontes do Windows (sem ele o PDFsharp não acha a Arial)
        PdfAnnotations.Edit(input, output, doc =>
        {
            // Unicode: qualquer letra que o OCR devolver (acentos, travessões, aspas) é gravada e copiada certo.
            var options = new XPdfFontOptions(PdfFontEncoding.Unicode);
            var brush = XBrushes.Black; // não aparece: o texto é gravado no modo invisível (veja DrawInView)
            foreach (var (index, words) in pages)
            {
                if (index < 0 || index >= doc.PageCount) throw new ArgumentOutOfRangeException(nameof(pages), $"A página {index + 1} não existe.");
                var usable = words.Where(w => w.Text.Trim().Length > 0 && w.Width > 0 && w.Height > 0).ToList();
                if (usable.Count == 0) continue;
                PdfPageContent.DrawInView(doc.Pages[index], gfx =>
                {
                    foreach (var word in usable)
                    {
                        string text = word.Text.Trim();
                        // A caixa do OCR vai mais ou menos do topo das letras até a base das "pernas" (g, p):
                        // a fonte com essa altura e a linha de base a 80% da caixa encaixam bem.
                        var font = new XFont("Arial", word.Height, XFontStyleEx.Regular, options);
                        double measured = gfx.MeasureString(text, font).Width;
                        if (measured <= 0) continue;
                        var state = gfx.Save();
                        gfx.TranslateTransform(word.X, word.Y + word.Height * 0.8);
                        gfx.ScaleTransform(word.Width / measured, 1);
                        gfx.DrawString(text, font, brush, 0, 0);
                        gfx.Restore(state);
                    }
                }, invisibleText: true);
            }
        });
    }
}
