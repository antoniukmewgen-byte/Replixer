using EchoVault.Infrastructure;
using System.Windows.Input;

namespace EchoVault.ViewModels;

public class MainViewModel : ViewModelBase
{
    private ViewModelBase _currentViewModel;

    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetField(ref _currentViewModel, value);
    }

    public ICommand NavigateHomeCommand { get; }
    public ICommand NavigateRecordingsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateProfileCommand { get; }

    private readonly HomeViewModel _homeVm = new();
    private readonly RecordingsViewModel _recordingsVm = new();
    private readonly SettingsViewModel _settingsVm = new();
    private readonly ProfileViewModel _profileVm = new();

    public MainViewModel()
    {
        _currentViewModel = _homeVm;

        NavigateHomeCommand = new RelayCommand(() => CurrentViewModel = _homeVm);
        NavigateRecordingsCommand = new RelayCommand(() => CurrentViewModel = _recordingsVm);
        NavigateSettingsCommand = new RelayCommand(() => CurrentViewModel = _settingsVm);
        NavigateProfileCommand = new RelayCommand(() => CurrentViewModel = _profileVm);
    }
}
