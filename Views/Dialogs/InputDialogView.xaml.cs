using System.Windows.Controls;
using System.Windows.Input;

namespace Replixer.Views.Dialogs;

public partial class InputDialogView : UserControl
{
    public InputDialogView()
    {
        InitializeComponent();
        Loaded += (_, _) => InputBox.Focus();
        InputBox.KeyDown += OnKeyDown;
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)  (DataContext as dynamic)?.ConfirmCommand.Execute(null);
        if (e.Key == Key.Escape) (DataContext as dynamic)?.CancelCommand.Execute(null);
    }
}
