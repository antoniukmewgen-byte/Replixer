using Replixer.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Replixer;

public partial class MainWindow : Window
{
    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();

        vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = vm;

        Closing += (_, e) => { e.Cancel = true; Hide(); };

        PreviewMouseDown += (_, _) =>
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
        };

        MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentViewModel)) return;

        var button = ((MainViewModel)DataContext).CurrentViewModel switch
        {
            HomeViewModel       => HomeButton,
            RecordingsViewModel => RecordingsButton,
            SettingsViewModel   => SettingsButton,
            ProfileViewModel    => ProfileButton,
            _                   => null
        };

        if (button is null) return;

        var targetY = button.TransformToAncestor(SidebarGrid)
                            .Transform(new Point(0, 0)).Y;

        var animation = new DoubleAnimation(targetY, TimeSpan.FromMilliseconds(250))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut }
        };
        IndicatorTranslate.BeginAnimation(TranslateTransform.YProperty, animation);
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void FullscreenButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Hide();
}
