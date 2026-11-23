using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace PDFStudio.Core;

/// <summary>
/// Liga o PDF Studio ao Windows, só para o usuário atual (sem pedir administrador):
/// <list type="bullet">
/// <item>aparece em "Abrir com" nos arquivos .pdf do Explorador de Arquivos;</item>
/// <item>ganha o item "Abrir com o PDF Studio" no menu do botão direito (em "Mostrar mais opções" no Windows 11);</item>
/// <item>aparece na lista de Aplicativos padrão do Windows, para a pessoa escolher como leitor de PDF.</item>
/// </list>
/// O Windows não deixa um programa se colocar como padrão sozinho: essa escolha é sempre da pessoa, nas
/// Configurações do Windows ou em "Abrir com &gt; Escolher outro aplicativo &gt; Sempre".
/// </summary>
[SupportedOSPlatform("windows")]
public static class WindowsIntegration
{
    public const string AppName = "PDF Studio";
    public const string ProgId = "PDFStudio.pdf";
    private const string ExeName = "PDFStudio.exe";
    private const string Verb = "PDFStudio.abrir";
    private const string CapabilitiesPath = @"PDFStudio\Capabilities";

    /// <summary>Comando que o Windows usa para abrir um arquivo com o app.</summary>
    public static string OpenCommand(string exePath) => $"\"{exePath}\" \"%1\"";

    /// <summary>
    /// Registra o app (ou atualiza o caminho do programa). <paramref name="software"/>: a chave "Software" onde
    /// gravar (null = HKEY_CURRENT_USER\Software; os testes usam outra, para não mexer no Windows).
    /// </summary>
    public static void Register(string exePath, RegistryKey? software = null)
    {
        if (string.IsNullOrWhiteSpace(exePath)) throw new ArgumentException("Caminho do programa vazio.", nameof(exePath));
        using var root = software is null ? Registry.CurrentUser.CreateSubKey("Software") : null;
        var sw = software ?? root!;
        string command = OpenCommand(exePath);
        string icon = $"\"{exePath}\",0";

        // Tipo de arquivo do app.
        using (var prog = sw.CreateSubKey($@"Classes\{ProgId}"))
        {
            prog.SetValue("", "Documento PDF");
            prog.SetValue("FriendlyTypeName", "Documento PDF");
            using (var defaultIcon = prog.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using var open = prog.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
        }
        // .pdf pode ser aberto por ele ("Abrir com").
        using (var progids = sw.CreateSubKey(@"Classes\.pdf\OpenWithProgids"))
            progids.SetValue(ProgId, Array.Empty<byte>(), RegistryValueKind.None);
        // O programa em si (nome bonito na lista "Abrir com").
        using (var app = sw.CreateSubKey($@"Classes\Applications\{ExeName}"))
        {
            app.SetValue("FriendlyAppName", AppName);
            using (var types = app.CreateSubKey("SupportedTypes")) types.SetValue(".pdf", "");
            using (var defaultIcon = app.CreateSubKey("DefaultIcon")) defaultIcon.SetValue("", icon);
            using var open = app.CreateSubKey(@"shell\open\command");
            open.SetValue("", command);
        }
        // Item "Abrir com o PDF Studio" no botão direito dos arquivos .pdf.
        using (var verb = sw.CreateSubKey($@"Classes\SystemFileAssociations\.pdf\shell\{Verb}"))
        {
            verb.SetValue("", "Abrir com o PDF Studio");
            verb.SetValue("Icon", icon);
            using var open = verb.CreateSubKey("command");
            open.SetValue("", command);
        }
        // Lista de Aplicativos padrão do Windows.
        using (var caps = sw.CreateSubKey(CapabilitiesPath))
        {
            caps.SetValue("ApplicationName", AppName);
            caps.SetValue("ApplicationDescription", "Ler, comentar, editar, assinar e organizar PDFs.");
            caps.SetValue("ApplicationIcon", icon);
            using var assoc = caps.CreateSubKey("FileAssociations");
            assoc.SetValue(".pdf", ProgId);
        }
        using (var registered = sw.CreateSubKey("RegisteredApplications"))
            registered.SetValue(AppName, $@"Software\{CapabilitiesPath}");

        if (software is null) NotifyShell();
    }

    /// <summary>Desfaz <see cref="Register"/>. Não mexe em nada que não seja do PDF Studio.</summary>
    public static void Unregister(RegistryKey? software = null)
    {
        using var root = software is null ? Registry.CurrentUser.OpenSubKey("Software", writable: true) : null;
        var sw = software ?? root;
        if (sw is null) return;
        sw.DeleteSubKeyTree($@"Classes\{ProgId}", throwOnMissingSubKey: false);
        sw.DeleteSubKeyTree($@"Classes\Applications\{ExeName}", throwOnMissingSubKey: false);
        sw.DeleteSubKeyTree($@"Classes\SystemFileAssociations\.pdf\shell\{Verb}", throwOnMissingSubKey: false);
        sw.DeleteSubKeyTree(CapabilitiesPath, throwOnMissingSubKey: false);
        using (var app = sw.OpenSubKey("PDFStudio", writable: true))
            if (app is { SubKeyCount: 0, ValueCount: 0 }) { app.Dispose(); sw.DeleteSubKey("PDFStudio", throwOnMissingSubKey: false); }
        using (var progids = sw.OpenSubKey(@"Classes\.pdf\OpenWithProgids", writable: true))
            progids?.DeleteValue(ProgId, throwOnMissingValue: false);
        using (var registered = sw.OpenSubKey("RegisteredApplications", writable: true))
            registered?.DeleteValue(AppName, throwOnMissingValue: false);
        if (software is null) NotifyShell();
    }

    /// <summary>O app está ligado ao Windows (com qualquer caminho de programa)?</summary>
    public static bool IsRegistered(RegistryKey? software = null) => RegisteredCommand(software) is not null;

    /// <summary>Ligado e apontando para <paramref name="exePath"/> (o programa não mudou de lugar)?</summary>
    public static bool IsRegisteredTo(string exePath, RegistryKey? software = null) =>
        string.Equals(RegisteredCommand(software), OpenCommand(exePath), StringComparison.OrdinalIgnoreCase);

    private static string? RegisteredCommand(RegistryKey? software)
    {
        using var root = software is null ? Registry.CurrentUser.OpenSubKey("Software") : null;
        var sw = software ?? root;
        using var key = sw?.OpenSubKey($@"Classes\{ProgId}\shell\open\command");
        return key?.GetValue("") as string;
    }

    /// <summary>Avisa o Explorador de Arquivos que as associações mudaram (para os menus atualizarem já).</summary>
    private static void NotifyShell()
    {
        try { SHChangeNotify(0x08000000 /* SHCNE_ASSOCCHANGED */, 0, IntPtr.Zero, IntPtr.Zero); }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException) { /* sem Explorador: tanto faz */ }
    }

    [DllImport("shell32.dll")]
    private static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
}
