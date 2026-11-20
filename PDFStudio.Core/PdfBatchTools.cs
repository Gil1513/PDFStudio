using System.Globalization;
using System.Text;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.Advanced;

namespace PDFStudio.Core;

// Etapa 8.4: funções novas, feitas para o lote: numeração Bates, excluir páginas em branco, dados de formulário
// para CSV e nomes a partir do conteúdo.

/// <summary>Onde fica o número Bates na página.</summary>
public enum BatesPosition { BottomRight, BottomCenter, BottomLeft, TopRight, TopCenter, TopLeft }

/// <summary>
/// Numeração Bates: <paramref name="Prefix"/> + número com <paramref name="Digits"/> algarismos + <paramref name="Suffix"/>
/// (ex.: "ABC000001"). <paramref name="MarginPt"/>: distância da borda, em pontos.
/// </summary>
public sealed record BatesOptions(
    string Prefix = "", int Digits = 6, string Suffix = "", BatesPosition Position = BatesPosition.BottomRight,
    string FontFamily = "Arial", double FontSize = 10, AnnotationColor Color = default, double MarginPt = 28);

/// <summary>
/// Numeração Bates (usada em processos e auditorias): cada página de um conjunto de documentos recebe um número único,
/// que continua de um arquivo para o outro. Fica no conteúdo da página marcada como artefato do PDF Studio (dá para
/// trocar depois sem acumular) e não mexe em cabeçalho/rodapé.
/// </summary>
public static class PdfBates
{
    public const int MaxDigits = 12;

    /// <summary>"ABC" + 1 com 6 algarismos → "ABC000001".</summary>
    public static string Label(BatesOptions options, long number) =>
        options.Prefix + number.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Clamp(options.Digits, 1, MaxDigits), '0') + options.Suffix;

    /// <summary>Numera todas as páginas a partir de <paramref name="first"/>. Devolve quantas páginas foram numeradas.</summary>
    public static int Add(string input, BatesOptions options, long first, string output)
    {
        if (first < 0) throw new ArgumentOutOfRangeException(nameof(first), "O número inicial não pode ser negativo.");
        if (options.Digits is < 1 or > MaxDigits) throw new ArgumentOutOfRangeException(nameof(options), $"Use de 1 a {MaxDigits} algarismos.");
        var font = PdfPageContent.CreateFont(new PageTextStyle(options.FontFamily, options.FontSize, options.Color));
        var brush = new XSolidBrush(XColor.FromArgb(options.Color.R, options.Color.G, options.Color.B));
        int count = 0;
        PdfAnnotations.Edit(input, output, doc =>
        {
            for (int i = 0; i < doc.PageCount; i++)
            {
                var page = doc.Pages[i];
                PdfPageContent.RemoveArtifacts(page, PdfPageContent.ArtifactKind.Bates);
                string text = Label(options, first + i);
                var geometry = PageGeometry.Of(page);
                double w = geometry.ViewWidth, h = geometry.ViewHeight, m = options.MarginPt, lineHeight = font.GetHeight();
                bool top = options.Position is BatesPosition.TopLeft or BatesPosition.TopCenter or BatesPosition.TopRight;
                var row = new XRect(m, top ? m : h - m - lineHeight, Math.Max(1, w - 2 * m), lineHeight);
                var format = options.Position switch
                {
                    BatesPosition.BottomLeft or BatesPosition.TopLeft => XStringFormats.TopLeft,
                    BatesPosition.BottomCenter or BatesPosition.TopCenter => XStringFormats.TopCenter,
                    _ => XStringFormats.TopRight,
                };
                PdfPageContent.DrawInView(page, gfx => gfx.DrawString(text, font, brush, row, format), artifact: PdfPageContent.ArtifactKind.Bates);
                count++;
            }
        });
        return count;
    }

    /// <summary>Quantas páginas têm número Bates colocado pelo PDF Studio.</summary>
    public static int Count(string path)
    {
        using var doc = PdfSharp.Pdf.IO.PdfReader.Open(new MemoryStream(File.ReadAllBytes(path)), PdfSharp.Pdf.IO.PdfDocumentOpenMode.Import);
        return doc.Pages.Cast<PdfPage>().Count(p => PdfPageContent.CountArtifacts(p, PdfPageContent.ArtifactKind.Bates) > 0);
    }
}

/// <summary>
/// Páginas em branco: a conta é feita sobre a imagem da página (desenhada no app pelo motor do Windows), porque uma
/// página escaneada "em branco" tem sujeira e uma página com só um traço não é vazia. Aqui ficam as contas testáveis.
/// </summary>
public static class BlankPages
{
    /// <summary>Um ponto mais escuro que isso (0 = preto, 255 = branco) conta como tinta.</summary>
    public const int InkLevel = 160;

