using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using PDFStudio.Controls;
using Windows.Foundation;
using PDFStudio.Core;
using PDFStudio.Services;

namespace PDFStudio.Pages;

// Etapa 5: ferramentas que escrevem no conteúdo da página (não são comentários).
// 5.1 "Adicionar texto": clicar na página e digitar, com fonte do Windows, tamanho, cor, negrito e itálico.
public sealed partial class ViewerPage
{
    /// <summary>Margem interna (pontos) da caixa de digitação do "Adicionar texto"; o texto começa no ponto clicado.</summary>
    private const double PageTextPadding = 3;

    private bool _pageFontsLoaded;
    private bool _settingPageFont;

    /// <summary>Preenche a lista de fontes (na primeira vez) e mostra as últimas escolhas.</summary>
    private void InitPageFontOptions()
    {
        _settingPageFont = true;
        try
        {
            if (!_pageFontsLoaded)
            {
                _pageFontsLoaded = true;
                foreach (var name in SystemFonts.Installed()) PageFontBox.Items.Add(name);
                if (PageFontBox.Items.Count == 0) PageFontBox.Items.Add("Arial");
            }
            string font = App.Settings.PageTextFont;
            PageFontBox.SelectedItem = PageFontBox.Items.Contains(font) ? font
                : PageFontBox.Items.Contains("Arial") ? "Arial" : PageFontBox.Items[0];
            PageBoldToggle.IsChecked = App.Settings.PageTextBold;
            PageItalicToggle.IsChecked = App.Settings.PageTextItalic;
        }
        finally
        {
            _settingPageFont = false;
        }
    }

