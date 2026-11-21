using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Selecionar e copiar texto. Sem nenhuma ferramenta ligada: arrastar sobre o texto da página seleciona
// (fica azul); Ctrl+C ou o botão direito → "Copiar" leva o texto para a área de transferência.
// Clicar fora (ou trocar de ferramenta) desfaz a seleção.
public sealed partial class ViewerPage
{
    private DrawingSurface? _copySurface;    // página onde está a seleção
    private int _copyPage = -1;
    private Canvas? _copyLayer;              // retângulos azuis da seleção
    private Point _copyStart;
    private Task<List<TextWord>>? _copyWordsTask;
    private List<TextWord> _copySelected = [];
    private bool _copyDragging;

    /// <summary>Começa a seleção no ponto clicado (botão esquerdo, sem ferramenta).</summary>
    private void StartCopySelection(DrawingSurface surface, PageSlot slot, PointerRoutedEventArgs e)
    {
        ClearCopySelection();
        if (DisplayPath is null) return;
        surface.CapturePointer(e.Pointer);
        _copySurface = surface;
        _copyPage = slot.Index;
        _copyStart = e.GetCurrentPoint(surface).Position;
        _copyDragging = true;
        _copyLayer = new Canvas { IsHitTestVisible = false };
        surface.Children.Add(_copyLayer);
        _copyWordsTask = WordsOfPage(slot.Index); // normalmente já lidas quando o mouse entrou na página
    }

    private void MoveCopySelection(Point current)
    {
        if (!_copyDragging || _copyWordsTask is not { IsCompletedSuccessfully: true } task) return;
        PaintCopySelection(task.Result, current);
    }

    private async Task FinishCopySelectionAsync(Point current)
    {
        if (!_copyDragging || _copyWordsTask is not { } task) return;
        _copyDragging = false;
        _copySurface?.ReleasePointerCaptures();
        var layer = _copyLayer;
        var words = await task;
        if (!ReferenceEquals(layer, _copyLayer)) return; // outra seleção começou enquanto lia
        PaintCopySelection(words, current);
        if (_copySelected.Count == 0) ClearCopySelection(); // foi só um clique
    }

    /// <summary>Seleciona as palavras entre o começo do arraste e <paramref name="current"/> e pinta de azul.</summary>
    private void PaintCopySelection(List<TextWord> words, Point current)
    {
        if (_copyLayer is not { } layer) return;
        layer.Children.Clear();
        _copySelected = [];
        if (words.Count == 0) return;
        double ax = _copyStart.X / DipsPerPoint, ay = _copyStart.Y / DipsPerPoint;
        double bx = current.X / DipsPerPoint, by = current.Y / DipsPerPoint;
        // Um clique (ou arraste mínimo) não seleciona; e precisa começar ou terminar perto de algum texto.
        if (Math.Abs(current.X - _copyStart.X) + Math.Abs(current.Y - _copyStart.Y) < 4) return;
        if (words.Min(w => w.DistanceTo(ax, ay)) >= 12 && words.Min(w => w.DistanceTo(bx, by)) >= 12) return;
        _copySelected = PdfTextLayout.Select(words, ax, ay, bx, by);

        var c = ((SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]).Color;
        var fill = new SolidColorBrush(Windows.UI.Color.FromArgb(80, c.R, c.G, c.B));
        foreach (var (x, y, w, h) in PdfTextLayout.LineRects(_copySelected))
        {
            var rect = new Shapes.Rectangle { Width = w * DipsPerPoint, Height = h * DipsPerPoint, Fill = fill };
            Canvas.SetLeft(rect, x * DipsPerPoint);
            Canvas.SetTop(rect, y * DipsPerPoint);
            layer.Children.Add(rect);
        }
    }

    private void ClearCopySelection()
    {
        if (_copyLayer is not null) _copySurface?.Children.Remove(_copyLayer);
        if (_copyDragging) _copySurface?.ReleasePointerCaptures();
        _copyLayer = null;
        _copySurface = null;
        _copyPage = -1;
        _copyWordsTask = null;
        _copySelected = [];
        _copyDragging = false;
    }

    private bool HasCopySelection => _copySelected.Count > 0;

    private async void CopySelectedText()
    {
        if (!HasCopySelection) return;
        var selected = _copySelected;
        if (!await CopyAllowedAsync("Cópia não permitida")) return;
        if (!ReferenceEquals(selected, _copySelected)) return; // a seleção mudou enquanto conferia
        var package = new DataPackage();
        package.SetText(PdfTextLayout.TextOf(_copySelected));
        Clipboard.SetContent(package);
        // Grava de vez: o texto continua copiado mesmo depois de fechar o app.
        try { Clipboard.Flush(); } catch (Exception) { /* outro programa usando a área de transferência */ }
        ShowStatus(InfoBarSeverity.Success, "Texto copiado", "Cole com Ctrl+V onde quiser.");
    }

    /// <summary>
    /// Ctrl+C com texto da página selecionado. Pego antes de chegar a uma caixa de texto com o cursor (ex.:
    /// número da página ou pesquisa), que "engoliria" a tecla mesmo sem nada marcado nela.
    /// </summary>
    private void CopyKey_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != Windows.System.VirtualKey.C || !HasCopySelection) return;
        var ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control);
        if (!ctrl.HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down)) return;
        // Numa caixa de texto com algo marcado, o Ctrl+C é dela.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox { SelectionLength: > 0 } or PasswordBox or RichEditBox) return;
        e.Handled = true;
        CopySelectedText();
    }

    /// <summary>Botão direito sobre a página com texto selecionado: menu com "Copiar".</summary>
    private void ShowCopyMenu(DrawingSurface surface, Point p)
    {
        var copy = new MenuFlyoutItem { Text = "Copiar", Icon = new FontIcon { Glyph = "" } };
        copy.KeyboardAccelerators.Add(new KeyboardAccelerator { Key = Windows.System.VirtualKey.C, Modifiers = Windows.System.VirtualKeyModifiers.Control, IsEnabled = false });
        copy.Click += (_, _) => CopySelectedText();
        var menu = new MenuFlyout { Items = { copy } };
        menu.ShowAt(surface, p);
    }

    private void Accel_Copy(KeyboardAccelerator s, KeyboardAcceleratorInvokedEventArgs a)
    {
        // Numa caixa de texto com algo marcado, o Ctrl+C é dela.
        if (FocusManager.GetFocusedElement(XamlRoot) is TextBox { SelectionLength: > 0 } or PasswordBox) return;
        if (!HasCopySelection) return;
        a.Handled = true;
        CopySelectedText();
    }

    /// <summary>Sem ferramenta: I (de texto) sobre as palavras, para mostrar que dá para selecionar.</summary>
    private bool IsOverText(PageSlot slot, Point p)
    {
        if (DisplayPath is null) return false;
        var task = WordsOfPage(slot.Index);
        if (!task.IsCompletedSuccessfully) return false;
        double x = p.X / DipsPerPoint, y = p.Y / DipsPerPoint;
        return task.Result.Any(w => w.DistanceTo(x, y) < 1);
    }
}