    /// <summary>Faixa da borda que não conta (sombra e bordas pretas do scanner), em fração do lado.</summary>
    public const double IgnoredBorder = 0.03;

    /// <summary>Tolerância padrão: até 0,1% da página com tinta ainda é "em branco" (sujeira, pontinhos).</summary>
    public const double DefaultTolerancePercent = 0.1;

    /// <summary>Fração da página (sem a borda) com tinta, de 0 a 1. <paramref name="bgra"/>: pixels BGRA, 4 bytes cada.</summary>
    public static double InkRatio(ReadOnlySpan<byte> bgra, int width, int height)
    {
        if (width <= 0 || height <= 0 || bgra.Length < width * height * 4) throw new ArgumentException("Imagem incompleta.", nameof(bgra));
        int bx = (int)(width * IgnoredBorder), by = (int)(height * IgnoredBorder);
        long ink = 0, total = 0;
        for (int y = by; y < height - by; y++)
        {
            int row = y * width * 4;
            for (int x = bx; x < width - bx; x++)
            {
                int i = row + x * 4;
                // Luminância (o mesmo peso que o olho dá a cada cor). Transparente conta como papel branco.
                int lum = bgra[i + 3] == 0 ? 255 : (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000;
                if (lum < InkLevel) ink++;
                total++;
            }
        }
        return total == 0 ? 0 : (double)ink / total;
    }

    /// <summary>A página está em branco com essa <paramref name="tolerancePercent"/> (porcentagem de tinta aceita)?</summary>
    public static bool IsBlank(double inkRatio, double tolerancePercent) => inkRatio * 100 <= tolerancePercent + 1e-9;

    /// <summary>Índices (de 0) → texto de páginas para excluir ("2, 5, 7").</summary>
    public static string Ranges(IEnumerable<int> indexes) => string.Join(", ", indexes.Order().Select(i => (i + 1).ToString(CultureInfo.InvariantCulture)));
}

/// <summary>Um campo de formulário preenchido: nome completo (ex.: "cliente.nome") e o valor como texto.</summary>
public sealed record FormFieldValue(string Name, string Value);

/// <summary>Dados de formulário (AcroForm) dos PDFs, para juntar numa planilha CSV (uma linha por arquivo).</summary>
public static class PdfFormData
{
    /// <summary>Os campos do formulário, na ordem do arquivo. Campos de assinatura e botões de ação ficam de fora.</summary>
    public static List<FormFieldValue> Read(string path)
    {
        using var doc = PdfOperations.OpenForImport(path);
        var result = new List<FormFieldValue>();
        if (doc.Internals.Catalog.Elements.GetDictionary("/AcroForm")?.Elements.GetArray("/Fields") is not { } fields) return result;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        for (int i = 0; i < fields.Elements.Count; i++)
            if (fields.Elements.GetDictionary(i) is { } field) Walk(field, "", null, result, seen);
        return result;
    }

    private static void Walk(PdfDictionary field, string parentName, string? parentType, List<FormFieldValue> result, HashSet<PdfDictionary> seen)
    {
        if (!seen.Add(field)) return; // PDF malfeito com laço
        string part = field.Elements.GetString("/T");
        string name = part.Length == 0 ? parentName : parentName.Length == 0 ? part : $"{parentName}.{part}";
        string? type = field.Elements.GetName("/FT") is { Length: > 0 } ft ? ft : parentType; // o tipo pode vir do "pai"
        // Filhos com nome próprio são outros campos; filhos sem /T são só os "desenhos" (widgets) deste campo.
        var kids = field.Elements.GetArray("/Kids");
        var namedKids = new List<PdfDictionary>();
        if (kids is not null)
            for (int i = 0; i < kids.Elements.Count; i++)
                if (kids.Elements.GetDictionary(i) is { } kid && kid.Elements.ContainsKey("/T")) namedKids.Add(kid);
        if (namedKids.Count > 0)
        {
            foreach (var kid in namedKids) Walk(kid, name, type, result, seen);
            return;
        }
        if (name.Length == 0 || type == "/Sig") return;
        if (type == "/Btn" && (field.Elements.GetInteger("/Ff") & (1 << 16)) != 0) return; // botão de ação (não guarda valor)
        result.Add(new FormFieldValue(name, ValueText(field.Elements.GetValue("/V"))));
    }

