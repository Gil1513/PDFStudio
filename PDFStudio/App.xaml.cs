using Microsoft.UI.Xaml;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio;

public partial class App : Application
{
    private Window? _window;

    /// <summary>Janela principal, usada pelos seletores de arquivo (precisam do WindowId).</summary>
    public static MainWindow? MainWindow { get; private set; }

    /// <summary>Arquivos recentes e com estrela.</summary>
    public static RecentFilesStore Recent { get; } = new(RecentFilesStore.DefaultPath);

    /// <summary>Preferências (tema, modo de exibição).</summary>
    public static AppSettings Settings { get; } = AppSettings.Load();

    public App()
    {
        InitializeComponent();
    }

    /// <summary>Arquivo onde os erros inesperados são anotados (para descobrir o que aconteceu depois).</summary>
    public static string ErrorLogPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDFStudio", "erros.log");

    /// <summary>Anota um erro inesperado em erros.log (só no computador; o arquivo fica pequeno).</summary>
    public static void LogError(string where, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ErrorLogPath)!);
            if (File.Exists(ErrorLogPath) && new FileInfo(ErrorLogPath).Length > 512 * 1024)
                File.Move(ErrorLogPath, ErrorLogPath + ".old", overwrite: true);
            File.AppendAllText(ErrorLogPath, $"[{DateTime.Now:dd/MM/yyyy HH:mm:ss}] {where}: {ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Não conseguir anotar o erro não pode causar outro erro.
        }
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

        // Cópias de trabalho esquecidas (app fechado à força) não ficam ocupando espaço.
        Task.Run(() => PDFStudio.Core.WorkingCopy.CleanupStale());
    }
}
