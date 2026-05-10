using Replixer.Services.Manager;
using System.Configuration;
using System.Data;
using System.Windows;
using WpfApp = System.Windows.Application;

namespace Replixer
{
    /// <summary>
    /// Interaction logic for App.xaml
    /// </summary>
    public partial class App : WpfApp
    {
        public static IWindowManager WindowManager { get; } = new WindowManager();

        private Hardcodet.Wpf.TaskbarNotification.TaskbarIcon _notifyIcon;
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);

            // Знаходимо іконку в ресурсах та ініціалізуємо її
            _notifyIcon = (Hardcodet.Wpf.TaskbarNotification.TaskbarIcon)FindResource("NotifyIcon");
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // Обов'язково вивільняємо ресурси
            _notifyIcon.Dispose();
            base.OnExit(e);
        }
    }

}