    private static string ValueText(PdfItem? value) => value switch
    {
        PdfReference r => ValueText(r.Value),
        PdfString s => s.Value,
        PdfName n => n.Value.TrimStart('/') is var v && v == "Off" ? "" : n.Value.TrimStart('/'), // caixa desmarcada = vazio
        PdfArray a => string.Join("; ", a.Elements.Select(ValueText)),
        PdfInteger i => i.Value.ToString(CultureInfo.InvariantCulture),
        PdfReal d => d.Value.ToString(CultureInfo.InvariantCulture),
        null => "",
        _ => value.ToString() ?? "",
    };

    /// <summary>
    /// Planilha CSV para o Excel em português: separador ";", UTF-8 com BOM (acentos certos), primeira coluna "Arquivo"
    /// e depois todos os campos que aparecem em algum arquivo (na ordem em que aparecem).
    /// </summary>
    public static string ToCsv(IReadOnlyList<(string File, IReadOnlyList<FormFieldValue> Fields)> rows)
    {
        var columns = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (_, fields) in rows)
            foreach (var f in fields)
                if (known.Add(f.Name)) columns.Add(f.Name);
        var sb = new StringBuilder();
        sb.AppendJoin(';', new[] { "Arquivo" }.Concat(columns).Select(Cell)).Append("\r\n");
        foreach (var (file, fields) in rows)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var f in fields) values.TryAdd(f.Name, f.Value);
            sb.AppendJoin(';', new[] { file }.Concat(columns.Select(c => values.GetValueOrDefault(c, ""))).Select(Cell)).Append("\r\n");
        }
        return sb.ToString();
    }

    /// <summary>Célula do CSV: entre aspas se tiver ";", aspas ou quebra de linha. Começando com =, +, - ou @ ganha um
    /// apóstrofo, para o Excel não tratar o valor como fórmula.</summary>
    public static string Cell(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@') value = "'" + value;
        return value.IndexOfAny([';', '"', '\n', '\r']) >= 0 ? "\"" + value.Replace("\"", "\"\"") + "\"" : value;
    }
}

/// <summary>O que dá para usar no novo nome do arquivo.</summary>
public sealed record RenameInfo(string FileName, string Title, string Author, DateTime? Date, string FirstLine);

/// <summary>
/// Nomes a partir do conteúdo: o modelo usa {titulo}, {autor}, {data} (data de criação do PDF, "2026-11-19"),
/// {linha} (primeira linha de texto da 1ª página), {nome} (nome atual) e {n} (posição na lista, 001, 002…).
/// </summary>
public static class PdfRename
{
    public const int MaxLength = 120;

    public static RenameInfo Info(string path)
    {
        var props = DocumentPropertiesService.Read(path);
        var words = PdfTextLayout.Words(path, 0);
        string line = words.Count == 0 ? "" : string.Join(" ", words.Where(w => w.Line == words[0].Line).Select(w => w.Text));
        return new RenameInfo(Path.GetFileNameWithoutExtension(path), props.Metadata.Title.Trim(), props.Metadata.Author.Trim(),
            props.Created ?? File.GetLastWriteTime(path), line.Trim());
    }

    /// <summary>
    /// O novo nome (sem extensão). Trechos sem informação somem; se o nome ficar vazio, fica o nome atual. Caracteres
    /// proibidos em nome de arquivo viram espaço e o nome é cortado em <see cref="MaxLength"/> letras.
    /// </summary>
    public static string NameFor(string template, RenameInfo info, int position, int total)
    {
        string number = position.ToString(CultureInfo.InvariantCulture).PadLeft(Math.Max(3, total.ToString(CultureInfo.InvariantCulture).Length), '0');
        string name = (template ?? "")
            .Replace("{titulo}", info.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{título}", info.Title, StringComparison.OrdinalIgnoreCase)
            .Replace("{autor}", info.Author, StringComparison.OrdinalIgnoreCase)
            .Replace("{data}", info.Date?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "", StringComparison.OrdinalIgnoreCase)
            .Replace("{linha}", info.FirstLine, StringComparison.OrdinalIgnoreCase)
            .Replace("{nome}", info.FileName, StringComparison.OrdinalIgnoreCase)
            .Replace("{n}", number, StringComparison.OrdinalIgnoreCase);
        var invalid = Path.GetInvalidFileNameChars();
        name = new string(name.Select(c => Array.IndexOf(invalid, c) >= 0 || char.IsControl(c) ? ' ' : c).ToArray());
        name = string.Join(' ', name.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        name = System.Text.RegularExpressions.Regex.Replace(name, @"([-_])(\s*[-_])+", "$1"); // "001 - - linha" → "001 - linha"
        name = name.Trim(' ', '.', '-', '_'); // sobra de separadores quando um campo veio vazio ("- relatório")
        if (name.Length > MaxLength) name = name[..MaxLength].TrimEnd(' ', '.');
        return name.Length == 0 ? info.FileName : name;
    }
}
