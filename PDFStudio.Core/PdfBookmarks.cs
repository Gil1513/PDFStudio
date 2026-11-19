using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>
/// Um marcador (item do sumário do PDF). <see cref="Page"/> começa em 0 e é null quando o marcador não leva a
/// uma página deste arquivo (ex.: abre um site). <see cref="Top"/> é a altura na página (pontos a partir do topo
/// da página na tela), ou null para "início da página".
/// </summary>
public sealed record Bookmark(string Title, int? Page, double? Top, bool IsOpen, IReadOnlyList<Bookmark> Children);

/// <summary>
/// Etapa 7.2: marcadores. Lê e altera a árvore /Outlines direto no arquivo, mexendo só no item afetado: os outros
/// marcadores ficam como estão (cores, estilos, ações que o app não conhece). Um marcador é indicado pelo
/// "caminho": a posição dele em cada nível (ex.: [1, 0] = 1º filho do 2º marcador).
/// </summary>
public static class PdfBookmarks
{
    /// <summary>Profundidade máxima lida (proteção contra arquivos com laços na árvore).</summary>
    private const int MaxDepth = 64;

    public static List<Bookmark> List(string path)
    {
        using var doc = PdfReader.Open(path, PdfDocumentOpenMode.Import);
        if (Root(doc, create: false) is not { } root) return [];
        var visited = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        return Read(doc, root, 0, visited);
    }

    private static List<Bookmark> Read(PdfDocument doc, PdfDictionary parent, int depth, HashSet<PdfDictionary> visited)
    {
        var list = new List<Bookmark>();
        if (depth > MaxDepth) return list;
        foreach (var item in Children(parent))
        {
            if (!visited.Add(item)) break; // laço: para de ler este nível
            var (page, top) = Destination(doc, item);
            list.Add(new Bookmark(item.Elements.GetString("/Title"), page, top, IsOpen(item), Read(doc, item, depth + 1, visited)));
        }
        return list;
    }

    /// <summary>Acrescenta um marcador em <paramref name="parentPath"/> (vazio = nível de cima), na posição <paramref name="index"/>.</summary>
    public static void Add(string input, IReadOnlyList<int> parentPath, int index, string title, int page, double? top, string output)
    {
        title = CheckTitle(title);
        PdfAnnotations.Edit(input, output, doc =>
        {
            var root = Root(doc, create: true)!;
            var parent = parentPath.Count == 0 ? root : Find(root, parentPath);
            var item = new PdfDictionary(doc);
            item.Elements["/Title"] = new PdfString(title, PdfStringEncoding.Unicode);
            item.Elements["/Dest"] = NewDestination(doc, page, top);
            doc.Internals.AddObject(item);
            var children = Children(parent).ToList();
            children.Insert(Math.Clamp(index, 0, children.Count), item);
            Relink(parent, children, root);
            if (!ReferenceEquals(parent, root)) SetOpen(parent); // mostra o que acabou de entrar
            Recount(root);
        });
    }

    public static void Rename(string input, IReadOnlyList<int> path, string title, string output)
    {
        title = CheckTitle(title);
        PdfAnnotations.Edit(input, output, doc =>
            Find(Root(doc, false) ?? throw Missing(), path).Elements["/Title"] = new PdfString(title, PdfStringEncoding.Unicode));
    }

    /// <summary>Muda para onde o marcador leva (a página e a altura vistas na tela agora).</summary>
    public static void SetDestination(string input, IReadOnlyList<int> path, int page, double? top, string output)
    {
        PdfAnnotations.Edit(input, output, doc =>
        {
            var item = Find(Root(doc, false) ?? throw Missing(), path);
            item.Elements.Remove("/A");
            item.Elements["/Dest"] = NewDestination(doc, page, top);
        });
    }

    /// <summary>Exclui o marcador e os que estão dentro dele. Sem marcadores, o /Outlines sai do arquivo.</summary>
    public static void Delete(string input, IReadOnlyList<int> path, string output)
    {
        PdfAnnotations.Edit(input, output, doc =>
        {
            var root = Root(doc, false) ?? throw Missing();
            var item = Find(root, path);
            var parent = ParentOf(root, path);
            Relink(parent, Children(parent).Where(c => !ReferenceEquals(c, item)).ToList(), root);
            if (Children(root).Count == 0) doc.Internals.Catalog.Elements.Remove("/Outlines");
            else Recount(root);
        });
    }

