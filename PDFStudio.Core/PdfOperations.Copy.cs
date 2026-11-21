using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

// Copiar páginas: duplicar páginas do documento, idênticas (texto, fontes, imagens, comentários, links e campos),
// e colocar as cópias onde o usuário escolher.
public static partial class PdfOperations
{
    /// <summary>Máximo de cópias de uma vez.</summary>
    public const int MaxPageCopies = 50;

    /// <summary>
    /// Copia as páginas de <paramref name="ranges"/> (na ordem digitada) <paramref name="copies"/> vezes e coloca as
    /// cópias em <paramref name="position"/> (quantas páginas ficam antes delas: 0 = início; total = fim). O resto do
    /// documento (marcadores, formulário, informações) fica como está. Campos de formulário copiados ganham nome
    /// próprio ("Nome (cópia 2)"), para não ficarem ligados ao original. Retorna o total de páginas do resultado.
    /// </summary>
    public static int CopyPages(string input, string ranges, int copies, int position, string output)
    {
        if (copies is < 1 or > MaxPageCopies)
            throw new ArgumentOutOfRangeException(nameof(copies), $"Escolha de 1 a {MaxPageCopies} cópias.");
        byte[] bytes = File.ReadAllBytes(input); // a saída pode ser o próprio arquivo de entrada
        PdfDocument? baseSource = null;
        PdfDocument doc;
        try
        {
            doc = PdfLoad.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            // PDF com restrições de alteração: monta um documento novo com as páginas, como "Inserir páginas"
            // (o formulário do documento não vem junto).
            baseSource = OpenImport(bytes, input);
            doc = new PdfDocument();
            CopyInfo(baseSource, doc);
            for (int i = 0; i < baseSource.PageCount; i++) doc.AddPage(baseSource.Pages[i]);
        }
        using var _doc = doc;
        using var _base = baseSource;
        CheckPosition(position, doc.PageCount);
        var pages = PageRanges.Parse(ranges, doc.PageCount).SelectMany(g => g).ToList();
        if (pages.Count * copies > 2000) throw new ArgumentOutOfRangeException(nameof(copies), "Cópias demais de uma vez (máximo de 2000 páginas).");

        var names = PdfSignatureFields.FieldNames(doc);
        var newFields = new List<PdfDictionary>();
        var linkFixes = new List<(PdfPage Page, int Annot, PdfArray Dest, int Target)>();
        int at = position;
        for (int c = 0; c < copies; c++)
        {
            // Cada rodada lê o arquivo de novo: assim cada cópia é independente (nada fica compartilhado).
            using var source = OpenImport(bytes, input);
            foreach (int index in pages)
            {
                var original = source.Pages[index];
                var links = PrepareForCopy(original, source, names, newFields);
                var copy = doc.Pages.Insert(at, original);
                var copied = Annotations(copy).ToList();
                foreach (var (annot, target) in links)
                    if (annot < copied.Count && copied[annot].Item2 is { } dest) linkFixes.Add((copy, annot, dest, target));
                at++;
            }
        }

        // Links internos: apontam para a página certa do documento (já contando as cópias inseridas).
        foreach (var (_, _, dest, target) in linkFixes)
        {
            int shifted = target < position ? target : target + pages.Count * copies;
            dest.Elements[0] = doc.Pages[shifted].Reference!;
        }

        // Campos copiados entram no formulário do documento (com nome novo). No documento remontado não há formulário.
        if (newFields.Count > 0 && baseSource is null)
        {
            var acroForm = doc.Internals.Catalog.Elements.GetDictionary("/AcroForm");
            if (acroForm is null)
            {
                acroForm = new PdfDictionary(doc);
                doc.Internals.AddObject(acroForm);
                doc.Internals.Catalog.Elements["/AcroForm"] = acroForm.Reference;
            }
            var fields = acroForm.Elements.GetArray("/Fields");
            if (fields is null)
            {
                fields = new PdfArray(doc);
                acroForm.Elements["/Fields"] = fields;
            }
            foreach (var field in newFields)
                if (FindImported(doc, field) is { } imported) fields.Elements.Add(imported.Reference!);
        }

        int total = doc.PageCount;
        SaveSafely(doc, output);
        return total;
    }

