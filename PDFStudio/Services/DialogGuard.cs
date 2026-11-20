using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace PDFStudio.Services;

/// <summary>
/// O WinUI só permite uma janela de diálogo (ContentDialog) aberta por vez; abrir uma segunda lança uma exceção
/// e derruba o app. Use <see cref="AnyOpen"/> antes de mostrar um diálogo que pode aparecer "de surpresa"
/// (ex.: ao fechar a janela).
/// </summary>
public static class DialogGuard
{
    /// <summary>true se já existe um ContentDialog aberto nesta janela.</summary>
    public static bool AnyOpen(XamlRoot? root)
    {
        if (root is null) return false;
        try
        {
            return VisualTreeHelper.GetOpenPopupsForXamlRoot(root).Any(p => p.Child is ContentDialog);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
