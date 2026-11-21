using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace PDFStudio.Core;

/// <summary>Como o PDF está protegido.</summary>
public enum PasswordState
{
    /// <summary>Sem criptografia.</summary>
    None,
    /// <summary>Abre sem senha, mas tem restrições (imprimir, copiar, editar…) guardadas por uma senha de permissões.</summary>
    Restricted,
    /// <summary>Precisa de senha para abrir.</summary>
    OpenPassword,
}

/// <summary>
/// O que colocar no PDF. <paramref name="OpenPassword"/>: senha pedida para abrir (vazia = abre sem senha).
/// <paramref name="PermissionsPassword"/>: senha que libera tudo e permite tirar a proteção (vazia = a mesma de abrir).
/// As permissões só valem quando há senha de permissões diferente da de abrir.
/// </summary>
public sealed record PasswordOptions(
    string OpenPassword,
    string PermissionsPassword = "",
    bool AllowPrint = true,
    bool AllowCopy = true,
    bool AllowEdit = true,
    bool AllowComments = true)
{
    public bool HasRestrictions => !(AllowPrint && AllowCopy && AllowEdit && AllowComments);
}

/// <summary>O que o autor do PDF permite fazer com ele (sem proteção ou com a senha de permissões: tudo).</summary>
public sealed record PdfPermissions(bool Print, bool Copy, bool Edit, bool Comments)
{
    public static readonly PdfPermissions All = new(true, true, true, true);
    /// <summary>Usado quando não dá para conferir: na dúvida, nada é liberado.</summary>
    public static readonly PdfPermissions Nothing = new(false, false, false, false);
}

/// <summary>A senha dada não serve para a operação pedida.</summary>
public sealed class PdfPasswordException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// Etapa 6.1: colocar e remover senha. Usa criptografia AES de 128 bits: a de 256 bits não abre no
/// visualizador do Windows (Windows.Data.Pdf), que o próprio app usa para desenhar as páginas.
/// Observação: as restrições (não imprimir, não copiar…) são respeitadas pelos leitores de PDF sérios,
/// mas não impedem tudo; a senha para abrir é a proteção de verdade.
/// </summary>
public static class PdfPasswords
{
    /// <summary>Descobre se o PDF tem senha para abrir, só restrições ou nada.</summary>
    public static PasswordState Inspect(string path)
    {
        try
        {
            PdfReader.Open(path, PdfDocumentOpenMode.Import).Dispose();
            return IsEncrypted(path) ? PasswordState.Restricted : PasswordState.None;
        }
        catch (PdfReaderException ex) when (IsPasswordError(ex))
        {
            return PasswordState.OpenPassword;
        }
    }

    /// <summary>
    /// Grava em <paramref name="output"/> o PDF protegido. <paramref name="currentPassword"/>: senha atual,
    /// se o PDF já tiver uma (precisa ser a de permissões).
    /// </summary>
    public static void Protect(string input, PasswordOptions options, string output, string? currentPassword = null)
    {
        string open = options.OpenPassword ?? "";
        string owner = options.PermissionsPassword ?? "";
        if (open.Length == 0 && owner.Length == 0)
            throw new ArgumentException("Informe a senha para abrir ou a senha de permissões.", nameof(options));
        if (options.HasRestrictions && (owner.Length == 0 || owner == open))
            throw new ArgumentException("Para limitar o que pode ser feito, a senha de permissões precisa ser diferente da senha para abrir.", nameof(options));

        using var doc = OpenForChange(input, currentPassword);
        var security = doc.SecuritySettings;
        security.UserPassword = open;
        security.OwnerPassword = owner.Length > 0 ? owner : open;
        security.PermitPrint = options.AllowPrint;
        security.PermitFullQualityPrint = options.AllowPrint;
        security.PermitExtractContent = options.AllowCopy;
        security.PermitModifyDocument = options.AllowEdit;
        security.PermitAssembleDocument = options.AllowEdit;
        security.PermitAnnotations = options.AllowComments;
        security.PermitFormsFill = options.AllowComments || options.AllowEdit;
        doc.SecurityHandler.SetEncryptionToV4UsingAES(); // AES-128 (veja o resumo da classe)
        PdfOperations.SaveSafely(doc, output);
    }

    /// <summary>Grava em <paramref name="output"/> o PDF sem senha nem restrições.</summary>
    public static void Remove(string input, string password, string output)
    {
        using var doc = OpenForChange(input, password);
        doc.SecurityHandler.SetEncryptionToNoneAndResetPasswords();
        PdfOperations.SaveSafely(doc, output);
    }

