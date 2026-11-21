using System.Security.Cryptography;
using System.Text;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>O que o documento recebeu depois de uma assinatura (gravações "no fim do arquivo").</summary>
[Flags]
public enum SignatureChanges
{
    None = 0,
    /// <summary>Outra assinatura (ou um campo de assinatura vazio).</summary>
    Signatures = 1,
    /// <summary>Campos de formulário preenchidos.</summary>
    FormFill = 2,
    /// <summary>Comentários (notas, marcações, desenhos) acrescentados, mudados ou apagados.</summary>
    Comments = 4,
    /// <summary>Algo que não é permitido mudou: texto, imagens, páginas… A assinatura não vale para o que se vê.</summary>
    NotAllowed = 8,
    /// <summary>Não deu para conferir o que mudou.</summary>
    Unknown = 16,
}

/// <summary>
/// Compara a versão assinada (os bytes cobertos pela assinatura) com o arquivo atual, objeto por objeto, e diz
/// que tipo de alteração veio depois. Comentários e outras assinaturas são permitidos; mudar o conteúdo das
/// páginas não é (é o "ataque" de esconder um texto novo numa gravação posterior).
/// </summary>
internal static class SignatureRevisions
{
    private static readonly HashSet<string> CatalogKeys = ["/AcroForm", "/DSS"];
    private static readonly HashSet<string> AcroFormKeys = ["/Fields", "/SigFlags", "/DR", "/DA", "/NeedAppearances"];
    private static readonly HashSet<string> FillKeys = ["/V", "/AS", "/AP", "/MK"];
    private static readonly HashSet<string> SigningKeys = ["/V", "/AP", "/AS", "/F", "/MK", "/Lock", "/SV"];

    public static SignatureChanges After(byte[] bytes, long signedEnd, PdfDocument current, int? certificationLevel)
    {
        if (signedEnd >= bytes.Length) return SignatureChanges.None;
        // A versão assinada tem de terminar exatamente no fim de uma gravação (%%EOF).
        int e = (int)signedEnd;
        while (e > 0 && bytes[e - 1] is (byte)'\n' or (byte)'\r' or (byte)' ' or (byte)'\t' or 0) e--;
        if (e < 5 || Encoding.ASCII.GetString(bytes, e - 5, 5) != "%%EOF") return SignatureChanges.Unknown;

        PdfDocument old;
        try { old = PdfReader.Open(new MemoryStream(bytes, 0, (int)signedEnd, writable: false), PdfDocumentOpenMode.Import); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { return SignatureChanges.Unknown; }
        using (old)
        {
            SignatureChanges changes;
            try { changes = Classify(old, current); }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or InvalidCastException) { return SignatureChanges.Unknown; }

            // Assinatura de certificação: 1 = nada pode mudar; 2 = formulários e assinaturas; 3 = também comentários.
            if (certificationLevel == 1 && changes != SignatureChanges.None) changes |= SignatureChanges.NotAllowed;
            if (certificationLevel == 2 && changes.HasFlag(SignatureChanges.Comments)) changes |= SignatureChanges.NotAllowed;
            return changes;
        }
    }

    private static SignatureChanges Classify(PdfDocument old, PdfDocument now)
    {
        var before = Map(old);
        var after = Map(now);

        int catalog = PdfSyntax.Num(old.Internals.Catalog);
        int? acroForm = (old.Internals.Catalog.Elements["/AcroForm"] as PdfReference)?.ObjectNumber;
        int? info = old.Info.Reference?.ObjectNumber;
        int? metadata = (old.Internals.Catalog.Elements["/Metadata"] as PdfReference)?.ObjectNumber;

        // Listas que podem ganhar itens: /Annots das páginas, /Fields do formulário e /Kids dos campos.
        var growingArrays = new HashSet<int>();
        // Aparências (/AP) de comentários (false) e de campos (true).
        var appearances = new Dictionary<int, bool>();
        foreach (var (_, obj) in before)
        {
            if (obj is not PdfDictionary d) continue;
            if (IsPage(d) && d.Elements["/Annots"] is PdfReference annots) growingArrays.Add(annots.ObjectNumber);
            if (d.Elements["/Kids"] is PdfReference kids && d.Elements.ContainsKey("/FT")) growingArrays.Add(kids.ObjectNumber);
            if (IsAnnot(d) && d.Elements["/AP"] is { } ap) CollectRefs(ap, IsWidget(d), appearances, 0);
        }
        if (acroForm is { } af && before.TryGetValue(af, out var form) && form is PdfDictionary formDict
            && formDict.Elements["/Fields"] is PdfReference fieldsRef)
            growingArrays.Add(fieldsRef.ObjectNumber);
        else if (old.Internals.Catalog.Elements["/AcroForm"] is PdfDictionary directForm && directForm.Elements["/Fields"] is PdfReference directFields)
            growingArrays.Add(directFields.ObjectNumber);

        var result = SignatureChanges.None;
        foreach (var (number, obj) in after)
        {
            if (!before.TryGetValue(number, out var was))
            {
                result |= Added(obj);
                continue;
            }
            if (PdfSyntax.Value(was).AsSpan().SequenceEqual(PdfSyntax.Value(obj))) continue;
            result |= Modified(number, was, obj, before, catalog, acroForm, info, metadata, growingArrays, appearances);
        }
        foreach (var (number, was) in before)
            if (!after.ContainsKey(number)) result |= Removed(was, number, appearances);
        return result;
    }