    private async void PageFontBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_settingPageFont || PageFontBox.SelectedItem is not string font) return;
        // Algumas fontes não podem ser gravadas no PDF (ex.: fontes "PostScript" .otf): avisa logo na escolha.
        bool ok = await Task.Run(() => PdfPageContent.CanUseFont(font));
        if (!ok)
        {
            ShowStatus(InfoBarSeverity.Warning, "Fonte não disponível para PDF",
                $"A fonte \"{font}\" não pode ser gravada no PDF. Escolha outra.");
            InitPageFontOptions(); // volta para a última fonte que funcionou
            return;
        }
        App.Settings.PageTextFont = font;
        App.Settings.Save();
    }

    private void PageStyleToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (_settingPageFont) return;
        App.Settings.PageTextBold = PageBoldToggle.IsChecked == true;
        App.Settings.PageTextItalic = PageItalicToggle.IsChecked == true;
        App.Settings.Save();
    }

    // =====================================================================
    // 5.2 Imagem
    // =====================================================================

    private string? _imagePath;
    private double _imageWidth, _imageHeight; // tamanho natural, em pontos

    /// <summary>Parte da página (largura e altura) que a imagem ocupa no máximo quando colocada com um clique.</summary>
    private const double ImageMaxShare = 0.5;

    /// <summary>Escolhe a imagem (PNG ou JPG) e confere se ela pode ser lida. Devolve false se o usuário desistiu ou deu erro.</summary>
    private async Task<bool> ChooseImageAsync()
    {
        string? path = await FilePickers.PickFileAsync(PdfPageContent.ImageExtensions);
        if (path is null) return false;
        try
        {
            var (w, h) = await Task.Run(() => PdfPageContent.ImageSize(path));
            if (w <= 0 || h <= 0) throw new InvalidOperationException("A imagem está vazia.");
            (_imagePath, _imageWidth, _imageHeight) = (path, w, h);
            ImageNameText.Text = Path.GetFileName(path);
            ToolTipService.SetToolTip(ImageNameText, path);
            RemoveImageGhost(); // a prévia passa a mostrar a imagem nova
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            ShowStatus(InfoBarSeverity.Error, "Não foi possível usar a imagem", ex.Message);
            return false;
        }
    }

    private async void ChangeImage_Click(object sender, RoutedEventArgs e) => await ChooseImageAsync();

    /// <summary>Tamanho (DIPs) da imagem colocada com um clique: o natural, reduzido para caber em metade da página.</summary>
    private Size DefaultImageSize(DrawingSurface surface)
    {
        double w = _imageWidth * DipsPerPoint, h = _imageHeight * DipsPerPoint;
        double scale = Math.Min(1, Math.Min(surface.ActualWidth * ImageMaxShare / w, surface.ActualHeight * ImageMaxShare / h));
        return new Size(w * scale, h * scale);
    }

    // ---------------- Prévia (acompanha o mouse; ao arrastar, mostra o tamanho escolhido) ----------------

    private Image? _imageGhost;
    private DrawingSurface? _imageGhostSurface;

    private Image ImageGhost(DrawingSurface surface)
    {
        if (!ReferenceEquals(surface, _imageGhostSurface)) RemoveImageGhost();
        if (_imageGhost is null)
        {
            _imageGhost = new Image
            {
                Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(_imagePath!)),
                Stretch = Microsoft.UI.Xaml.Media.Stretch.Fill,
                Opacity = 0.6,
                IsHitTestVisible = false,
            };
            _imageGhostSurface = surface;
            surface.Children.Add(_imageGhost);
        }
        return _imageGhost;
    }

    private void RemoveImageGhost()
    {
        if (_imageGhost is not null) _imageGhostSurface?.Children.Remove(_imageGhost);
        _imageGhost = null;
        _imageGhostSurface = null;
    }

    private static void Place(FrameworkElement element, Rect r)
    {
        Canvas.SetLeft(element, r.X);
        Canvas.SetTop(element, r.Y);
        element.Width = r.Width;
        element.Height = r.Height;
    }

    /// <summary>Retângulo da imagem colocada com um clique: centrado no ponto e sem sair da página.</summary>
    private Rect ClickImageRect(DrawingSurface surface, Point p)
    {
        var size = DefaultImageSize(surface);
        double x = Math.Clamp(p.X - size.Width / 2, 0, Math.Max(0, surface.ActualWidth - size.Width));
        double y = Math.Clamp(p.Y - size.Height / 2, 0, Math.Max(0, surface.ActualHeight - size.Height));
        return new Rect(x, y, size.Width, size.Height);
    }

    /// <summary>Retângulo arrastado de <paramref name="a"/> até <paramref name="b"/>, com a proporção da imagem.</summary>
    private Rect DraggedImageRect(DrawingSurface surface, Point a, Point b)
    {
        double ratio = _imageHeight / _imageWidth;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double w = Math.Max(Math.Abs(dx), Math.Abs(dy) / ratio);
        // Não deixa passar da borda da página na direção do arraste.
        double maxW = dx >= 0 ? surface.ActualWidth - a.X : a.X;
        double maxH = dy >= 0 ? surface.ActualHeight - a.Y : a.Y;
        w = Math.Max(1, Math.Min(w, Math.Min(maxW, maxH / ratio)));
        double h = w * ratio;
        return new Rect(dx >= 0 ? a.X : a.X - w, dy >= 0 ? a.Y : a.Y - h, w, h);
    }

    private void MoveImageGhost(DrawingSurface surface, PointerRoutedEventArgs e)
    {
        if (_imagePath is null) return;
        var p = e.GetCurrentPoint(surface).Position;
        if (_imageDragSurface is not null)
        {
            if (!ReferenceEquals(surface, _imageDragSurface)) return;
            e.Handled = true;
            Place(ImageGhost(surface), DraggedImageRect(surface, _imageDragStart, ClampTo(surface, p)));
            return;
        }
        Place(ImageGhost(surface), ClickImageRect(surface, p));
    }

    // ---------------- Clicar ou arrastar para colocar ----------------

    private DrawingSurface? _imageDragSurface;
    private PageSlot? _imageDragSlot;
    private Point _imageDragStart;

    private static Point ClampTo(DrawingSurface surface, Point p) =>
        new(Math.Clamp(p.X, 0, surface.ActualWidth), Math.Clamp(p.Y, 0, surface.ActualHeight));

    private void StartImageDrag(DrawingSurface surface, PageSlot slot, PointerRoutedEventArgs e)
    {
        if (_imagePath is null) return;
        surface.CapturePointer(e.Pointer);
        _imageDragSurface = surface;
        _imageDragSlot = slot;
        _imageDragStart = ClampTo(surface, e.GetCurrentPoint(surface).Position);
    }

    private void CancelImageDrag()
    {
        _imageDragSurface?.ReleasePointerCaptures();
        _imageDragSurface = null;
        _imageDragSlot = null;
    }

    private async Task FinishImageDragAsync(DrawingSurface surface, PointerRoutedEventArgs e)
    {
        if (!ReferenceEquals(surface, _imageDragSurface) || _imageDragSlot is not { } slot || _imagePath is not { } path) return;
        e.Handled = true;
        var end = ClampTo(surface, e.GetCurrentPoint(surface).Position);
        var start = _imageDragStart;
        CancelImageDrag();
        RemoveImageGhost();
        bool click = Math.Abs(end.X - start.X) + Math.Abs(end.Y - start.Y) < 8;
        var r = click ? ClickImageRect(surface, start) : DraggedImageRect(surface, start, end);
        int page = slot.Index;
        await ApplyEditAsync((input, output) =>
            PdfPageContent.AddImage(input, page, r.X / DipsPerPoint, r.Y / DipsPerPoint, r.Width / DipsPerPoint, r.Height / DipsPerPoint, path, output), label: "Adicionar imagem");
    }

    private PageTextStyle CurrentPageTextStyle() => new(
        App.Settings.PageTextFont,
        App.Settings.PageTextFontSize,
        AnnotationColor.FromHex(App.Settings.PageTextColor),
        App.Settings.PageTextBold,
        App.Settings.PageTextItalic);
}