    /// <summary>Abre para alterar. Precisa da senha "dona" (a de permissões, ou a única senha que houver).</summary>
    private static PdfDocument OpenForChange(string input, string? password)
    {
        if (!IsEncrypted(input)) password = null; // sem proteção: a senha não importa
        try
        {
            return string.IsNullOrEmpty(password)
                ? PdfReader.Open(input, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(input, password, PdfDocumentOpenMode.Modify);
        }
        catch (PdfReaderException ex) when (IsPasswordError(ex))
        {
            bool ownerRequired = ex.Message.Contains("owner password", StringComparison.OrdinalIgnoreCase);
            throw new PdfPasswordException((ownerRequired, string.IsNullOrEmpty(password)) switch
            {
                (true, false) => "Esta é a senha para abrir. Para tirar ou trocar a proteção, use a senha de permissões.",
                (_, true) => "Este PDF está protegido. Informe a senha de permissões.",
                _ => "Senha incorreta.",
            }, ex);
        }
    }

    /// <summary>
    /// Etapa 7.3: o autor permite imprimir? PDF sem proteção sempre permite. <paramref name="password"/>: a senha
    /// usada para abrir, se houver.
    /// </summary>
    public static bool AllowsPrinting(string path, string? password = null) => Permissions(path, password).Print;

    /// <summary>
    /// O que o autor permite (imprimir, copiar o texto, alterar, comentar), lido do /Encrypt (/P). Com a senha de
    /// permissões, tudo. Se não der para conferir (senha errada, arquivo estragado), nada — o app falha "fechado".
    /// </summary>
    public static PdfPermissions Permissions(string path, string? password = null)
    {
        try
        {
            if (!IsEncrypted(path)) return PdfPermissions.All;
            using var doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(path, PdfDocumentOpenMode.Import)
                : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
            var security = doc.SecuritySettings;
            if (IsOwner(path, password)) return PdfPermissions.All;
            return new PdfPermissions(security.PermitPrint, security.PermitExtractContent,
                security.PermitModifyDocument, security.PermitAnnotations);
        }
        catch (Exception ex) when (ex is PdfReaderException or IOException or UnauthorizedAccessException or InvalidOperationException
                                       or NotSupportedException or FormatException or ArgumentException)
        {
            return PdfPermissions.Nothing;
        }
    }

    /// <summary>A senha dada é a "dona" (de permissões)? O PDFsharp só deixa abrir para alterar com ela.</summary>
    private static bool IsOwner(string path, string? password)
    {
        try
        {
            using var doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(path, PdfDocumentOpenMode.Modify)
                : PdfReader.Open(path, password, PdfDocumentOpenMode.Modify);
            return true;
        }
        catch (PdfReaderException)
        {
            return false;
        }
    }

    /// <summary>
    /// O arquivo é criptografado (tem /Encrypt no trailer mais recente)? Lê só o fim do arquivo e a tabela que ele
    /// aponta. Se a estrutura não for reconhecida, procura "/Encrypt" no arquivo todo (na dúvida, trata como protegido).
    /// </summary>
    public static bool IsEncrypted(string path)
    {
        using var file = File.OpenRead(path);
        try
        {
            int tailLength = (int)Math.Min(file.Length, 2048);
            var tail = new byte[tailLength];
            file.Seek(-tailLength, SeekOrigin.End);
            file.ReadExactly(tail);
            string end = System.Text.Encoding.Latin1.GetString(tail);
            var m = System.Text.RegularExpressions.Regex.Match(end, @"startxref\s+(\d+)", System.Text.RegularExpressions.RegexOptions.RightToLeft);
            if (m.Success && long.TryParse(m.Groups[1].Value, out long at) && at >= 0 && at < file.Length)
            {
                var buffer = new byte[(int)Math.Min(file.Length - at, 64 * 1024)];
                file.Seek(at, SeekOrigin.Begin);
                file.ReadExactly(buffer);
                string text = System.Text.Encoding.Latin1.GetString(buffer);
                string? dict = null;
                if (text.StartsWith("xref", StringComparison.Ordinal))
                {
                    int t = text.IndexOf("trailer", StringComparison.Ordinal);
                    int e = t < 0 ? -1 : text.IndexOf("startxref", t, StringComparison.Ordinal);
                    if (t >= 0) dict = e > t ? text[t..e] : text[t..];
                }
                else if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^\d+\s+\d+\s+obj") && text.Contains("/XRef", StringComparison.Ordinal))
                {
                    int st = text.IndexOf("stream", StringComparison.Ordinal);
                    dict = st > 0 ? text[..st] : null;
                }
                if (dict is not null) return System.Text.RegularExpressions.Regex.IsMatch(dict, @"/Encrypt\b");
            }
        }
        catch (IOException) { }
        // Estrutura que não reconhecemos: procura no arquivo todo.
        file.Seek(0, SeekOrigin.Begin);
        using var all = new MemoryStream();
        file.CopyTo(all);
        return System.Text.Encoding.Latin1.GetString(all.GetBuffer(), 0, (int)all.Length).Contains("/Encrypt", StringComparison.Ordinal);
    }

    private static bool IsPasswordError(PdfReaderException ex) =>
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);
}
