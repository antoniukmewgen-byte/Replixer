using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Replixer.Views.Controls;

// A small "drum"/wheel-style numeric picker: shows the current value with the
// previous/next value dimmed above and below, and changes value on mouse-wheel scroll
// (wraps around between Minimum and Maximum). Used instead of free-text time entry.
public partial class NumberWheel : UserControl
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(
        nameof(Value), typeof(int), typeof(NumberWheel),
        new FrameworkPropertyMetadata(0, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnValueChanged));

    public static readonly DependencyProperty MinimumProperty = DependencyProperty.Register(
        nameof(Minimum), typeof(int), typeof(NumberWheel), new PropertyMetadata(0, OnRangeChanged));

    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(
        nameof(Maximum), typeof(int), typeof(NumberWheel), new PropertyMetadata(59, OnRangeChanged));

    public int Value
    {
        get => (int)GetValue(ValueProperty);
        set => SetValue(ValueProperty, value);
    }

    public int Minimum
    {
        get => (int)GetValue(MinimumProperty);
        set => SetValue(MinimumProperty, value);
    }

    public int Maximum
    {
        get => (int)GetValue(MaximumProperty);
        set => SetValue(MaximumProperty, value);
    }

    public NumberWheel()
    {
        InitializeComponent();
        MouseWheel += OnMouseWheel;
        Loaded += (_, _) => UpdateDisplay();
    }

    private static void OnValueChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumberWheel)d).UpdateDisplay();

    private static void OnRangeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        => ((NumberWheel)d).UpdateDisplay();

    private void OnMouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        Value = Wrap(Value + (e.Delta > 0 ? -1 : 1));
    }

    private int Wrap(int value)
    {
        var range = Maximum - Minimum + 1;
        if (range <= 0) return Minimum;
        return Minimum + ((value - Minimum) % range + range) % range;
    }

    private void UpdateDisplay()
    {
        if (PrevText is null) return;

        PrevText.Text    = Wrap(Value - 1).ToString("00");
        CurrentText.Text = Value.ToString("00");
        NextText.Text    = Wrap(Value + 1).ToString("00");
    }
}
