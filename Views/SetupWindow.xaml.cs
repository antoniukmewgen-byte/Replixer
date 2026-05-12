using Replixer.ViewModels;
using System.Windows;
using System.Windows.Input;

namespace Replixer.Views;

public partial class SetupWindow : Window
{
    public SetupWindow(SetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
}
