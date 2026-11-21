using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Para onde um link leva: um endereço da internet/e-mail (<see cref="Url"/>) ou uma página deste PDF (0 = primeira).</summary>
public sealed record LinkTarget(string? Url, int? Page)
{
    public static LinkTarget ToUrl(string url) => new(url, null);
    public static LinkTarget ToPage(int page) => new(null, page);
}

/// <summary>Um link da página: área na tela (pontos, canto sup. esq.) e destino (null quando o app não entende o destino).</summary>
public sealed record LinkInfo(int PageIndex, int Index, double X, double Y, double Width, double Height, LinkTarget? Target);

// Etapa 5.3: links (anotação /Link). Não aparecem no painel de comentários.
public static partial class PdfAnnotations
{
    private static readonly string[] SafeSchemes = ["http", "https", "mailto"];

    /// <summary>
    /// Arruma o endereço digitado: "site.com" vira "https://site.com" e "nome@site.com" vira "mailto:nome@site.com".
    /// Só aceita http, https e mailto; devolve null se o endereço não servir.
    /// </summary>
    public static string? NormalizeUrl(string? text)
    {
        string s = (text ?? "").Trim();
        if (s.Length == 0 || s.Any(char.IsWhiteSpace)) return null;
        if (!s.Contains("://") && !s.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase))
            s = s.Contains('@') && !s.Contains('/') ? "mailto:" + s : "https://" + s;
        if (!Uri.TryCreate(s, UriKind.Absolute, out var uri) || !SafeSchemes.Contains(uri.Scheme)) return null;
        if (uri.Scheme != "mailto" && (string.IsNullOrEmpty(uri.Host) || !uri.Host.Contains('.') && uri.Host != "localhost")) return null;
        return s;
    }

    /// <summary>Cria um link na área (<paramref name="x"/>, <paramref name="y"/>, largura, altura) da tela, em pontos.</summary>
    public static void AddLink(string input, int pageIndex, double x, double y, double width, double height, LinkTarget target, string output)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "A área do link é pequena demais.");
        Edit(input, output, doc =>
        {
            var page = GetPage(doc, pageIndex);
            var rect = PageGeometry.Of(page).ViewRectToPdf(x, y, width, height);
            var link = new PdfDictionary(doc);
            link.Elements["/Type"] = new PdfName("/Annot");
            link.Elements["/Subtype"] = new PdfName("/Link");
            link.Elements["/Rect"] = rect;
            link.Elements["/NM"] = new PdfString(Guid.NewGuid().ToString());
            link.Elements["/F"] = new PdfInteger(PrintFlag);
            var border = new PdfArray(doc);
            for (int i = 0; i < 3; i++) border.Elements.Add(new PdfInteger(0)); // sem borda: só a área clicável
            link.Elements["/Border"] = border;
            if (page.Reference is not null) link.Elements["/P"] = page.Reference;
            SetTarget(doc, link, target);
            doc.Internals.AddObject(link);
            var annots = page.Elements.GetArray("/Annots");
            if (annots is null)
            {
                annots = new PdfArray(doc);
                page.Elements["/Annots"] = annots;
            }
            annots.Elements.Add(link.Reference!);
        });
    }

    /// <summary>Troca o destino do link <paramref name="index"/> (posição em /Annots) da página.</summary>
    public static void SetLinkTarget(string input, int pageIndex, int index, LinkTarget target, string output) =>
        Edit(input, output, doc =>
        {
            var link = GetPage(doc, pageIndex).Elements.GetArray("/Annots")?.Elements.GetDictionary(index);
            if (link?.Elements.GetName("/Subtype") != "/Link") throw new InvalidOperationException("O link não foi encontrado.");
            SetTarget(doc, link, target);
        });

    /// <summary>Links da página <paramref name="pageIndex"/> (ou de todas, com null).</summary>
    public static List<LinkInfo> ListLinks(string path, int? pageIndex = null)
    {
        using var doc = PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), PdfDocumentOpenMode.Import);
        var pageNumbers = new Dictionary<PdfReference, int>();
        for (int p = 0; p < doc.PageCount; p++)
            if (doc.Pages[p].Reference is { } r) pageNumbers[r] = p;

        var result = new List<LinkInfo>();
        for (int p = 0; p < doc.PageCount; p++)
        {
            if (pageIndex is { } only && only != p) continue;
            var page = doc.Pages[p];
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            var geometry = PageGeometry.Of(page);
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                var a = annots.Elements.GetDictionary(i);
                if (a?.Elements.GetName("/Subtype") != "/Link" || a.Elements.GetArray("/Rect") is not { Elements.Count: 4 } r) continue;
                var (vx, vy, vw, vh) = geometry.PdfRectToView(r.Elements.GetReal(0), r.Elements.GetReal(1), r.Elements.GetReal(2), r.Elements.GetReal(3));
                result.Add(new LinkInfo(p, i, vx, vy, vw, vh, ReadTarget(doc, a, pageNumbers)));
            }
        }
        return result;
    }

    // ---------------- Destino ----------------

    private static void SetTarget(PdfDocument doc, PdfDictionary link, LinkTarget target)
    {
        link.Elements.Remove("/A");
        link.Elements.Remove("/Dest");
        if (target.Page is { } pageNumber)
        {
            var page = GetPage(doc, pageNumber);
            var dest = new PdfArray(doc);
            dest.Elements.Add(page.Reference!);
            dest.Elements.Add(new PdfName("/Fit")); // mostra a página inteira
            link.Elements["/Dest"] = dest;
        }
        else
        {
            string url = NormalizeUrl(target.Url) ?? throw new ArgumentException("Endereço inválido. Use um site (https://…) ou um e-mail.", nameof(target));
            var action = new PdfDictionary(doc);
            action.Elements["/S"] = new PdfName("/URI");
            action.Elements["/URI"] = new PdfString(url);
            link.Elements["/A"] = action;
        }
    }

    private static LinkTarget? ReadTarget(PdfDocument doc, PdfDictionary link, Dictionary<PdfReference, int> pageNumbers)
    {
        var action = link.Elements.GetDictionary("/A");
        if (action is not null)
        {
            string kind = action.Elements.GetName("/S");
            if (kind == "/URI") return action.Elements.GetString("/URI") is { Length: > 0 } uri ? LinkTarget.ToUrl(uri) : null;
            if (kind != "/GoTo") return null;
        }
        var dest = action is not null ? action.Elements["/D"] : link.Elements["/Dest"];
        return DestinationPage(doc, dest, pageNumbers) is { } page ? LinkTarget.ToPage(page) : null;
    }

    /// <summary>Página de um destino: [página /XYZ …], ou um nome procurado em /Dests ou /Names /Dests.</summary>
    private static int? DestinationPage(PdfDocument doc, PdfItem? dest, Dictionary<PdfReference, int> pageNumbers, int depth = 0)
    {
        if (depth > 4 || dest is null) return null;
        if (dest is PdfReference reference) dest = reference.Value;
        switch (dest)
        {
            case PdfArray { Elements.Count: > 0 } array:
                var first = array.Elements[0];
                if (first is PdfReference pageRef && pageNumbers.TryGetValue(pageRef, out int page)) return page;
                if (first is PdfInteger number && number.Value >= 0 && number.Value < doc.PageCount) return number.Value; // destino "remoto" numérico
                return null;
            case PdfDictionary dict: // { /D [ ... ] }
                return DestinationPage(doc, dict.Elements["/D"], pageNumbers, depth + 1);
            case PdfName name:
                return DestinationPage(doc, NamedDestination(doc, name.Value.TrimStart('/')), pageNumbers, depth + 1);
            case PdfString text:
                return DestinationPage(doc, NamedDestination(doc, text.Value), pageNumbers, depth + 1);
            default:
                return null;
        }
    }

    private static PdfItem? NamedDestination(PdfDocument doc, string name)
    {
        var catalog = doc.Internals.Catalog;
        if (catalog.Elements.GetDictionary("/Dests") is { } dests && dests.Elements["/" + name] is { } found) return found;
        return catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests") is { } tree ? FindInNameTree(tree, name, 0) : null;
    }

    private static PdfItem? FindInNameTree(PdfDictionary node, string name, int depth, HashSet<PdfDictionary>? seen = null)
    {
        seen ??= new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        if (depth > 20 || !seen.Add(node)) return null; // nó repetido: PDF malfeito (laço ou ramos que se repetem)
        if (node.Elements.GetArray("/Names") is { } names)
            for (int i = 0; i + 1 < names.Elements.Count; i += 2)
                if (names.Elements[i] is PdfString key && key.Value == name) return names.Elements[i + 1];
        if (node.Elements.GetArray("/Kids") is { } kids)
            for (int i = 0; i < kids.Elements.Count; i++)
                if (kids.Elements.GetDictionary(i) is { } kid && FindInNameTree(kid, name, depth + 1, seen) is { } found) return found;
        return null;
    }
}