    private static SignatureChanges Added(PdfObject obj)
    {
        if (obj is not PdfDictionary d) return SignatureChanges.None;
        string? type = d.Elements.GetName("/Type");
        if (type is "/XRef" or "/ObjStm") return SignatureChanges.None;
        if (type is "/Sig" or "/DocTimeStamp") return SignatureChanges.Signatures;
        if (IsPage(d) || type == "/Pages") return SignatureChanges.NotAllowed; // página nova
        if (IsAnnot(d))
        {
            if (!IsWidget(d)) return SignatureChanges.Comments;
            return FieldType(d) == "/Sig" ? SignatureChanges.Signatures : SignatureChanges.NotAllowed; // campo novo de formulário
        }
        if (d.Elements.ContainsKey("/FT") && !d.Elements.ContainsKey("/Subtype"))
            return d.Elements.GetName("/FT") == "/Sig" ? SignatureChanges.Signatures : SignatureChanges.NotAllowed;
        return SignatureChanges.None; // aparência, fonte… só conta se algo permitido apontar para ela
    }

    private static SignatureChanges Modified(int number, PdfObject was, PdfObject now, Dictionary<int, PdfObject> before,
        int catalog, int? acroForm, int? info, int? metadata, HashSet<int> growingArrays, Dictionary<int, bool> appearances)
    {
        if (number == info || number == metadata) return SignatureChanges.None; // dados do documento (autor, datas)
        if (appearances.TryGetValue(number, out bool widget))
            return widget ? SignatureChanges.FormFill : SignatureChanges.Comments;

        if (was is PdfArray oldArray && now is PdfArray newArray)
            return growingArrays.Contains(number) ? RemovedItems(Refs(oldArray), Refs(newArray), before) : SignatureChanges.NotAllowed;
        if (was is not PdfDictionary a || now is not PdfDictionary b) return SignatureChanges.NotAllowed;
        if (a.Elements.GetName("/Type") is "/XRef" or "/ObjStm") return SignatureChanges.None;

        var keys = DifferentKeys(a, b);
        if (number == catalog)
        {
            if (!keys.All(CatalogKeys.Contains)) return SignatureChanges.NotAllowed;
            if (keys.Contains("/AcroForm") && a.Elements["/AcroForm"] is PdfDictionary fa && b.Elements["/AcroForm"] is PdfDictionary fb)
                return FormChanges(fa, fb, before);
            return SignatureChanges.None;
        }
        if (number == acroForm) return FormChanges(a, b, before);
        if (IsPage(a))
        {
            if (!keys.All(k => k == "/Annots")) return SignatureChanges.NotAllowed;
            return b.Elements["/Annots"] is PdfReference ? SignatureChanges.None
                : RemovedItems(Refs(a.Elements["/Annots"], before), Refs(b.Elements["/Annots"], before), before);
        }

        bool isField = a.Elements.ContainsKey("/FT") || a.Elements["/Parent"] is PdfReference;
        if (IsAnnot(a) && !IsWidget(a)) return SignatureChanges.Comments;
        if (IsWidget(a) || isField)
        {
            if (FieldType(a) == "/Sig")
            {
                // Assinar um campo vazio: ele ganha o /V (e a aparência). Trocar uma assinatura que já existia não vale.
                bool wasEmpty = Value(a, "/V") is null;
                return wasEmpty && keys.All(SigningKeys.Contains) ? SignatureChanges.Signatures : SignatureChanges.NotAllowed;
            }
            return keys.All(FillKeys.Contains) ? SignatureChanges.FormFill : SignatureChanges.NotAllowed;
        }
        return SignatureChanges.NotAllowed; // conteúdo de página, fonte, imagem…
    }

