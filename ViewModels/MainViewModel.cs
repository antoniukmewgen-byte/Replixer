using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services.Upload;
using Replixer.ViewModels.Dialogs;
using System.Windows.Input;

namespace Replixer.ViewModels;

public class MainViewModel : ViewModelBase
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

    public ICommand NavigateHomeCommand { get; }
    public ICommand NavigateRecordingsCommand { get; }
    public ICommand NavigateSettingsCommand { get; }
    public ICommand NavigateProfileCommand { get; }

    private readonly HomeViewModel _homeVm;
    private readonly RecordingsViewModel _recordingsVm = new();
    private readonly SettingsViewModel _settingsVm;
    private readonly ProfileViewModel _profileVm = new();

    public MainViewModel()
    {
        var settings  = AppSettings.Load();
        var uploader  = new GoogleDriveUploadService();
        var telegram  = new TelegramUploadService(settings);

        _homeVm     = new HomeViewModel(dialog => Dialog = dialog, settings, uploader, telegram, _recordingsVm);
        _settingsVm = new SettingsViewModel(settings, uploader, telegram);

        _currentViewModel = _homeVm;

        NavigateHomeCommand       = new RelayCommand(() => CurrentViewModel = _homeVm);
        NavigateRecordingsCommand = new RelayCommand(() => CurrentViewModel = _recordingsVm);
        NavigateSettingsCommand   = new RelayCommand(() => CurrentViewModel = _settingsVm);
        NavigateProfileCommand    = new RelayCommand(() => CurrentViewModel = _profileVm);
    }
}
