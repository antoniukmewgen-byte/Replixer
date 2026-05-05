using EchoVault.Views;
using System.Windows;
using System.Windows.Input;

namespace EchoVault;

public partial class MainWindow : Window
{
    private readonly HomePage _homePage = new();
    private readonly RecordingsPage _recordingsPage = new();
    private readonly SettingsPage _settingsPage = new();
    private readonly ProfilePage _profilePage = new();

    public MainWindow()
    {
        InitializeComponent();
        PageContent.Content = _homePage;
    }

    private void Window_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void HomeButton_Click(object sender, RoutedEventArgs e) =>
        PageContent.Content = _homePage;

    private void RecordingsButton_Click(object sender, RoutedEventArgs e) =>
        PageContent.Content = _recordingsPage;

    private void SettingsButton_Click(object sender, RoutedEventArgs e) =>
        PageContent.Content = _settingsPage;

    private void ProfileButton_Click(object sender, RoutedEventArgs e) =>
        PageContent.Content = _profilePage;

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState.Minimized;

    private void CloseButton_Click(object sender, RoutedEventArgs e) =>
        Close();
}
