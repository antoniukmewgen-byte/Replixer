using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Replixer.Views;

// Повноекранний оверлей для ручного вибору області скріна месенджера. Показується поверх уже
// гарантовано foreground-вікна месенджера (див. MissedCallReportViewModel.OpenMessengerAsync):
// рамка вибору стартує з фактичних меж вікна месенджера (GetWindowRect), а менеджер може її
// підкоригувати (лише потягнути за кутові/бокові ручки — саму рамку цілком не перетягуємо,
// щоб клік усередині "дірки" не сприймався як "посунути вікно"), перш ніж підтвердити кнопкою
// "Зробити скрін". Корінний Grid НЕ click-through — це навмисно: клік будь-де на цьому
// вікні (навіть над "діркою", де видно сам месенджер) перехоплюється нами, тож поки оверлей
// показаний, користувач фізично не може взаємодіяти з жодним іншим вікном (і з самим месенджером
// теж — щоб випадково не написати щось у чат під час підбору рамки).
public partial class ScreenshotSelectionWindow : Window
{
    private enum DragMode { None, ResizeNW, ResizeN, ResizeNE, ResizeE, ResizeSE, ResizeS, ResizeSW, ResizeW }

    private const double MinSize = 40;

    private readonly Int32Rect _initialScreenRect;
    private double _dpiX = 1.0;
    private double _dpiY = 1.0;

    private Rect _selection;
    private DragMode _dragMode = DragMode.None;
    private Point _dragStartMouse;
    private Rect _dragStartRect;

    // Результат у фізичних пікселях віртуального екрана (та сама система координат, що й
    // GetWindowRect/BitBlt) — заповнюється лише якщо користувач підтвердив вибір кнопкою.
    public Int32Rect? ResultRect { get; private set; }

    public ScreenshotSelectionWindow(Int32Rect initialScreenRect)
    {
        InitializeComponent();
        _initialScreenRect = initialScreenRect;

        Left   = SystemParameters.VirtualScreenLeft;
        Top    = SystemParameters.VirtualScreenTop;
        Width  = SystemParameters.VirtualScreenWidth;
        Height = SystemParameters.VirtualScreenHeight;

        Loaded    += OnLoaded;
        MouseMove += OnWindowMouseMove;
        MouseLeftButtonUp += OnWindowMouseUp;
    }

    // Зручний точковий виклик: показує оверлей модально і повертає обрану область (у фізичних
    // пікселях) або null, якщо користувач натиснув "Скасувати"/Esc/закрив вікно інакше.
    public static Int32Rect? Show(Int32Rect initialScreenRect)
    {
        var window = new ScreenshotSelectionWindow(initialScreenRect);
        window.ShowDialog();
        return window.ResultRect;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        _dpiX = dpi.DpiScaleX;
        _dpiY = dpi.DpiScaleY;

        var x = _initialScreenRect.X / _dpiX - Left;
        var y = _initialScreenRect.Y / _dpiY - Top;
        var w = _initialScreenRect.Width  / _dpiX;
        var h = _initialScreenRect.Height / _dpiY;

        _selection = ClampToWindow(new Rect(x, y, Math.Max(MinSize, w), Math.Max(MinSize, h)));
        UpdateVisuals();

        Focus();
    }

