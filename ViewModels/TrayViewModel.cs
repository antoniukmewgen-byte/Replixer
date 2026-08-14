using Replixer.Infrastructure;
using Replixer.Services.Manager;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Replixer.ViewModels;

public sealed class TrayViewModel : ViewModelBase, IDisposable
{
    private readonly IWindowManager _windowManager;
    private readonly HomeViewModel  _homeVm;

    public event Action<string, string>? BalloonRequested;

    public ICommand OpenCommand      { get; }
    public ICommand RecordCommand    { get; }
    public ICommand WorldClockCommand { get; }
    public ICommand ExitCommand      { get; }

    public string RecordLabel  => _homeVm.IsRecording ? "Зупинити запис" : "Записати дзвінок";
    public bool   IsRecording  => _homeVm.IsRecording;

    public TrayViewModel(IWindowManager windowManager, HomeViewModel homeVm)
    {
        _windowManager = windowManager;
        _homeVm        = homeVm;

        OpenCommand       = new RelayCommand(OpenVault);
        RecordCommand     = new RelayCommand(ToggleRecording);
        WorldClockCommand = new RelayCommand(_windowManager.ToggleWorldClock);
        ExitCommand       = new RelayCommand(() => Application.Current.Shutdown());

        _homeVm.PropertyChanged += OnHomePropertyChanged;
    }

    private void OnHomePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(HomeViewModel.IsRecording))
        {
            OnPropertyChanged(nameof(RecordLabel));
            OnPropertyChanged(nameof(IsRecording));
        }
    }

    private void OpenVault() => _windowManager.ShowMainWindow();

    private void ToggleRecording()
    {
        if (_homeVm.IsRecording)
            _homeVm.ManualStopRecording();
        else
            _homeVm.ManualStartRecording();
    }

    public void ShowBalloon(string title, string message) =>
        BalloonRequested?.Invoke(title, message);

    public void Dispose() => _homeVm.PropertyChanged -= OnHomePropertyChanged;
}
