using PdfSharp.Pdf;

namespace PDFStudio.Core;

/// <summary>Em qual página fica a assinatura visível de cada arquivo do lote (os arquivos têm tamanhos diferentes).</summary>
public enum BatchSignaturePage { First, Last }

/// <summary>Etapa 8.2: senha, assinatura e impressão em lote.</summary>
public static partial class PdfBatch
{
    // ---------------- Senha ----------------

    /// <summary>
    /// Operação "colocar senha". <paramref name="currentPassword"/>: senha de permissões dos arquivos que já têm
    /// proteção (os outros não precisam dela).
    /// </summary>
    public static Func<string, string, string?> Protect(PasswordOptions options, string? currentPassword) => (input, output) =>
    {
        bool had = PdfPasswords.Inspect(input) != PasswordState.None;
        PdfPasswords.Protect(input, options, output, string.IsNullOrEmpty(currentPassword) ? null : currentPassword);
        return had ? "proteção trocada" : "protegido";
    };

    /// <summary>Operação "remover senha": a mesma senha é tentada em todos os arquivos.</summary>
    public static Func<string, string, string?> RemovePassword(string password) => (input, output) =>
    {
        if (PdfPasswords.Inspect(input) == PasswordState.None)
        {
            File.Copy(input, output, overwrite: true);
            return "já estava sem senha (cópia)";
        }
        PdfPasswords.Remove(input, password, output);
        return "senha removida";
    };

    // ---------------- Assinar ----------------

    /// <summary>
    /// Operação "assinar" com o mesmo certificado em todos os arquivos. Do <paramref name="template"/> valem o
    /// certificado, se é visível, o canto, o carimbo e os dados (motivo, local…); a página é a primeira ou a última de
    /// cada arquivo. Arquivo que já tem assinatura é recusado: gravá-lo de novo invalidaria a assinatura anterior.
    /// </summary>
    public static Func<string, string, string?> Sign(SignatureRequest template, BatchSignaturePage page) => (input, output) =>
    {
        if (PdfSigning.HasSignatures(input))
            throw new InvalidOperationException("Já tem assinatura: assinar de novo invalidaria a anterior (use a tela Assinar, um arquivo por vez).");
        int pageNumber = page == BatchSignaturePage.Last ? PdfOperations.GetPageCount(input) : 1;
        var request = new SignatureRequest
        {
            InputPath = input,
            OutputPath = output,
            PfxPath = template.PfxPath,
            PfxPassword = template.PfxPassword,
            Visible = template.Visible,
            PageNumber = pageNumber,
            Position = template.Position,
            Reason = template.Reason,
            Location = template.Location,
            ContactInfo = template.ContactInfo,
            StampSignerName = template.StampSignerName,
            StampTitle = template.StampTitle,
            StampShowDate = template.StampShowDate,
            StampShowDetails = template.StampShowDetails,
            TimestampUrl = template.TimestampUrl,
        };
        PdfSigning.SignAsync(request).GetAwaiter().GetResult();
        return template.Visible ? $"assinado (página {pageNumber})" : "assinado";
    };

    // ---------------- Converter e criar (Etapa 8.3) ----------------

    /// <summary>Operação "PDF para Word" (use com extension ".docx").</summary>
    public static Func<string, string, string?> ToWord() => (input, output) =>
    {
        var r = PdfToWord.Convert(input, output);
        return $"{r.Pages} {(r.Pages == 1 ? "página" : "páginas")}" + (r.Images > 0 ? $", {r.Images} {(r.Images == 1 ? "imagem" : "imagens")}" : "");
    };

    /// <summary>Operação "imagem para PDF": cada imagem vira um PDF de uma página.</summary>
    public static Func<string, string, string?> ImageToPdf(ImagePageSize size, ImageMargin margin) => (input, output) =>
    {
        ImagesToPdf.Create([input], size, margin, output);
        return null;
    };

    /// <summary>A entrada é uma imagem que dá para pôr num PDF (pela extensão)?</summary>
    public static bool IsImage(string path) =>
        ImagesToPdf.Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ---------------- Imprimir ----------------

    /// <summary>Por que o arquivo não pode ir para a impressão (null = pode).</summary>
    public static string? CannotPrint(string path)
    {
        if (!File.Exists(path)) return "O arquivo não existe mais.";
        try
        {
            return PdfPasswords.Inspect(path) switch
            {
                PasswordState.OpenPassword => "Tem senha para abrir: remova a senha antes.",
                _ when !PdfPasswords.AllowsPrinting(path) => "Quem criou o PDF não permite imprimir.",
                _ => null,
            };
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return FriendlyError(ex);
        }
    }

    /// <summary>
    /// Junta os arquivos num só, para uma única janela de impressão. <paramref name="eachOnNewSheet"/>: depois de um
    /// arquivo com número ímpar de páginas entra uma página em branco, assim, em frente e verso, o próximo arquivo
    /// começa numa folha nova. Devolve o total de páginas.
    /// </summary>
    public static int MergeForPrint(IReadOnlyList<string> inputs, string output, bool eachOnNewSheet)
    {
        if (inputs.Count == 0) throw new ArgumentException("Nenhum arquivo para imprimir.", nameof(inputs));
        using var result = new PdfDocument();
        for (int i = 0; i < inputs.Count; i++)
        {
            using var source = PdfOperations.OpenForImport(inputs[i]);
            PdfPage? last = null;
            foreach (var page in source.Pages) last = result.AddPage(page);
            if (eachOnNewSheet && i < inputs.Count - 1 && source.PageCount % 2 == 1 && last is not null)
            {
                var blank = result.AddPage();
                blank.Width = last.Width;
                blank.Height = last.Height;
            }
        }
        result.Info.Creator = "PDF Studio";
        int count = result.PageCount;
        PdfOperations.SaveSafely(result, output);
        return count;
    }
}
