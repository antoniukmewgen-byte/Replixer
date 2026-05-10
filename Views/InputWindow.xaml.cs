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
        Loaded += (_, _) => InputBox.Focus();
    }

    private void OkButton_Click(object sender, RoutedEventArgs e) => Confirm();

    private void InputBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Confirm();
    }

    private void Confirm()
    {
        Result = InputBox.Text;
        DialogResult = true;
    }
}