    private void OnWindowKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            OnCancelClick(sender, new RoutedEventArgs());
    }

    private void OnResizeHandleDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } &&
            Enum.TryParse<DragMode>("Resize" + tag, out var mode))
        {
            StartDrag(mode, e);
        }
    }

    private void StartDrag(DragMode mode, MouseButtonEventArgs e)
    {
        _dragMode       = mode;
        _dragStartMouse = e.GetPosition(this);
        _dragStartRect  = _selection;
        Mouse.Capture(this);
        e.Handled = true;
    }

    private void OnWindowMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragMode == DragMode.None) return;

        var pos = e.GetPosition(this);
        var dx  = pos.X - _dragStartMouse.X;
        var dy  = pos.Y - _dragStartMouse.Y;

        double left = _dragStartRect.Left, top = _dragStartRect.Top;
        double right = _dragStartRect.Right, bottom = _dragStartRect.Bottom;

        switch (_dragMode)
        {
            case DragMode.ResizeNW: left += dx; top += dy; break;
            case DragMode.ResizeN:  top += dy; break;
            case DragMode.ResizeNE: right += dx; top += dy; break;
            case DragMode.ResizeE:  right += dx; break;
            case DragMode.ResizeSE: right += dx; bottom += dy; break;
            case DragMode.ResizeS:  bottom += dy; break;
            case DragMode.ResizeSW: left += dx; bottom += dy; break;
            case DragMode.ResizeW:  left += dx; break;
        }

        // Мінімальний розмір — не даємо "звернути" рамку в точку через протилежний край.
        if (right - left < MinSize)
        {
            if (_dragMode is DragMode.ResizeNW or DragMode.ResizeW or DragMode.ResizeSW) left = right - MinSize;
            else right = left + MinSize;
        }
        if (bottom - top < MinSize)
        {
            if (_dragMode is DragMode.ResizeNW or DragMode.ResizeN or DragMode.ResizeNE) top = bottom - MinSize;
            else bottom = top + MinSize;
        }

        var proposed = new Rect(left, top, right - left, bottom - top);
        _selection = ClampToWindow(proposed);
        UpdateVisuals();
    }

    private void OnWindowMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragMode == DragMode.None) return;
        _dragMode = DragMode.None;
        Mouse.Capture(null);
    }

    private Rect ClampToWindow(Rect r)
    {
        var maxW = ActualWidth  > 0 ? ActualWidth  : Width;
        var maxH = ActualHeight > 0 ? ActualHeight : Height;

        var left   = Math.Max(0, r.Left);
        var top    = Math.Max(0, r.Top);
        var right  = Math.Min(maxW, r.Right);
        var bottom = Math.Min(maxH, r.Bottom);
        return new Rect(left, top, Math.Max(MinSize, right - left), Math.Max(MinSize, bottom - top));
    }

    private void UpdateVisuals()
    {
        OuterGeometry.Rect = new Rect(0, 0, ActualWidth > 0 ? ActualWidth : Width, ActualHeight > 0 ? ActualHeight : Height);
        HoleGeometry.Rect  = _selection;

        SelectionBorder.Width  = _selection.Width;
        SelectionBorder.Height = _selection.Height;
        SelectionBorder.Margin = new Thickness(_selection.Left, _selection.Top, 0, 0);

        PositionHandle(HandleNW, _selection.Left,                     _selection.Top);
        PositionHandle(HandleN,  _selection.Left + _selection.Width / 2, _selection.Top);
        PositionHandle(HandleNE, _selection.Right,                    _selection.Top);
        PositionHandle(HandleE,  _selection.Right,                    _selection.Top + _selection.Height / 2);
        PositionHandle(HandleSE, _selection.Right,                    _selection.Bottom);
        PositionHandle(HandleS,  _selection.Left + _selection.Width / 2, _selection.Bottom);
        PositionHandle(HandleSW, _selection.Left,                     _selection.Bottom);
        PositionHandle(HandleW,  _selection.Left,                     _selection.Top + _selection.Height / 2);

        // Панель кнопок — під рамкою; якщо не влазить знизу (рамка впритул до низу екрана),
        // показуємо над рамкою натомість.
        const double panelHeight = 44;
        const double gap = 12;
        var maxH = ActualHeight > 0 ? ActualHeight : Height;

        double panelTop = _selection.Bottom + gap + panelHeight <= maxH
            ? _selection.Bottom + gap
            : Math.Max(0, _selection.Top - gap - panelHeight);

        ActionPanel.Margin = new Thickness(_selection.Left, panelTop, 0, 0);
    }

    private static void PositionHandle(FrameworkElement handle, double centerX, double centerY)
    {
        handle.Margin = new Thickness(centerX - handle.Width / 2, centerY - handle.Height / 2, 0, 0);
    }

    private void OnCaptureClick(object sender, RoutedEventArgs e)
    {
        var physX = (int)Math.Round((_selection.X + Left) * _dpiX);
        var physY = (int)Math.Round((_selection.Y + Top)  * _dpiY);
        var physW = (int)Math.Round(_selection.Width  * _dpiX);
        var physH = (int)Math.Round(_selection.Height * _dpiY);

        ResultRect  = new Int32Rect(physX, physY, physW, physH);
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        ResultRect  = null;
        DialogResult = false;
        Close();
    }
}
