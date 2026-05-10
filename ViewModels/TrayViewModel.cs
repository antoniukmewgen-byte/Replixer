using Replixer.Infrastructure;
using System.Windows;
using System.Windows.Input;

namespace Replixer.ViewModels
{
    public class TrayViewModel : ViewModelBase
    {
        public ICommand RecordCommand { get; }
        public ICommand OpenVaultCommand { get; }
        public ICommand ExitCommand { get; }

        public TrayViewModel()
        {
            RecordCommand = new RelayCommand(StartRecording);
            OpenVaultCommand = new RelayCommand(OpenVault);
            ExitCommand = new RelayCommand(ExitApp);
        }

        private void StartRecording()
        {

        }

        private void OpenVault()
        {
            var mainWindow = Application.Current.MainWindow;
            if (mainWindow != null)
            {
                mainWindow.Show();
                mainWindow.WindowState = WindowState.Normal;
                mainWindow.Activate();
            }
        }

        private void ExitApp()
        {
            Application.Current.Shutdown();
        }
    }
}