    /// <summary>
    /// Move o marcador (com os de dentro) para <paramref name="newParentPath"/>, na posição <paramref name="newIndex"/>
    /// (contada depois de tirá-lo do lugar antigo). Os caminhos são os de antes da mudança.
    /// </summary>
    public static void Move(string input, IReadOnlyList<int> path, IReadOnlyList<int> newParentPath, int newIndex, string output)
    {
        if (newParentPath.Count >= path.Count && newParentPath.Take(path.Count).SequenceEqual(path))
            throw new ArgumentException("Um marcador não pode ir para dentro dele mesmo.");
        PdfAnnotations.Edit(input, output, doc =>
        {
            var root = Root(doc, false) ?? throw Missing();
            var item = Find(root, path);
            var oldParent = ParentOf(root, path);
            var newParent = newParentPath.Count == 0 ? root : Find(root, newParentPath); // achado antes de mexer
            Relink(oldParent, Children(oldParent).Where(c => !ReferenceEquals(c, item)).ToList(), root);
            var children = Children(newParent).ToList();
            children.Insert(Math.Clamp(newIndex, 0, children.Count), item);
            Relink(newParent, children, root);
            if (!ReferenceEquals(newParent, root)) SetOpen(newParent); // mostra o que acabou de entrar
            Recount(root);
        });
    }

    // ---------------- Árvore ----------------

    private static PdfDictionary? Root(PdfDocument doc, bool create)
    {
        var root = doc.Internals.Catalog.Elements.GetDictionary("/Outlines");
        if (root is not null || !create) return root;
        root = new PdfDictionary(doc);
        root.Elements.SetName("/Type", "/Outlines");
        doc.Internals.AddObject(root);
        doc.Internals.Catalog.Elements["/Outlines"] = root.Reference;
        return root;
    }

