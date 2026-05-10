using System.Windows;
using System.Windows.Input;

namespace Replixer.Views;

public partial class InputWindow : Window
{
    public string? Result { get; private set; }

    public InputWindow(string prompt)
    {
        InitializeComponent();
        PromptText.Text = prompt;
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        if (Owner != null)
        {
            Left   = Owner.Left;
            Top    = Owner.Top;
            Width  = Owner.Width;
            Height = Owner.Height;
        }
        InputBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Confirm();
    private void CancelButton_Click(object sender, RoutedEventArgs e) => Close();
    private void Overlay_MouseDown(object sender, MouseButtonEventArgs e) => Close();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  Confirm();
        if (e.Key == Key.Escape) Close();
    }

    private void Confirm()
    {
        Result = InputBox.Text;
        DialogResult = true;
    }
}
