using Replixer.Views;
using System.Windows;

namespace Replixer.Services.Manager
{
    public class WindowManager : IWindowManager
    {
        private CallCheatSheetWindow? _cheatSheet;
        private MissedCallReminderWindow? _missedCallReminder;
        private WorldClockWindow? _worldClock;

        public void ShowCheatSheet()
        {
            if (_cheatSheet != null) return;

            _cheatSheet = new CallCheatSheetWindow();

            _cheatSheet.Left = SystemParameters.PrimaryScreenWidth - _cheatSheet.Width - 20;
            _cheatSheet.Top = 50;

            _cheatSheet.Show();
        }

        public void CloseCheatSheet()
        {
            _cheatSheet?.Close();
            _cheatSheet = null;
        }

        public void ShowMainWindow()
        {
            var win = Application.Current.MainWindow;
            if (win is null) return;
            win.Show();
            if (win.WindowState == WindowState.Minimized)
                win.WindowState = WindowState.Normal;
            win.Activate();
        }

        // Викликається один раз при старті застосунку (див. App.xaml.cs). На відміну від
        // ShowCheatSheet/CloseCheatSheet тут немає парного Close-методу: банер навмисно
        // лишається на екрані (Topmost) до самого виходу із застосунку, а не ховається
        // програмно десь у логіці дзвінків.
        public void ShowMissedCallReminder()
        {
            if (_missedCallReminder != null) return;

            _missedCallReminder = new MissedCallReminderWindow();

            _missedCallReminder.Left = (SystemParameters.PrimaryScreenWidth - _missedCallReminder.Width) / 2;
            _missedCallReminder.Top  = 16;

            _missedCallReminder.Show();
        }

        // Перемикач-віджет (як RecordCommand у трей-меню) — перший клік показує, другий ховає.
        // Якщо користувач закрив вікно самостійно кнопкою "✕", _worldClock вже null (див. Closed
        // нижче), тож наступний виклик просто відкриє його заново.
        public void ToggleWorldClock()
        {
            if (_worldClock != null)
            {
                _worldClock.Close();
                return;
            }

            _worldClock = new WorldClockWindow();
            _worldClock.Closed += (_, _) => _worldClock = null;

            _worldClock.Left = 20;
            _worldClock.Top  = 50;

            _worldClock.Show();
        }
    }
}
