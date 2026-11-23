using Microsoft.UI;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace PDFStudio.Pages;

/// <summary>
/// Régua (Ver → Régua): fica por cima do documento, só para medir e alinhar — não grava nada no PDF. Marca
/// centímetros em cima e polegadas embaixo, no tamanho real do documento (acompanha o zoom). Arraste para mover;
/// puxe a bolinha da ponta para girar (Shift: de 15 em 15°); a roda do mouse sobre ela gira de 1 em 1°.
/// </summary>
public sealed partial class ViewerPage
{
    private const double RulerHeight = 96;
    private const double PointsPerCm = 72 / 2.54;

    private Canvas? _rulerLayer;
    private Grid? _ruler;
    private Canvas? _rulerTicks;
    private TextBlock? _rulerAngleText;
    private readonly CompositeTransform _rulerTransform = new();
    private Point _rulerCenter;
    private double _rulerAngle;
    private double _rulerBuiltFor;

    // Arraste: mover (Rotate = false) ou girar.
    private bool _rulerDragging, _rulerRotating;
    private Point _rulerPressPoint, _rulerPressCenter;
    private double _rulerPressAngle, _rulerPressPointerAngle;

    private bool RulerVisible => _ruler is { Visibility: Visibility.Visible };

    private void ToggleRuler()
    {
        if (_ruler is null) CreateRuler();
        else _ruler.Visibility = RulerVisible ? Visibility.Collapsed : Visibility.Visible;
        if (!RulerVisible) return;
        UpdateRulerScale(force: true);
        ShowStatus(Microsoft.UI.Xaml.Controls.InfoBarSeverity.Informational, "Régua",
            "Arraste para mover. Puxe a bolinha da ponta para girar (Shift: de 15 em 15°); a roda do mouse sobre ela gira de 1 em 1°. Não muda o PDF.");
    }

