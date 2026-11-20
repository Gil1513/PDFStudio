using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using PDFStudio.Controls;
using PDFStudio.Core;
using PDFStudio.Services;
using Windows.Foundation;
using Shapes = Microsoft.UI.Xaml.Shapes;

namespace PDFStudio.Pages;

// Mover e redimensionar comentários já feitos (ferramenta "Mover comentários", na aba Comentário).
// Clicar num comentário seleciona; arrastar move; as alças dos cantos mudam o tamanho (Shift mantém a proporção;
// no carimbo a proporção é sempre mantida). Realces acompanham o texto e não se movem; a nota só muda de lugar.
public sealed partial class ViewerPage
{
    private enum MoveGrip { None, Body, TopLeft, TopRight, BottomLeft, BottomRight }

    private Task<List<AnnotationInfo>>? _moveListTask;
    private string? _moveListPath;

    private AnnotationInfo? _moveSel;            // comentário selecionado
    private DrawingSurface? _moveSurface;        // página onde ele está
    private PageSlot? _moveSlot;
    private Canvas? _moveFrame;                  // contorno + alças
    private Rect _moveBox;                       // caixa atual na tela (DIPs), muda enquanto arrasta
    private MoveGrip _moveGrip;                  // o que está sendo arrastado
    private Point _moveStart;
    private Rect _moveStartBox;
    private bool _moveButtonDown;                // o botão ainda está apertado (a lista pode ter demorado)

    private const double GripSize = 9; // lado da alça, em pixels da tela (não muda com o zoom)

    /// <summary>Os comentários do arquivo mostrado (lidos uma vez por versão).</summary>
    private Task<List<AnnotationInfo>> CommentsForMove()
    {
        string path = DisplayPath!;
        if (_moveListPath != path || _moveListTask is null)
        {
            _moveListPath = path;
            _moveListTask = Task.Run(() =>
            {
                try { return PdfAnnotations.List(path); }
                catch (Exception) { return new List<AnnotationInfo>(); }
            });
        }
        return _moveListTask;
    }

    /// <summary>Comentário sob o ponto (DIPs). Os que podem ser movidos têm preferência; o de cima ganha.</summary>
    private AnnotationInfo? CommentAt(PageSlot slot, Point p)
    {
        if (DisplayPath is null) return null;
        var task = CommentsForMove();
        if (!task.IsCompletedSuccessfully) return null;
        double x = p.X / DipsPerPoint, y = p.Y / DipsPerPoint, tolerance = 3 / Zoom;
        var hits = task.Result.Where(a => a.PageIndex == slot.Index && !a.Hidden &&
            x >= a.X - tolerance && x <= a.X + a.Width + tolerance && y >= a.Y - tolerance && y <= a.Y + a.Height + tolerance).ToList();
        return hits.LastOrDefault(a => PdfAnnotations.CanMove(a.Kind)) ?? hits.LastOrDefault();
    }

    private double Zoom => Math.Max(0.1, Scroller.ZoomFactor);

    // ---------------- Mouse ----------------

    private async Task MovePressedAsync(DrawingSurface surface, PageSlot slot, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(surface).Position;
        // Prende o mouse já no clique: se a lista ainda estiver sendo lida, o arraste não se perde.
        surface.CapturePointer(e.Pointer);
        _moveButtonDown = true;
        var list = CommentsForMove();
        if (!list.IsCompleted) await list; // normalmente já foi lida (ao ligar a ferramenta / ao passar o mouse)
        var grip = ReferenceEquals(surface, _moveSurface) ? GripAt(p) : MoveGrip.None;
        if (grip == MoveGrip.None)
        {
            var hit = CommentAt(slot, p);
            if (hit is null) { surface.ReleasePointerCaptures(); ClearCommentSelection(); return; }
            if (!PdfAnnotations.CanMove(hit.Kind))
            {
                surface.ReleasePointerCaptures();
                ClearCommentSelection();
                ShowStatus(InfoBarSeverity.Informational, "Este comentário não se move",
                    hit.Kind is AnnotationKind.Highlight or AnnotationKind.Underline or AnnotationKind.StrikeOut
                        ? "Realces, sublinhados e riscos acompanham o texto da página. Para trocar, exclua e marque de novo."
                        : "O PDF Studio não sabe mover este tipo de comentário.");
                return;
            }
            SelectComment(surface, slot, hit);
            grip = MoveGrip.Body;
        }
        if (!_moveButtonDown) return; // já soltou enquanto lia: fica só selecionado
        _moveGrip = grip;
        _moveStart = p;
        _moveStartBox = _moveBox;
    }

