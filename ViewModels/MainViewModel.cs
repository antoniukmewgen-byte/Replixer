using Replixer.Infrastructure;
using Replixer.ViewModels.Dialogs;
using System.Windows.Input;

namespace Replixer.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDisposable
{
    private ViewModelBase _currentViewModel;
    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetField(ref _currentViewModel, value);
    }

    private CallDialogViewModel? _dialog;
    public CallDialogViewModel? Dialog
    {
        get => _dialog;
        private set => SetField(ref _dialog, value);
    }

    public ICommand NavigateHomeCommand       { get; }
    public ICommand NavigateRecordingsCommand { get; }
    public ICommand NavigateSettingsCommand   { get; }
    public ICommand NavigateProfileCommand    { get; }

    private readonly HomeViewModel _homeVm;
    private readonly SettingsViewModel _settingsVm;

    public MainViewModel(
        HomeViewModel homeVm,
        RecordingsViewModel recordingsVm,
        SettingsViewModel settingsVm,
        ProfileViewModel profileVm)
    {
        _homeVm     = homeVm;
        _settingsVm = settingsVm;

        _homeVm.DialogRequested += vm => Dialog = vm;
        _currentViewModel = _homeVm;

        NavigateHomeCommand       = new RelayCommand(() => CurrentViewModel = _homeVm);
        NavigateRecordingsCommand = new RelayCommand(() => CurrentViewModel = recordingsVm);
        NavigateSettingsCommand   = new RelayCommand(() => CurrentViewModel = _settingsVm);
        NavigateProfileCommand    = new RelayCommand(() => CurrentViewModel = profileVm);
    }

    public void Dispose()
    {
        _homeVm.Dispose();
        _settingsVm.Dispose();
    }
}