    private static PdfDocument OpenImport(byte[] bytes, string path)
    {
        try
        {
            return PdfLoad.Open(new MemoryStream(bytes), PdfDocumentOpenMode.Import);
        }
        catch (PdfReaderException ex) when (ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"\"{Path.GetFileName(path)}\" está protegido por senha.", ex);
        }
    }

    /// <summary>
    /// Arruma a página (na cópia em memória do arquivo, antes de importar) para que a importação traga só ela:
    /// links para outras páginas ficam anotados e são religados depois; campos viram campos próprios, com nome novo;
    /// marcações de estrutura e "artigos" (que puxariam o documento inteiro) saem. Retorna os links internos
    /// (posição do comentário na lista, página de destino).
    /// </summary>
    private static List<(int Annot, int Target)> PrepareForCopy(PdfPage page, PdfDocument source,
        HashSet<string> names, List<PdfDictionary> newFields)
    {
        page.Elements.Remove("/StructParents");
        page.Elements.Remove("/B");
        page.Elements.Remove("/Thumb");
        var pageNumbers = new Dictionary<PdfDictionary, int>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < source.PageCount; i++) pageNumbers[source.Pages[i]] = i;

        var links = new List<(int, int)>();
        var renamed = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        int n = 0;
        foreach (var (annot, dest) in Annotations(page))
        {
            annot.Elements.Remove("/StructParent");
            if (dest is not null && dest.Elements.Count > 0)
            {
                var target = dest.Elements[0] is PdfReference r ? r.Value as PdfDictionary : dest.Elements[0] as PdfDictionary;
                if (target is not null && pageNumbers.TryGetValue(target, out int number))
                {
                    links.Add((n, number));
                    dest.Elements[0] = new PdfInteger(0); // provisório: senão a importação traria a outra página junto
                }
            }
            if (annot.Elements.GetName("/Subtype") == "/Widget") DetachField(annot, page, names, newFields, renamed);
            n++;
        }
        return links;
    }

    /// <summary>Os comentários da página, com o destino (array) se for um link interno direto.</summary>
    private static IEnumerable<(PdfDictionary, PdfArray?)> Annotations(PdfPage page)
    {
        if (page.Elements.GetArray("/Annots") is not { } annots) yield break;
        for (int i = 0; i < annots.Elements.Count; i++)
        {
            if (annots.Elements.GetDictionary(i) is not { } annot) continue;
            var dest = annot.Elements.GetArray("/Dest");
            if (dest is null && annot.Elements.GetDictionary("/A") is { } action && action.Elements.GetName("/S") == "/GoTo")
                dest = action.Elements.GetArray("/D");
            yield return (annot, dest);
        }
    }

    /// <summary>
    /// Torna o campo deste "desenho" (widget) um campo independente: herda o que vinha dos "pais", fica só com os
    /// desenhos desta página, ganha nome novo e entra na lista de campos. Assinaturas viram campos vazios.
    /// </summary>
    private static void DetachField(PdfDictionary widget, PdfPage page, HashSet<string> names,
        List<PdfDictionary> newFields, HashSet<PdfDictionary> renamed)
    {
        var field = widget.Elements.ContainsKey("/T") ? widget : widget.Elements.GetDictionary("/Parent");
        if (field is null || !renamed.Add(field)) return;

        // Nome completo e chaves herdadas dos pais.
        var parts = new List<string>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (var f = field; f is not null && seen.Add(f) && parts.Count < 32; f = f.Elements.GetDictionary("/Parent"))
        {
            if (f.Elements.GetString("/T") is { Length: > 0 } t) parts.Insert(0, t);
            if (!ReferenceEquals(f, field))
                foreach (var key in new[] { "/FT", "/Ff", "/V", "/DV", "/DA", "/Q", "/Opt", "/MaxLen" })
                    if (!field.Elements.ContainsKey(key) && f.Elements[key] is { } value) field.Elements[key] = value;
        }
        field.Elements.Remove("/Parent");

        // Só os desenhos desta página continuam ligados ao campo copiado.
        if (!ReferenceEquals(field, widget) && field.Elements.GetArray("/Kids") is { } kids)
            for (int i = kids.Elements.Count - 1; i >= 0; i--)
                if (kids.Elements.GetDictionary(i) is not { } kid || !IsOnPage(kid, page)) kids.Elements.RemoveAt(i);

        if (field.Elements.GetName("/FT") == "/Sig")
        {
            field.Elements.Remove("/V"); // a assinatura não é copiada: fica um campo para assinar de novo
            field.Elements.Remove("/Lock");
        }

        string baseName = string.Join(".", parts).Replace('.', '-');
        if (baseName.Length == 0) baseName = "Campo";
        string name;
        int copy = 2;
        do name = $"{baseName} (cópia {copy++})"; while (!names.Add(name));
        field.Elements.SetString("/T", name);
        newFields.Add(field);
    }

    private static bool IsOnPage(PdfDictionary widget, PdfPage page)
    {
        if (widget.Elements.GetDictionary("/P") is { } p) return ReferenceEquals(p, page);
        return page.Elements.GetArray("/Annots") is { } annots
            && Enumerable.Range(0, annots.Elements.Count).Any(i => ReferenceEquals(annots.Elements.GetDictionary(i), widget));
    }

    /// <summary>O objeto do documento que veio da importação de <paramref name="field"/> (procura nas páginas).</summary>
    private static PdfDictionary? FindImported(PdfDocument doc, PdfDictionary field)
    {
        foreach (var page in doc.Pages)
        {
            if (page.Elements.GetArray("/Annots") is not { } annots) continue;
            for (int i = 0; i < annots.Elements.Count; i++)
            {
                if (annots.Elements.GetDictionary(i) is not { } annot) continue;
                var candidate = annot.Elements.ContainsKey("/T") ? annot : annot.Elements.GetDictionary("/Parent");
                if (candidate is not null && candidate.Elements.GetString("/T") == field.Elements.GetString("/T")) return candidate;
            }
        }
        return null;
    }
}
