using Replixer.Views;
using System.Windows;

namespace Replixer.Services.Manager
{
    public class WindowManager : IWindowManager
    {
        private CallCheatSheetWindow? _cheatSheet;

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
    }
}