    private static SignatureChanges FormChanges(PdfDictionary a, PdfDictionary b, Dictionary<int, PdfObject> before)
    {
        var keys = DifferentKeys(a, b);
        if (!keys.All(AcroFormKeys.Contains)) return SignatureChanges.NotAllowed;
        if (keys.Contains("/Fields") && a.Elements["/Fields"] is not PdfReference)
        {
            var removed = Refs(a.Elements["/Fields"], before).Except(Refs(b.Elements["/Fields"], before));
            if (removed.Any()) return SignatureChanges.NotAllowed; // campo tirado do formulário
        }
        return SignatureChanges.None;
    }

    /// <summary>Itens tirados de uma lista: comentários apagados são permitidos; campos e outras coisas, não.</summary>
    private static SignatureChanges RemovedItems(List<int> before, List<int> after, Dictionary<int, PdfObject> objects)
    {
        var result = SignatureChanges.None;
        foreach (int gone in before.Except(after))
        {
            if (objects.TryGetValue(gone, out var obj) && obj is PdfDictionary d && IsAnnot(d) && !IsWidget(d))
                result |= SignatureChanges.Comments;
            else return SignatureChanges.NotAllowed;
        }
        return result;
    }

    private static SignatureChanges Removed(PdfObject was, int number, Dictionary<int, bool> appearances)
    {
        if (appearances.TryGetValue(number, out bool widget)) return widget ? SignatureChanges.FormFill : SignatureChanges.Comments;
        if (was is PdfDictionary d && IsAnnot(d) && !IsWidget(d)) return SignatureChanges.Comments;
        return SignatureChanges.NotAllowed;
    }

    // ---------------- Auxiliares ----------------

    private static Dictionary<int, PdfObject> Map(PdfDocument doc)
    {
        var map = new Dictionary<int, PdfObject>();
        foreach (var obj in doc.Internals.GetAllObjects())
            if (obj.Reference is { } r) map[r.ObjectNumber] = obj;
        return map;
    }

    private static HashSet<string> DifferentKeys(PdfDictionary a, PdfDictionary b)
    {
        var keys = new HashSet<string>(a.Elements.Keys.Concat(b.Elements.Keys));
        keys.RemoveWhere(k => PdfSyntax.Value(a.Elements[k]).AsSpan().SequenceEqual(PdfSyntax.Value(b.Elements[k])));
        if (a.Stream is not null || b.Stream is not null)
        {
            keys.Remove("/Length");
            if (!SHA256.HashData(a.Stream?.Value ?? []).AsSpan().SequenceEqual(SHA256.HashData(b.Stream?.Value ?? [])))
                keys.Add("(stream)");
        }
        return keys;
    }

    private static PdfItem? Value(PdfDictionary d, string key) => d.Elements[key] is PdfNull ? null : d.Elements[key];

    private static List<int> Refs(PdfArray array) =>
        array.Elements.OfType<PdfReference>().Select(r => r.ObjectNumber).ToList();

    private static List<int> Refs(PdfItem? item, Dictionary<int, PdfObject> objects) => item switch
    {
        PdfArray a => Refs(a),
        PdfReference r when objects.TryGetValue(r.ObjectNumber, out var o) && o is PdfArray a => Refs(a),
        _ => [],
    };

    private static void CollectRefs(PdfItem item, bool widget, Dictionary<int, bool> into, int depth)
    {
        if (depth > 4) return;
        switch (item)
        {
            case PdfReference r:
                into[r.ObjectNumber] = widget;
                break;
            case PdfDictionary d:
                foreach (var key in d.Elements.Keys) if (d.Elements[key] is { } v) CollectRefs(v, widget, into, depth + 1);
                break;
        }
    }

    private static bool IsPage(PdfDictionary d) => d.Elements.GetName("/Type") == "/Page";

    private static bool IsAnnot(PdfDictionary d) =>
        d.Elements.GetName("/Type") == "/Annot" || (d.Elements.ContainsKey("/Subtype") && d.Elements.ContainsKey("/Rect")
            && d.Elements.GetName("/Type") is "" or null);

    private static bool IsWidget(PdfDictionary d) => d.Elements.GetName("/Subtype") == "/Widget";

    /// <summary>Tipo do campo (/FT), procurando também nos "pais" (até 10 níveis).</summary>
    private static string? FieldType(PdfDictionary d)
    {
        for (int i = 0; i < 10 && d is not null; i++)
        {
            if (d.Elements.GetName("/FT") is { Length: > 0 } ft) return ft;
            d = d.Elements.GetDictionary("/Parent")!;
        }
        return null;
    }
}
