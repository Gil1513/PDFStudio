using System.Globalization;
using System.Text;
using PdfSharp.Pdf;

namespace PDFStudio.Core;

// Etapa 4.6: alterar comentários existentes (texto, cor) e cópia sem comentários (para ocultá-los na tela).
public static partial class PdfAnnotations
{
    /// <summary>Tipos cuja aparência os leitores sabem refazer sozinhos a partir da cor (/C), se for preciso apagá-la.</summary>
    private static readonly HashSet<string> Regenerable = ["/Square", "/Circle", "/Line", "/Ink", "/Highlight", "/Underline", "/StrikeOut", "/Text"];

    /// <summary>
    /// Troca o texto do comentário. Na caixa de texto, o desenho é refeito (e a caixa cresce se o texto não couber).
    /// </summary>
    public static void SetContents(string input, int pageIndex, int index, string text, string output) =>
        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var a = AnnotationAt(page, index);
            text = NormalizeNewLines(text);
            string subtype = a.Elements.GetName("/Subtype");
            if (subtype == "/FreeText" && string.IsNullOrWhiteSpace(text))
                throw new ArgumentException("A caixa de texto não pode ficar vazia. Para apagá-la, use Excluir.", nameof(text));

            if (string.IsNullOrEmpty(text)) a.Elements.Remove("/Contents");
            else a.Elements["/Contents"] = new PdfString(text, PdfStringEncoding.Unicode);
            Touch(a);

            if (subtype != "/FreeText") return;
            string da = a.Elements.GetString("/DA");
            double fontSize = FontSizeFromDA(da) ?? 12;
            var color = ColorFromDA(da) ?? new AnnotationColor(0, 0, 0);
            double lineWidth = a.Elements.GetDictionary("/BS")?.Elements.GetReal("/W") ?? 0;
            var geometry = PageGeometry.Of(page);
            var r = a.Elements.GetArray("/Rect")!;
            var (x, y, width, height) = geometry.PdfRectToView(r.Elements.GetReal(0), r.Elements.GetReal(1), r.Elements.GetReal(2), r.Elements.GetReal(3));
            double needed = TextHeight(WrapText(text, fontSize, width - 2 * TextPadding).Count, fontSize);
            if (needed > height)
            {
                height = needed;
                y = Math.Max(0, Math.Min(y, geometry.ViewHeight - height));
                a.Elements["/Rect"] = geometry.ViewRectToPdf(x, y, width, height);
            }
            WriteTextBoxAppearance(doc, a, geometry.Rotation, width, height, text, fontSize, new AnnotationStyle(color, lineWidth));
        });

    /// <summary>Troca a cor do comentário (e do desenho dele).</summary>
    public static void SetColor(string input, int pageIndex, int index, AnnotationColor color, string output) =>
        Edit(input, output, doc =>
        {
            var a = AnnotationAt(GetPage(doc, pageIndex), index);
            string subtype = a.Elements.GetName("/Subtype");
            string da = a.Elements.GetString("/DA");
            var old = ReadColor(a.Elements.GetArray("/C")) ?? ColorFromDA(da);

            if (subtype == "/FreeText") a.Elements["/DA"] = new PdfString(ReplaceDAColor(da, color));
            else a.Elements["/C"] = color.ToPdfArray(doc);
            Touch(a);

            // O desenho (/AP) feito pelo PDF Studio usa a cor como "r g b RG/rg": basta trocar os números.
            bool replaced = old is { } previous && previous != color && ReplaceAppearanceColor(a, previous, color);
            if (!replaced && old != color && Regenerable.Contains(subtype)) a.Elements.Remove("/AP"); // o leitor redesenha com a cor nova
        });

    /// <summary>Copia o PDF sem os comentários (links e campos de formulário continuam), para mostrar a página "limpa".</summary>
    public static void CopyWithoutComments(string input, string output) =>
        Edit(input, output, doc =>
        {
            foreach (var page in doc.Pages)
            {
                var annots = page.Elements.GetArray("/Annots");
                if (annots is null) continue;
                for (int i = annots.Elements.Count - 1; i >= 0; i--)
                {
                    string subtype = annots.Elements.GetDictionary(i)?.Elements.GetName("/Subtype") ?? "";
                    if (!NotComments.Contains(subtype) || subtype == "/Popup") annots.Elements.RemoveAt(i);
                }
            }
        });

    private static PdfDictionary AnnotationAt(PdfPage page, int index)
    {
        var annots = page.Elements.GetArray("/Annots") ?? throw new InvalidOperationException("A página não tem comentários.");
        return annots.Elements.GetDictionary(index) ?? throw new ArgumentOutOfRangeException(nameof(index), "Comentário não encontrado.");
    }

    /// <summary>Marca a data de alteração.</summary>
    private static void Touch(PdfDictionary a) => a.Elements["/M"] = new PdfString(PdfDate(DateTime.Now));

    private static bool ReplaceAppearanceColor(PdfDictionary a, AnnotationColor from, AnnotationColor to)
    {
        var form = a.Elements.GetDictionary("/AP")?.Elements.GetDictionary("/N");
        if (form?.Stream is null) return false;
        string content = Encoding.Latin1.GetString(form.Stream.UnfilteredValue);
        string updated = content.Replace($"{from.Pdf} RG", $"{to.Pdf} RG").Replace($"{from.Pdf} rg", $"{to.Pdf} rg");
        if (updated == content) return false;
        form.Elements.Remove("/Filter");
        form.Elements.Remove("/DecodeParms");
        form.Stream.Value = Encoding.Latin1.GetBytes(updated);
        return true;
    }

    /// <summary>Tamanho da fonte em /DA ("/Helv 12 Tf ...").</summary>
    private static double? FontSizeFromDA(string da)
    {
        var parts = da.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int tf = Array.IndexOf(parts, "Tf");
        return tf >= 1 && double.TryParse(parts[tf - 1], NumberStyles.Float, CultureInfo.InvariantCulture, out double size) && size > 0 ? size : null;
    }

    /// <summary>Troca a cor "r g b rg" do /DA, mantendo a fonte.</summary>
    private static string ReplaceDAColor(string da, AnnotationColor color)
    {
        var parts = da.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int rg = parts.LastIndexOf("rg");
        if (rg >= 3) parts.RemoveRange(rg - 3, 4);
        int g = parts.LastIndexOf("g");
        if (g >= 1) parts.RemoveRange(g - 1, 2); // cor em tons de cinza
        if (parts.Count == 0) parts.AddRange(["/Helv", "12", "Tf"]);
        return $"{string.Join(' ', parts)} {color.Pdf} rg";
    }
}
