using System.Windows;
using System.Windows.Input;

namespace Replixer.Views;

public partial class MissedCallReminderWindow : Window
{
    public MissedCallReminderWindow() => InitializeComponent();

    // Дозволяємо перетягнути банер, якщо він комусь заважає у стандартному місці,
    // але закрити його з UI неможливо — вікно живе, доки живе застосунок.
    private void Banner_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }
}
