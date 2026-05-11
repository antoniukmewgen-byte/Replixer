using Replixer.ViewModels.Dialogs;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Replixer.Views;

public partial class CallToastWindow : Window
{
    public CallToastWindow(CallDialogViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var workArea = SystemParameters.WorkArea;
        Left = workArea.Right  - ActualWidth  - 12;
        Top  = workArea.Bottom - ActualHeight - 24;

        var slide = new DoubleAnimation(16, 0, TimeSpan.FromMilliseconds(300))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(220));

        SlideTransform.BeginAnimation(TranslateTransform.YProperty, slide);
        BeginAnimation(OpacityProperty, fade);
    }
}
