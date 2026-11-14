using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using PDFStudio.Pages;
using PDFStudio.Services;

namespace PDFStudio;

/// <summary>
/// Janela principal: barra de título (com botão voltar) + um Frame que mostra a tela inicial
/// ou a ferramenta escolhida.
/// </summary>
public sealed partial class MainWindow : Window
{
    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        FitToScreen();
        Activated += FirstActivated;
        ApplyTheme(App.Settings.Theme);

        ContentFrame.Navigate(typeof(HomePage));
        if (startupFile is not null) OpenInViewer(startupFile);
    }

    /// <summary>Abre com 90% da área útil da tela (até 1800 x 1100 px), centralizada.</summary>
    private void FitToScreen()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        if (area.Width < 800 || area.Height < 600) area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        if (area.Width < 800 || area.Height < 600) return;
        int w = Math.Min((int)(area.Width * 0.9), 1800), h = Math.Min((int)(area.Height * 0.9), 1100);
        AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    private void FirstActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= FirstActivated;
        FitToScreen(); // repete quando a janela já está na tela certa
    }

    // ---------- Navegação ----------

    /// <summary>Mostra o visualizador com o arquivo indicado.</summary>
    public void OpenInViewer(string path) => ContentFrame.Navigate(typeof(ViewerPage), path, new DrillInNavigationTransitionInfo());

    /// <summary>Abre uma ferramenta pela chave usada nos cartões da tela inicial.</summary>
    public void OpenTool(string key, object? parameter = null)
    {
        Type? page = key switch
        {
            "viewer" => typeof(ViewerPage),
            "combinar" or "dividir" => typeof(MergeSplitPage),
            "organizar" => typeof(OrganizePage),
            "assinar" => typeof(SignPage),
            "configuracoes" => typeof(SettingsPage),
            _ => null,
        };
        if (page is null) return;
        if (key == "dividir") parameter ??= "split";
        if (key == "combinar") parameter ??= "merge";
        ContentFrame.Navigate(page, parameter, new DrillInNavigationTransitionInfo());
    }

    /// <summary>Texto ao lado de "PDF Studio" na barra de título (ex.: nome do arquivo aberto).</summary>
    public void SetSubtitle(string text) => AppTitleBar.Subtitle = text;

    /// <summary>Volta para a tela inicial.</summary>
    public void GoHome()
    {
        while (ContentFrame.CanGoBack && ContentFrame.CurrentSourcePageType != typeof(HomePage)) ContentFrame.GoBack();
        if (ContentFrame.CurrentSourcePageType != typeof(HomePage)) ContentFrame.Navigate(typeof(HomePage));
    }

    /// <summary>Escolhe um PDF e abre no visualizador.</summary>
    public async Task PickAndOpenAsync()
    {
        string? path = await FilePickers.PickPdfAsync();
        if (path is not null) OpenInViewer(path);
    }

    private void AppTitleBar_BackRequested(TitleBar sender, object args)
    {
        if (ContentFrame.CanGoBack) ContentFrame.GoBack();
    }

    private void ContentFrame_Navigated(object sender, NavigationEventArgs e)
    {
        bool home = e.SourcePageType == typeof(HomePage);
        AppTitleBar.IsBackButtonVisible = !home && ContentFrame.CanGoBack;
        AppTitleBar.Subtitle = e.SourcePageType.Name switch
        {
            nameof(ViewerPage) => (ContentFrame.Content as ViewerPage)?.FileName ?? "Visualizar",
            nameof(MergeSplitPage) => "Juntar e dividir",
            nameof(OrganizePage) => "Organizar páginas",
            nameof(SignPage) => "Assinar",
            nameof(SettingsPage) => "Configurações",
            _ => "",
        };
    }

    private async void OpenAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        // No visualizador o próprio botão "Abrir" já trata o Ctrl+O.
        if (ContentFrame.CurrentSourcePageType == typeof(ViewerPage)) return;
        args.Handled = true;
        await PickAndOpenAsync();
    }

    // ---------- Tema ----------

    public void ApplyTheme(ElementTheme theme)
    {
        RootGrid.RequestedTheme = theme;
        bool dark = theme == ElementTheme.Dark
                    || (theme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark);

        // Botões minimizar/maximizar/fechar acompanham o tema.
        var bar = AppWindow.TitleBar;
        bar.ButtonBackgroundColor = Colors.Transparent;
        bar.ButtonInactiveBackgroundColor = Colors.Transparent;
        bar.ButtonForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverForegroundColor = dark ? Colors.White : Colors.Black;
        bar.ButtonHoverBackgroundColor = dark ? Windows.UI.Color.FromArgb(40, 255, 255, 255) : Windows.UI.Color.FromArgb(30, 0, 0, 0);
        bar.ButtonInactiveForegroundColor = dark ? Windows.UI.Color.FromArgb(140, 255, 255, 255) : Windows.UI.Color.FromArgb(140, 0, 0, 0);
    }
}