    /// <summary>Filhos na ordem (/First, depois /Next), parando se a corrente der volta.</summary>
    private static List<PdfDictionary> Children(PdfDictionary parent)
    {
        var list = new List<PdfDictionary>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var item = parent.Elements.GetDictionary("/First"); item is not null && seen.Add(item); item = item.Elements.GetDictionary("/Next"))
            list.Add(item);
        return list;
    }

    private static PdfDictionary Find(PdfDictionary root, IReadOnlyList<int> path)
    {
        if (path.Count == 0) throw new ArgumentException("Escolha um marcador.");
        var current = root;
        foreach (int i in path)
        {
            var children = Children(current);
            if (i < 0 || i >= children.Count) throw Missing();
            current = children[i];
        }
        return current;
    }

    private static PdfDictionary ParentOf(PdfDictionary root, IReadOnlyList<int> path) =>
        path.Count <= 1 ? root : Find(root, path.Take(path.Count - 1).ToList());

    /// <summary>Refaz a corrente de filhos: /First, /Last, /Prev, /Next e /Parent.</summary>
    private static void Relink(PdfDictionary parent, List<PdfDictionary> children, PdfDictionary root)
    {
        if (children.Count == 0)
        {
            parent.Elements.Remove("/First");
            parent.Elements.Remove("/Last");
            parent.Elements.Remove("/Count");
            return;
        }
        parent.Elements["/First"] = children[0].Reference;
        parent.Elements["/Last"] = children[^1].Reference;
        for (int i = 0; i < children.Count; i++)
        {
            var c = children[i];
            c.Elements["/Parent"] = parent.Reference;
            if (i > 0) c.Elements["/Prev"] = children[i - 1].Reference; else c.Elements.Remove("/Prev");
            if (i < children.Count - 1) c.Elements["/Next"] = children[i + 1].Reference; else c.Elements.Remove("/Next");
        }
    }

    private static bool IsOpen(PdfDictionary item) => item.Elements.GetInteger("/Count") > 0;

    private static void SetOpen(PdfDictionary item) => item.Elements.SetInteger("/Count", 1); // o valor certo vem no Recount

    /// <summary>
    /// Recalcula /Count (norma 12.3.3): item aberto = quantos descendentes aparecem; fechado = o mesmo número,
    /// negativo; a raiz = total visível.
    /// </summary>
    private static void Recount(PdfDictionary root)
    {
        int Visible(PdfDictionary parent, int depth)
        {
            int n = 0;
            foreach (var c in Children(parent))
            {
                bool open = IsOpen(c);
                int below = depth < MaxDepth ? Visible(c, depth + 1) : 0;
                if (below == 0) c.Elements.Remove("/Count");
                else c.Elements.SetInteger("/Count", open ? below : -below);
                n += 1 + (open ? below : 0);
            }
            return n;
        }
        root.Elements.SetInteger("/Count", Visible(root, 0));
    }

    // ---------------- Destino ----------------

    private static PdfArray NewDestination(PdfDocument doc, int page, double? top)
    {
        var p = doc.Pages[PdfPageContent.CheckPage(doc, page)];
        var geometry = PageGeometry.Of(p);
        if (top is { } t && p.Rotate % 360 == 0)
        {
            double y = geometry.ViewToPdf(0, Math.Max(0, t)).Y;
            return new PdfArray(doc, p.Reference!, new PdfName("/XYZ"), PdfNull.Value, new PdfReal(y), PdfNull.Value);
        }
        return new PdfArray(doc, p.Reference!, new PdfName("/Fit"));
    }

    /// <summary>Página (0…) e altura na tela para onde o marcador leva: /Dest, ação /GoTo ou destino com nome.</summary>
    private static (int? Page, double? Top) Destination(PdfDocument doc, PdfDictionary item)
    {
        var dest = item.Elements["/Dest"];
        if (dest is null && item.Elements.GetDictionary("/A") is { } action && action.Elements.GetName("/S") == "/GoTo")
            dest = action.Elements["/D"];
        var array = Resolve(doc, dest, 0);
        if (array is null || array.Elements.Count < 2) return (null, null);

        int page = -1;
        if (array.Elements[0] is PdfReference reference)
        {
            for (int i = 0; i < doc.PageCount; i++)
                if (ReferenceEquals(doc.Pages[i].Reference, reference) || doc.Pages[i].Reference?.ObjectID == reference.ObjectID) { page = i; break; }
        }
        if (page < 0) return (null, null);

        var pdfPage = doc.Pages[page];
        if (pdfPage.Rotate % 360 != 0) return (page, null);
        string kind = array.Elements.GetName(1);
        int topIndex = kind switch { "/XYZ" => 3, "/FitH" or "/FitBH" => 2, _ => -1 };
        if (topIndex < 0 || topIndex >= array.Elements.Count || array.Elements[topIndex] is not (PdfReal or PdfInteger)) return (page, null);
        double y = array.Elements.GetReal(topIndex);
        return (page, Math.Max(0, PageGeometry.Of(pdfPage).PdfToView(0, y).Y));
    }

    /// <summary>Transforma o destino (array, nome, texto ou {/D …}) no array [página /Tipo …].</summary>
    private static PdfArray? Resolve(PdfDocument doc, PdfItem? dest, int depth)
    {
        if (depth > 4) return null;
        if (dest is PdfReference r) dest = r.Value;
        switch (dest)
        {
            case PdfArray array: return array;
            case PdfDictionary dict: return Resolve(doc, dict.Elements["/D"], depth + 1);
            case PdfName name:
                return Resolve(doc, doc.Internals.Catalog.Elements.GetDictionary("/Dests")?.Elements[name.Value], depth + 1);
            case PdfString text:
                var tree = doc.Internals.Catalog.Elements.GetDictionary("/Names")?.Elements.GetDictionary("/Dests");
                return tree is null ? null : Resolve(doc, LookupName(tree, text.Value, 0), depth + 1);
            default: return null;
        }
    }

    private static PdfItem? LookupName(PdfDictionary node, string key, int depth)
    {
        if (depth > 32) return null;
        if (node.Elements.GetArray("/Names") is { } names)
            for (int i = 0; i + 1 < names.Elements.Count; i += 2)
                if (names.Elements[i] is PdfString s && s.Value == key) return names.Elements[i + 1];
        if (node.Elements.GetArray("/Kids") is { } kids)
            for (int i = 0; i < kids.Elements.Count; i++)
                if (kids.Elements.GetDictionary(i) is { } kid && LookupName(kid, key, depth + 1) is { } found) return found;
        return null;
    }

    private static string CheckTitle(string title)
    {
        title = title.Trim();
        if (title.Length == 0) throw new ArgumentException("Dê um nome ao marcador.");
        return title;
    }

    private static ArgumentException Missing() => new("Este marcador não existe mais (o arquivo mudou). Atualize a lista.");
}
