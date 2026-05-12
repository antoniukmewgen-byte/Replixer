using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace Replixer.Controls;

public class RippleEffect : ContentControl
{
    static RippleEffect()
    {
        DefaultStyleKeyProperty.OverrideMetadata(typeof(RippleEffect),
            new FrameworkPropertyMetadata(typeof(RippleEffect)));
    }

    public static readonly DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(nameof(CornerRadius), typeof(CornerRadius), typeof(RippleEffect),
            new FrameworkPropertyMetadata(default(CornerRadius), FrameworkPropertyMetadataOptions.AffectsArrange));

    public CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    private Canvas? _canvas;

    public override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _canvas = GetTemplateChild("PART_Canvas") as Canvas;
    }

    private static Border? FindFirstBorder(DependencyObject parent)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is Border b && b.BorderBrush is SolidColorBrush)
                return b;
            var found = FindFirstBorder(child);
            if (found != null) return found;
        }
        return null;
    }

    protected override Size ArrangeOverride(Size arrangeBounds)
    {
        var size = base.ArrangeOverride(arrangeBounds);
        var r = CornerRadius.TopLeft;
        Clip = new RectangleGeometry(new Rect(size), r, r);
        return size;
    }

    protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnPreviewMouseLeftButtonDown(e);
        if (_canvas == null) return;

        var pos  = e.GetPosition(_canvas);
        var size = Math.Max(_canvas.ActualWidth, _canvas.ActualHeight) * 2.5;
        var dur  = new Duration(TimeSpan.FromMilliseconds(500));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        var baseColor = (FindFirstBorder(this)?.BorderBrush ?? BorderBrush) is SolidColorBrush scb
            ? scb.Color
            : Colors.White;
        var ellipse = new Ellipse
        {
            Width            = 0,
            Height           = 0,
            Fill             = new SolidColorBrush(Color.FromArgb(40, baseColor.R, baseColor.G, baseColor.B)),
            IsHitTestVisible = false,
        };

        Canvas.SetLeft(ellipse, pos.X);
        Canvas.SetTop(ellipse, pos.Y);
        _canvas.Children.Add(ellipse);

        var fadeOut = new DoubleAnimation(1, 0, dur);
        fadeOut.Completed += (_, _) => _canvas.Children.Remove(ellipse);

        ellipse.BeginAnimation(WidthProperty,       new DoubleAnimation(0, size, dur) { EasingFunction = ease });
        ellipse.BeginAnimation(HeightProperty,      new DoubleAnimation(0, size, dur) { EasingFunction = ease });
        ellipse.BeginAnimation(Canvas.LeftProperty, new DoubleAnimation(pos.X, pos.X - size / 2, dur) { EasingFunction = ease });
        ellipse.BeginAnimation(Canvas.TopProperty,  new DoubleAnimation(pos.Y, pos.Y - size / 2, dur) { EasingFunction = ease });
        ellipse.BeginAnimation(OpacityProperty,     fadeOut);
    }
}