    private void MoveMoved(DrawingSurface surface, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(surface).Position;
        if (_moveGrip == MoveGrip.None || !ReferenceEquals(surface, _moveSurface))
        {
            UpdateMoveCursor(surface, p);
            return;
        }
        double dx = p.X - _moveStart.X, dy = p.Y - _moveStart.Y;
        var b = _moveStartBox;
        double w = surface.Width, h = surface.Height;
        if (_moveGrip == MoveGrip.Body)
        {
            double x = Math.Clamp(b.X + dx, 0, Math.Max(0, w - b.Width)), y = Math.Clamp(b.Y + dy, 0, Math.Max(0, h - b.Height));
            _moveBox = new Rect(x, y, b.Width, b.Height);
        }
        else
        {
            // O canto oposto fica parado; o arrastado segue o mouse (dentro da página).
            double min = PdfAnnotations.MinCommentSide * DipsPerPoint;
            double left = b.Left, top = b.Top, right = b.Right, bottom = b.Bottom;
            bool west = _moveGrip is MoveGrip.TopLeft or MoveGrip.BottomLeft, north = _moveGrip is MoveGrip.TopLeft or MoveGrip.TopRight;
            if (west) left = Math.Clamp(b.Left + dx, 0, right - min); else right = Math.Clamp(b.Right + dx, left + min, w);
            if (north) top = Math.Clamp(b.Top + dy, 0, bottom - min); else bottom = Math.Clamp(b.Bottom + dy, top + min, h);
            bool keepRatio = _moveSel?.Kind == AnnotationKind.Stamp || e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift);
            if (keepRatio && b.Width > 0 && b.Height > 0)
            {
                double scale = Math.Max((right - left) / b.Width, (bottom - top) / b.Height);
                double nw = b.Width * scale, nh = b.Height * scale;
                if (west) left = right - nw; else right = left + nw;
                if (north) top = bottom - nh; else bottom = top + nh;
                if (left < 0 || top < 0 || right > w || bottom > h) return; // passaria da página: fica como está
            }
            _moveBox = new Rect(left, top, right - left, bottom - top);
        }
        PlaceMoveFrame();
    }

    private async Task MoveReleasedAsync(DrawingSurface surface)
    {
        _moveButtonDown = false;
        if (_moveGrip == MoveGrip.None || !ReferenceEquals(surface, _moveSurface) || _moveSel is not { } sel || _moveSlot is null) return;
        var grip = _moveGrip;
        var b = _moveBox;
        _moveGrip = MoveGrip.None; // antes de soltar o mouse: soltar dispara "perdeu o mouse", que desfaria a caixa
        surface.ReleasePointerCaptures();
        if (Math.Abs(b.X - _moveStartBox.X) + Math.Abs(b.Y - _moveStartBox.Y) + Math.Abs(b.Width - _moveStartBox.Width) +
            Math.Abs(b.Height - _moveStartBox.Height) < 1) return; // só um clique: continua selecionado

        int page = _moveSlot.Index, index = sel.Index;
        double x = b.X / DipsPerPoint, y = b.Y / DipsPerPoint, wPt = b.Width / DipsPerPoint, hPt = b.Height / DipsPerPoint;
        string label = grip == MoveGrip.Body ? "Mover comentário" : "Redimensionar comentário";
        var slot = _moveSlot;
        await ApplyEditAsync((input, output) => PdfAnnotations.MoveResize(input, page, index, x, y, wPt, hPt, output), label: label);

        // Gravado (ou não, se deu erro): a seleção volta a aparecer onde o comentário está agora.
        if (_tool?.Mode != DrawMode.MoveComment || DisplayPath is null) return;
        var now = (await CommentsForMove()).FirstOrDefault(a => a.PageIndex == sel.PageIndex && a.Index == sel.Index);
        if (now is not null) SelectComment(surface, slot, now);
    }

    /// <summary>O mouse escapou no meio do arraste: a seleção volta para onde estava.</summary>
    private void MoveCaptureLost(DrawingSurface surface)
    {
        if (_moveGrip == MoveGrip.None || !ReferenceEquals(surface, _moveSurface)) return;
        _moveGrip = MoveGrip.None;
        _moveBox = _moveStartBox;
        PlaceMoveFrame();
    }

    private void UpdateMoveCursor(DrawingSurface surface, Point p)
    {
        InputSystemCursorShape? cursor = null;
        var grip = ReferenceEquals(surface, _moveSurface) ? GripAt(p) : MoveGrip.None;
        if (grip is MoveGrip.TopLeft or MoveGrip.BottomRight) cursor = InputSystemCursorShape.SizeNorthwestSoutheast;
        else if (grip is MoveGrip.TopRight or MoveGrip.BottomLeft) cursor = InputSystemCursorShape.SizeNortheastSouthwest;
        else if (grip == MoveGrip.Body || (SlotOf(surface) is { } slot && CommentAt(slot, p) is { } a && PdfAnnotations.CanMove(a.Kind)))
            cursor = InputSystemCursorShape.SizeAll;
        surface.SetCursor(cursor);
    }

    // ---------------- Seleção na tela ----------------

    private void SelectComment(DrawingSurface surface, PageSlot slot, AnnotationInfo a)
    {
        if (!ReferenceEquals(surface, _moveSurface)) ClearCommentSelection();
        _moveSel = a;
        _moveSurface = surface;
        _moveSlot = slot;
        _moveBox = new Rect(a.X * DipsPerPoint, a.Y * DipsPerPoint, a.Width * DipsPerPoint, a.Height * DipsPerPoint);
        if (_moveFrame is null)
        {
            _moveFrame = new Canvas { IsHitTestVisible = false };
            surface.Children.Add(_moveFrame);
        }
        PlaceMoveFrame();
        ToolHintText.Text = PdfAnnotations.CanResize(a.Kind)
            ? "Arraste para mover; puxe os cantos para mudar o tamanho (Shift mantém a proporção). Esc tira a seleção."
            : "Arraste para mover a nota. Esc tira a seleção.";
    }

    /// <summary>Desenha o contorno e as alças na caixa atual (alças com tamanho fixo na tela, qualquer que seja o zoom).</summary>
    private void PlaceMoveFrame()
    {
        if (_moveFrame is null || _moveSel is null) return;
        _moveFrame.Children.Clear();
        var accent = (SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        double z = Zoom, b = _moveBox.Width, h = _moveBox.Height;
        var outline = new Shapes.Rectangle
        {
            Width = b, Height = h, Stroke = accent, StrokeThickness = 1.5 / z, StrokeDashArray = [4, 3],
            Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(20, accent.Color.R, accent.Color.G, accent.Color.B)),
        };
        Canvas.SetLeft(outline, _moveBox.X);
        Canvas.SetTop(outline, _moveBox.Y);
        _moveFrame.Children.Add(outline);
        if (!PdfAnnotations.CanResize(_moveSel.Kind)) return;
        double s = GripSize / z;
        foreach (var (cx, cy) in new[] { (_moveBox.Left, _moveBox.Top), (_moveBox.Right, _moveBox.Top), (_moveBox.Left, _moveBox.Bottom), (_moveBox.Right, _moveBox.Bottom) })
        {
            var grip = new Shapes.Rectangle { Width = s, Height = s, Fill = new SolidColorBrush(Microsoft.UI.Colors.White), Stroke = accent, StrokeThickness = 1.5 / z };
            Canvas.SetLeft(grip, cx - s / 2);
            Canvas.SetTop(grip, cy - s / 2);
            _moveFrame.Children.Add(grip);
        }
    }

    /// <summary>Qual parte da seleção está sob o ponto: uma alça, o meio ou nada.</summary>
    private MoveGrip GripAt(Point p)
    {
        if (_moveSel is null) return MoveGrip.None;
        var r = _moveBox;
        if (PdfAnnotations.CanResize(_moveSel.Kind))
        {
            double reach = (GripSize / 2 + 3) / Zoom;
            bool Near(double x, double y) => Math.Abs(p.X - x) <= reach && Math.Abs(p.Y - y) <= reach;
            if (Near(r.Left, r.Top)) return MoveGrip.TopLeft;
            if (Near(r.Right, r.Top)) return MoveGrip.TopRight;
            if (Near(r.Left, r.Bottom)) return MoveGrip.BottomLeft;
            if (Near(r.Right, r.Bottom)) return MoveGrip.BottomRight;
        }
        return r.Contains(p) ? MoveGrip.Body : MoveGrip.None;
    }

    private void ClearCommentSelection()
    {
        if (_moveFrame is not null) _moveSurface?.Children.Remove(_moveFrame);
        _moveSurface?.ReleasePointerCaptures();
        _moveFrame = null;
        _moveSel = null;
        _moveSurface = null;
        _moveSlot = null;
        _moveGrip = MoveGrip.None;
        if (_tool?.Mode == DrawMode.MoveComment) ToolHintText.Text = _tool.Hint;
    }
}
