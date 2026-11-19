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
            return HasEncryptDictionary(path) ? PasswordState.Restricted : PasswordState.None;
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
        if (!HasEncryptDictionary(input)) password = null; // sem proteção: a senha não importa
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

    /// <summary>O arquivo tem o dicionário /Encrypt (criptografia, com ou sem senha para abrir).</summary>
    /// <summary>
    /// Etapa 7.3: o autor permite imprimir? PDF sem proteção sempre permite. <paramref name="password"/>: a senha
    /// usada para abrir, se houver.
    /// </summary>
    public static bool AllowsPrinting(string path, string? password = null)
    {
        if (!HasEncryptDictionary(path)) return true;
        try
        {
            using var doc = string.IsNullOrEmpty(password)
                ? PdfReader.Open(path, PdfDocumentOpenMode.Import)
                : PdfReader.Open(path, password, PdfDocumentOpenMode.Import);
            return doc.SecuritySettings.PermitPrint;
        }
        catch (PdfReaderException ex) when (IsPasswordError(ex))
        {
            return true; // não deu para conferir; o próprio Windows recusaria abrir sem a senha
        }
    }

    private static bool HasEncryptDictionary(string path) =>
        System.Text.Encoding.Latin1.GetString(File.ReadAllBytes(path)).Contains("/Encrypt", StringComparison.Ordinal);

    private static bool IsPasswordError(PdfReaderException ex) =>
        ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase);
}
