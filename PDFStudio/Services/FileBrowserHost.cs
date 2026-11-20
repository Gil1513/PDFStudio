using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;

namespace PDFStudio.Services;

/// <summary>
/// Mostra a <see cref="FileBrowser"/> numa camada (Popup) que cobre a janela inteira, com o fundo escurecido.
/// Funciona por cima de um ContentDialog aberto (ex.: escolher arquivo dentro de "Inserir páginas").
/// </summary>
public static class FileBrowserHost
{
    private const double MaxWidth = 980, MaxHeight = 680, Margin = 32;
    private static bool _open;

    /// <summary>Os caminhos escolhidos, ou null se cancelou (ou se já havia uma tela de arquivos aberta).</summary>
    public static async Task<IReadOnlyList<string>?> ShowAsync(FileBrowserOptions options)
    {
        if (_open || App.MainWindow?.Content is not FrameworkElement content || content.XamlRoot is not { } root) return null;
        _open = true;
        var browser = new FileBrowser(options);
        var layer = new Grid
        {
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0x80, 0, 0, 0)),
            RequestedTheme = content.ActualTheme, // a camada não fica dentro da janela: não herda o tema sozinha
        };
        layer.Children.Add(browser);
        var popup = new Popup { XamlRoot = root, Child = layer, IsLightDismissEnabled = false };

        void Resize()
        {
            layer.Width = root.Size.Width;
            layer.Height = root.Size.Height;
            browser.Width = Math.Max(320, Math.Min(MaxWidth, root.Size.Width - 2 * Margin));
            browser.Height = Math.Max(300, Math.Min(MaxHeight, root.Size.Height - 2 * Margin));
        }
        void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) => Resize();

        Resize();
        root.Changed += RootChanged;
        popup.IsOpen = true;
        try
        {
            return await browser.Result;
        }
        finally
        {
            root.Changed -= RootChanged;
            popup.IsOpen = false;
            popup.Child = null;
            _open = false;
        }
    }
}
