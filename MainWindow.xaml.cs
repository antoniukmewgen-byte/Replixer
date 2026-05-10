using Replixer.ViewModels;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Replixer;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        this.Closing += MainWindow_Closing;
        var vm = new MainViewModel();
        vm.PropertyChanged += OnViewModelPropertyChanged;
        DataContext = vm;

        PreviewMouseDown += (_, _) =>
        {
            Keyboard.ClearFocus();
            FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
        };

        this.MaxHeight = SystemParameters.MaximizedPrimaryScreenHeight;
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.CurrentViewModel)) return;

        var button = ((MainViewModel)DataContext).CurrentViewModel switch
        {
            HomeViewModel => HomeButton,
            RecordingsViewModel => RecordingsButton,
            SettingsViewModel => SettingsButton,
            ProfileViewModel => ProfileButton,
            _ => null
        };

        if (button is null) return;

        var targetY = button.TransformToAncestor(SidebarGrid)
                            .Transform(new Point(0, 0)).Y;
        AnimateIndicator(targetY);
    }

    private void AnimateIndicator(double toY)
    {
        var animation = new DoubleAnimation(toY, TimeSpan.FromMilliseconds(250))
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

    private void FullscreenButton_Click(object sender, RoutedEventArgs e)
    {
        if (this.WindowState == WindowState.Maximized)
        {
            this.WindowState = WindowState.Normal;
        }
        else
        {
            this.WindowState = WindowState.Maximized;
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Hide();

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        e.Cancel = true;
        this.Hide();
    }
}
