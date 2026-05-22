using Replixer.Infrastructure;
using Replixer.Services.Manager;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;

namespace Replixer.ViewModels;

public sealed class TrayViewModel : ViewModelBase
{
    private readonly IWindowManager _windowManager;
    private readonly HomeViewModel  _homeVm;

    public event Action<string, string>? BalloonRequested;

    public ICommand OpenCommand   { get; }
    public ICommand RecordCommand { get; }
    public ICommand ExitCommand   { get; }

    public string RecordLabel =>
        _homeVm.IsRecording ? "Зупинити запис" : "Записати дзвінок";

    public TrayViewModel(IWindowManager windowManager, HomeViewModel homeVm)
    {
        _windowManager = windowManager;
        _homeVm        = homeVm;

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
}
