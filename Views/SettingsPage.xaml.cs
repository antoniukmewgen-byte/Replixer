using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace EchoVault.Views;

public partial class SettingsPage : UserControl
{
    public SettingsPage()
    {
        InitializeComponent();
    }

    private void Root_MouseDown(object sender, MouseButtonEventArgs e)
    {
        Keyboard.ClearFocus();
        FocusManager.SetFocusedElement(FocusManager.GetFocusScope(this), null);
    }
}