    private void CreateRuler()
    {
        _rulerLayer = new Canvas(); // sem fundo: o resto da tela continua recebendo o mouse
        DocumentArea.Children.Add(_rulerLayer);

        _rulerTicks = new Canvas { IsHitTestVisible = false };
        _rulerAngleText = new TextBlock
        {
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 40, 40, 40)),
            VerticalAlignment = VerticalAlignment.Center,
            MinWidth = 36,
            TextAlignment = TextAlignment.Center,
        };
        var level = RulerButton("", "Deixar reta (0°)", "RulerResetButton");
        level.Click += (_, _) => SetRulerAngle(0);
        var turn = RulerButton("", "Girar 90°", "RulerTurnButton");
        turn.Click += (_, _) => SetRulerAngle(Math.Round((_rulerAngle + 90) / 90) * 90);
        var close = RulerButton("", "Fechar a régua", "RulerCloseButton");
        close.Click += (_, _) => { if (_ruler is not null) _ruler.Visibility = Visibility.Collapsed; };
        var middle = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 2,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _rulerAngleText, level, turn, close },
        };

        // Bolinha de girar, na ponta direita.
        var handle = new Border
        {
            Width = 22,
            Height = 22,
            CornerRadius = new CornerRadius(11),
            Background = new SolidColorBrush(ColorHelper.FromArgb(255, 47, 91, 234)),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
            Child = new FontIcon { Glyph = "", FontSize = 12, Foreground = new SolidColorBrush(Colors.White) },
        };
        ToolTipService.SetToolTip(handle, "Puxe para girar (Shift: de 15 em 15°)");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(handle, "RulerRotateHandle");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(handle, "Girar a régua");
        handle.PointerPressed += (_, e) => StartRulerGesture(e, rotate: true);

        _ruler = new Grid
        {
            Height = RulerHeight,
            Background = new SolidColorBrush(ColorHelper.FromArgb(225, 255, 244, 196)),
            BorderBrush = new SolidColorBrush(ColorHelper.FromArgb(255, 150, 130, 70)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            RenderTransform = _rulerTransform,
            Children = { _rulerTicks, middle, handle },
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(_ruler, "Ruler");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_ruler, "Régua");
        _ruler.PointerPressed += (_, e) => StartRulerGesture(e, rotate: false);
        _ruler.PointerMoved += Ruler_PointerMoved;
        _ruler.PointerReleased += (_, e) => EndRulerGesture(e);
        _ruler.PointerCanceled += (_, e) => EndRulerGesture(e);
        _ruler.PointerCaptureLost += (_, _) => { _rulerDragging = _rulerRotating = false; };
        _ruler.PointerWheelChanged += Ruler_PointerWheelChanged;
        _rulerLayer.Children.Add(_ruler);

        // Começa no meio da área do documento.
        _rulerCenter = new Point(Math.Max(200, DocumentArea.ActualWidth / 2), Math.Max(120, DocumentArea.ActualHeight / 3));
        Scroller.ViewChanged += (_, _) => UpdateRulerScale();
        SetRulerAngle(0);
    }

    private static Button RulerButton(string glyph, string tip, string id)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12, Foreground = new SolidColorBrush(ColorHelper.FromArgb(255, 40, 40, 40)) },
            Padding = new Thickness(6, 4, 6, 4),
            MinWidth = 0,
            MinHeight = 0,
            Background = new SolidColorBrush(ColorHelper.FromArgb(40, 0, 0, 0)),
            BorderThickness = new Thickness(0),
        };
        ToolTipService.SetToolTip(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(button, tip);
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetAutomationId(button, id);
        return button;
    }

    /// <summary>Medidas da régua no zoom atual: um centímetro do documento ocupa quantos pontos da tela.</summary>
    private void UpdateRulerScale(bool force = false)
    {
        if (_ruler is null || _rulerTicks is null || !RulerVisible) return;
        double perCm = PointsPerCm * DipsPerPoint * Zoom;
        if (!force && Math.Abs(perCm - _rulerBuiltFor) < 0.01) return;
        _rulerBuiltFor = perCm;

        // Cabe na área do documento (com folga), entre 3 e 30 cm.
        double room = Math.Max(300, DocumentArea.ActualWidth * 0.8);
        int cms = (int)Math.Clamp(Math.Floor(room / perCm), 3, 30);
        double length = cms * perCm + 40; // folga: 12 no começo, 28 no fim (para o último número)
        _ruler.Width = length;
        _rulerTicks.Children.Clear();
        var ink = new SolidColorBrush(ColorHelper.FromArgb(255, 40, 40, 40));
        const double start = 12;

        // Em cima: centímetros (traço grande), meio centímetro e milímetros (quando cabem).
        int mmStep = perCm / 10 >= 3 ? 1 : perCm / 10 >= 1.2 ? 5 : 10;
        int labelEvery = perCm >= 22 ? 1 : perCm >= 11 ? 2 : 5;
        for (int mm = 0; mm <= cms * 10; mm += mmStep)
        {
            double x = start + mm * perCm / 10;
            double h = mm % 10 == 0 ? 18 : mm % 5 == 0 ? 12 : 7;
            AddTick(x, 0, h, ink);
            if (mm % 10 == 0 && mm / 10 % labelEvery == 0) AddLabel(mm == 0 ? "0 cm" : (mm / 10).ToString(), x, 19, ink, top: true);
        }

        // Embaixo: polegadas (1/8, 1/4, 1/2 e inteira).
        double perInch = 72 * DipsPerPoint * Zoom;
        int inches = (int)Math.Floor((length - 2 * start) / perInch);
        int eighthStep = perInch / 8 >= 4 ? 1 : perInch / 8 >= 2 ? 2 : 8;
        for (int e = 0; e <= inches * 8; e += eighthStep)
        {
            double x = start + e * perInch / 8;
            double h = e % 8 == 0 ? 16 : e % 4 == 0 ? 11 : e % 2 == 0 ? 8 : 5;
            AddTick(x, RulerHeight - 2 - h, h, ink);
            if (e % 8 == 0) AddLabel(e == 0 ? "0 pol" : (e / 8).ToString(), x, RulerHeight - 2 - 16 - 15, ink, top: false);
        }

        _rulerTransform.CenterX = length / 2;
        _rulerTransform.CenterY = RulerHeight / 2;
        PlaceRuler();
    }

    private void AddTick(double x, double y, double h, Brush ink)
    {
        var line = new Microsoft.UI.Xaml.Shapes.Rectangle { Width = 1, Height = h, Fill = ink };
        Canvas.SetLeft(line, x - 0.5);
        Canvas.SetTop(line, y);
        _rulerTicks!.Children.Add(line);
    }

    private void AddLabel(string text, double x, double y, Brush ink, bool top)
    {
        var label = new TextBlock { Text = text, FontSize = 10, Foreground = ink };
        Canvas.SetLeft(label, x + 2);
        Canvas.SetTop(label, y);
        _rulerTicks!.Children.Add(label);
    }

    private void PlaceRuler()
    {
        if (_ruler is null) return;
        Canvas.SetLeft(_ruler, _rulerCenter.X - _ruler.Width / 2);
        Canvas.SetTop(_ruler, _rulerCenter.Y - RulerHeight / 2);
    }

    private void SetRulerAngle(double degrees)
    {
        degrees %= 360;
        if (degrees > 180) degrees -= 360;
        if (degrees <= -180) degrees += 360;
        _rulerAngle = degrees;
        _rulerTransform.Rotation = degrees;
        if (_rulerAngleText is not null) _rulerAngleText.Text = $"{Math.Round(degrees):0}°";
    }

    private void StartRulerGesture(PointerRoutedEventArgs e, bool rotate)
    {
        if (_ruler is null || _rulerLayer is null || e.Handled) return;
        if (e.OriginalSource is FrameworkElement fe && FindParent<Button>(fe) is not null) return; // botões da régua
        e.Handled = true;
        _ruler.CapturePointer(e.Pointer);
        _rulerPressPoint = e.GetCurrentPoint(_rulerLayer).Position;
        _rulerPressCenter = _rulerCenter;
        _rulerPressAngle = _rulerAngle;
        _rulerPressPointerAngle = AngleTo(_rulerPressPoint);
        _rulerRotating = rotate;
        _rulerDragging = !rotate;
    }

    private double AngleTo(Point p) => Math.Atan2(p.Y - _rulerCenter.Y, p.X - _rulerCenter.X) * 180 / Math.PI;

    private void Ruler_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_rulerLayer is null || !(_rulerDragging || _rulerRotating)) return;
        e.Handled = true;
        var p = e.GetCurrentPoint(_rulerLayer).Position;
        if (_rulerDragging)
        {
            _rulerCenter = new Point(_rulerPressCenter.X + p.X - _rulerPressPoint.X, _rulerPressCenter.Y + p.Y - _rulerPressPoint.Y);
            PlaceRuler();
            return;
        }
        double angle = _rulerPressAngle + AngleTo(p) - _rulerPressPointerAngle;
        if (e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift)) angle = Math.Round(angle / 15) * 15;
        SetRulerAngle(angle);
    }

    private void EndRulerGesture(PointerRoutedEventArgs e)
    {
        if (!(_rulerDragging || _rulerRotating)) return;
        e.Handled = true;
        _rulerDragging = _rulerRotating = false;
        _ruler?.ReleasePointerCaptures();
    }

    private void Ruler_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        e.Handled = true; // a roda gira a régua em vez de rolar o documento
        int delta = e.GetCurrentPoint(_ruler).Properties.MouseWheelDelta;
        double step = e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Shift) ? 15 : 1;
        double angle = _rulerAngle + Math.Sign(delta) * step;
        if (step == 15) angle = Math.Round(angle / 15) * 15;
        SetRulerAngle(angle);
    }

    private static T? FindParent<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t) return t;
            d = VisualTreeHelper.GetParent(d);
        }
        return null;
    }
}
