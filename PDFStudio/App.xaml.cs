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
        // Pegador geral: um erro inesperado em qualquer botão é anotado e avisado, em vez de fechar o app
        // sem aviso (e perder o que não foi salvo).
        UnhandledException += App_UnhandledException;
        SafeSynchronizationContext.Install(ex =>
        {
            LogError("Erro inesperado", ex);
            ShowError(ex.Message);
        });
        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            LogError("Tarefa em segundo plano", e.Exception);
            e.SetObserved();
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) LogError("Erro fatal", ex);
        };
    }

    private bool _showingError;

    private void App_UnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        LogError("Erro inesperado", e.Exception);
        if (e.Exception is OutOfMemoryException or StackOverflowException) return; // sem como continuar com segurança
        e.Handled = true;
        ShowError(e.Message);
    }

    /// <summary>Avisa a pessoa que uma ação falhou (sem abrir dois avisos nem brigar com outra janela de diálogo).</summary>
    private async void ShowError(string message)
    {
        var root = MainWindow?.Content?.XamlRoot;
        if (root is null || _showingError || DialogGuard.AnyOpen(root)) return;
        _showingError = true;
        try
        {
            var dialog = new Microsoft.UI.Xaml.Controls.ContentDialog
            {
                XamlRoot = root,
                RequestedTheme = Settings.Theme,
                Title = "Algo deu errado",
                Content = "Esta ação não pôde ser concluída. O app continua aberto e o que não foi salvo foi mantido.\n\n"
                    + $"Detalhe: {message}\n\nO erro foi anotado em:\n{ErrorLogPath}",
                CloseButtonText = "OK",
            };
            await dialog.ShowAsync();
        }
        catch (Exception)
        {
            // Não conseguir mostrar o aviso não pode causar outro erro.
        }
        finally
        {
            _showingError = false;
        }
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

        // Se a pessoa já ligou o app ao Windows e depois mudou a pasta dele, corrige o caminho.
        try
        {
            if (Environment.ProcessPath is { } exe && WindowsIntegration.IsRegistered() && !WindowsIntegration.IsRegisteredTo(exe))
                WindowsIntegration.Register(exe);
        }
        catch (Exception) { /* não é motivo para não abrir o app */ }

        // Cópias de trabalho esquecidas (app fechado à força) não ficam ocupando espaço.
        Task.Run(() =>
        {
            PDFStudio.Core.WorkingCopy.CleanupStale();
            try { PDFStudio.Core.WorkingCopy.CleanupLooseCopies(); }
            catch (Exception ex) { LogError("Limpar cópias temporárias", ex); }
            try { PDFStudio.Core.PdfAttachments.CleanupOpenedCopies(); }
            catch (Exception ex) { LogError("Limpar cópias de anexos", ex); }
        });
    }
}
