using Microsoft.UI.Xaml;

namespace PDFStudio;

public partial class App : Application
{
    private Window? _window;

    /// <summary>Janela principal, usada pelos seletores de arquivo (precisam do WindowId).</summary>
    public static MainWindow? MainWindow { get; private set; }

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Permite "Abrir com > PDF Studio" ou PDFStudio.exe arquivo.pdf
        string? startupFile = Environment.GetCommandLineArgs()
            .Skip(1)
            .FirstOrDefault(a => a.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) && File.Exists(a));

        MainWindow = new MainWindow(startupFile);
        _window = MainWindow;
        _window.Activate();
    }
}
