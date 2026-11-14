using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using PDFStudio.Pages;

namespace PDFStudio;

/// <summary>
/// Janela principal: barra de título + menu lateral (NavigationView) que troca as páginas do app.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Arquivo a ser aberto na próxima vez que o visualizador for exibido.</summary>
    private string? _pendingViewerFile;

    public MainWindow(string? startupFile = null)
    {
        InitializeComponent();
        _pendingViewerFile = startupFile;

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1280, 860));

        NavView.SelectedItem = NavView.MenuItems[0];
    }

    /// <summary>Mostra o visualizador com o arquivo indicado.</summary>
    public void OpenInViewer(string path)
    {
        _pendingViewerFile = path;
        var viewerItem = NavView.MenuItems.OfType<NavigationViewItem>().First(i => (string)i.Tag == "viewer");
        if (ReferenceEquals(NavView.SelectedItem, viewerItem))
        {
            ContentFrame.Navigate(typeof(ViewerPage), TakePendingFile());
        }
        else
        {
            NavView.SelectedItem = viewerItem; // dispara SelectionChanged
        }
    }

    private string? TakePendingFile()
    {
        string? file = _pendingViewerFile;
        _pendingViewerFile = null;
        return file;
    }

    private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item) return;

        Type? page = (item.Tag as string) switch
        {
            "viewer" => typeof(ViewerPage),
            "mergesplit" => typeof(MergeSplitPage),
            "organize" => typeof(OrganizePage),
            "sign" => typeof(SignPage),
            _ => null,
        };
        if (page is null) return;

        object? parameter = page == typeof(ViewerPage) ? TakePendingFile() : null;
        if (ContentFrame.CurrentSourcePageType == page && parameter is null) return;
        ContentFrame.Navigate(page, parameter);
    }
}
