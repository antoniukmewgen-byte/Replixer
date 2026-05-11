using Replixer.Infrastructure;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Replixer.ViewModels;

public sealed class TrayViewModel : ViewModelBase
{
    private readonly MainWindow  _mainWindow;
    private readonly HomeViewModel _homeVm;

    public event Action<string, string>? BalloonRequested;

    public ICommand OpenCommand   { get; }
    public ICommand RecordCommand { get; }
    public ICommand ExitCommand   { get; }

    public string RecordLabel =>
        _homeVm.IsRecording ? "Зупинити запис" : "Записати дзвінок";

    public TrayViewModel(MainWindow mainWindow, HomeViewModel homeVm)
    {
        _mainWindow = mainWindow;
        _homeVm     = homeVm;

        OpenCommand   = new RelayCommand(OpenVault);
        RecordCommand = new RelayCommand(ToggleRecording);
        ExitCommand   = new RelayCommand(() => Application.Current.Shutdown());

        _homeVm.PropertyChanged += OnHomePropertyChanged;
    }

    private void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.IsRecording))
            OnPropertyChanged(nameof(RecordLabel));
    }

    private void OpenVault()
    {
        _mainWindow.Show();
        if (_mainWindow.WindowState == WindowState.Minimized)
            _mainWindow.WindowState = WindowState.Normal;
        _mainWindow.Activate();
    }

    private void ToggleRecording()
    {
        if (_homeVm.IsRecording)
            _homeVm.ManualStopRecording();
        else
            _homeVm.ManualStartRecording();
    }

    public void ShowBalloon(string title, string message) =>
        BalloonRequested?.Invoke(title, message);
}
