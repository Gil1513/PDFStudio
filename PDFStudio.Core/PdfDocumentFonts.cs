using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;
using PdfSharp.Pdf.IO;
using Pig = UglyToad.PdfPig;

namespace PDFStudio.Core;

/// <summary>
/// Uma fonte usada no documento. <paramref name="PdfName"/>: nome gravado no PDF ("Helvetica-Bold").
/// <paramref name="Embedded"/>: o PDF leva a fonte junto (senão cada leitor desenha com a que tiver).
/// <paramref name="Letters"/>: quantas letras usam essa fonte. <paramref name="Choice"/>: o que o PDF Studio
/// usaria para escrever com ela ao editar.
/// </summary>
public sealed record DocumentFont(string PdfName, string Family, bool Bold, bool Italic, bool Embedded, int Letters, FontChoice Choice)
{
    /// <summary>O texto editado sai com a própria fonte (não uma substituta).</summary>
    public bool IsExact => Choice.Source == FontSource.Original;
}

/// <summary>Etapa 5.10 (complemento): quais fontes o documento usa e se o PDF Studio tem cada uma.</summary>
public static class PdfDocumentFonts
{
    /// <summary>As fontes do documento, das mais usadas para as menos usadas.</summary>
    public static List<DocumentFont> List(string path, string? password = null, Func<string, bool>? isAvailable = null)
    {
        var letters = new Dictionary<string, int>(StringComparer.Ordinal);
        var options = new Pig.ParsingOptions { Password = password ?? "" };
        using (var pig = Pig.PdfDocument.Open(path, options))
            foreach (var page in pig.GetPages())
                foreach (var letter in page.Letters)
                {
                    if (string.IsNullOrWhiteSpace(letter.Value)) continue;
                    string name = StripSubset(letter.FontName ?? "");
                    if (name.Length > 0) letters[name] = letters.GetValueOrDefault(name) + 1;
                }

        var embedded = EmbeddedFonts(path, password);
        return letters
            .OrderByDescending(l => l.Value)
            .Select(l =>
            {
                var (family, bold, italic) = PdfContentBlocks.ParseFontName(l.Key);
                return new DocumentFont(l.Key, family, bold, italic, embedded.Contains(l.Key), l.Value,
                    FontLibrary.Resolve(family, isAvailable));
            })
            .ToList();
    }

    /// <summary>"ABCDEF+Arial-BoldMT" → "Arial-BoldMT" (o prefixo marca fonte embutida só com as letras usadas).</summary>
    public static string StripSubset(string name) =>
        name.Length > 7 && name[6] == '+' && name[..6].All(char.IsAsciiLetterUpper) ? name[7..] : name;

    /// <summary>Nomes (sem o prefixo de subconjunto) das fontes que o PDF leva junto, nas páginas e nos formulários XObject.</summary>
    private static HashSet<string> EmbeddedFonts(string path, string? password)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        using var doc = string.IsNullOrEmpty(password)
            ? PdfReader.Open(path, PdfDocumentOpenMode.Import)
            : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        foreach (var page in doc.Pages) Scan(page.Elements.GetDictionary("/Resources"), result, seen);
        return result;
    }

    private static void Scan(PdfDictionary? resources, HashSet<string> result, HashSet<PdfDictionary> seen)
    {
        if (resources is null || !seen.Add(resources)) return;
        if (resources.Elements.GetDictionary("/Font") is { } fonts)
            foreach (var key in fonts.Elements.Keys)
                if (fonts.Elements.GetDictionary(key) is { } font && IsEmbedded(font))
                    result.Add(StripSubset(font.Elements.GetName("/BaseFont").TrimStart('/')));
        if (resources.Elements.GetDictionary("/XObject") is { } xobjects)
            foreach (var key in xobjects.Elements.Keys)
                if (xobjects.Elements.GetDictionary(key) is { } x && x.Elements.GetName("/Subtype") == "/Form")
                    Scan(x.Elements.GetDictionary("/Resources"), result, seen);
    }

    private static bool IsEmbedded(PdfDictionary font)
    {
        var descriptor = font.Elements.GetDictionary("/FontDescriptor");
        // Fontes compostas (Type0) guardam o descritor na fonte "descendente".
        if (descriptor is null && font.Elements.GetArray("/DescendantFonts") is { Elements.Count: > 0 } descendants)
        {
            var item = descendants.Elements[0];
            var child = item as PdfDictionary ?? (item as PdfReference)?.Value as PdfDictionary;
            descriptor = child?.Elements.GetDictionary("/FontDescriptor");
        }
        return descriptor is not null && (descriptor.Elements.ContainsKey("/FontFile")
            || descriptor.Elements.ContainsKey("/FontFile2") || descriptor.Elements.ContainsKey("/FontFile3"));
    }
}
