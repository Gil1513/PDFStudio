using Microsoft.Win32;
using PDFStudio.Core;

namespace PDFStudio.Tests;

/// <summary>Ligar o app ao Windows ("Abrir com", botão direito, Aplicativos padrão), numa chave de teste.</summary>
public class WindowsIntegrationTests : IDisposable
{
    // Chave própria do teste: nada do Windows de verdade é alterado.
    private readonly string _path = $@"Software\PDFStudioTestes-{Guid.NewGuid():N}";
    private readonly RegistryKey _software;

    public WindowsIntegrationTests() => _software = Registry.CurrentUser.CreateSubKey(_path);

    public void Dispose()
    {
        _software.Dispose();
        Registry.CurrentUser.DeleteSubKeyTree(_path, throwOnMissingSubKey: false);
    }

    private string? Value(string key, string name = "")
    {
        using var k = _software.OpenSubKey(key);
        return k?.GetValue(name) as string;
    }

    [Fact]
    public void Register_AddsOpenWith_RightClick_AndDefaultAppsEntry()
    {
        const string exe = @"C:\Programas\PDF Studio\PDFStudio.exe";
        Assert.False(WindowsIntegration.IsRegistered(_software));

        WindowsIntegration.Register(exe, _software);

        string command = "\"C:\\Programas\\PDF Studio\\PDFStudio.exe\" \"%1\"";
        Assert.Equal(command, Value(@"Classes\PDFStudio.pdf\shell\open\command"));
        Assert.Equal(command, Value(@"Classes\Applications\PDFStudio.exe\shell\open\command"));
        Assert.Equal(command, Value(@"Classes\SystemFileAssociations\.pdf\shell\PDFStudio.abrir\command"));
        Assert.Equal("Abrir com o PDF Studio", Value(@"Classes\SystemFileAssociations\.pdf\shell\PDFStudio.abrir"));
        using (var progids = _software.OpenSubKey(@"Classes\.pdf\OpenWithProgids"))
            Assert.Contains("PDFStudio.pdf", progids!.GetValueNames());
        Assert.Equal("PDFStudio.pdf", Value(@"PDFStudio\Capabilities\FileAssociations", ".pdf"));
        Assert.Equal(@"Software\PDFStudio\Capabilities", Value("RegisteredApplications", "PDF Studio"));
        Assert.True(WindowsIntegration.IsRegisteredTo(exe, _software));
        Assert.False(WindowsIntegration.IsRegisteredTo(@"C:\outro\PDFStudio.exe", _software));
    }

    [Fact]
    public void Unregister_RemovesOnlyWhatIsOurs()
    {
        // Algo de outro programa que já estava lá.
        using (var other = _software.CreateSubKey(@"Classes\.pdf\OpenWithProgids")) other.SetValue("OutroLeitor.pdf", "");
        using (var reg = _software.CreateSubKey("RegisteredApplications")) reg.SetValue("Outro Leitor", @"Software\Outro\Capabilities");

        WindowsIntegration.Register(@"C:\app\PDFStudio.exe", _software);
        WindowsIntegration.Unregister(_software);

        Assert.False(WindowsIntegration.IsRegistered(_software));
        Assert.Null(_software.OpenSubKey(@"Classes\PDFStudio.pdf"));
        Assert.Null(_software.OpenSubKey(@"Classes\Applications\PDFStudio.exe"));
        Assert.Null(_software.OpenSubKey(@"Classes\SystemFileAssociations\.pdf\shell\PDFStudio.abrir"));
        Assert.Null(_software.OpenSubKey("PDFStudio"));
        using (var progids = _software.OpenSubKey(@"Classes\.pdf\OpenWithProgids"))
            Assert.Equal(["OutroLeitor.pdf"], progids!.GetValueNames());
        using (var reg = _software.OpenSubKey("RegisteredApplications"))
            Assert.Equal(["Outro Leitor"], reg!.GetValueNames());
    }

    [Fact]
    public void Register_Again_UpdatesTheProgramPath()
    {
        WindowsIntegration.Register(@"C:\antigo\PDFStudio.exe", _software);
        WindowsIntegration.Register(@"D:\novo\PDFStudio.exe", _software);
        Assert.True(WindowsIntegration.IsRegisteredTo(@"D:\novo\PDFStudio.exe", _software));
    }
}
