using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Upload;
using Replixer.ViewModels.Dialogs;
using Replixer.Views;
using System.Windows;
using System.Windows.Input;

namespace Replixer.ViewModels;

public sealed class MainViewModel : ViewModelBase, IDialogHost, IDisposable
{
    private ViewModelBase _currentViewModel;
    public ViewModelBase CurrentViewModel
    {
        get => _currentViewModel;
        private set => SetField(ref _currentViewModel, value);
    }

    private ViewModelBase? _dialog;
    public ViewModelBase? Dialog
    {
        get => _dialog;
        private set => SetField(ref _dialog, value);
    }

    private bool _updateAvailable;
    public bool UpdateAvailable
    {
        get => _updateAvailable;
        private set => SetField(ref _updateAvailable, value);
    }

    public ICommand NavigateHomeCommand       { get; }
    public ICommand NavigateRecordingsCommand { get; }
    public ICommand NavigateSettingsCommand   { get; }
    public ICommand NavigateProfileCommand    { get; }
    public ICommand CheckForUpdatesCommand    { get; }

    private readonly HomeViewModel        _homeVm;
    private readonly SettingsViewModel    _settingsVm;
    private readonly ProfileViewModel     _profileVm;
    private readonly TelegramUploadService _telegram;
    private readonly UpdateService        _updateService;
    private readonly AppSettings          _settings;
    private CallToastWindow? _toast;
    private UpdateInfo?      _pendingUpdate;

    public NotificationsViewModel Notifications { get; }

    public MainViewModel(
        HomeViewModel          homeVm,
        RecordingsViewModel    recordingsVm,
        SettingsViewModel      settingsVm,
        ProfileViewModel       profileVm,
        TelegramUploadService  telegram,
        NotificationsViewModel notifications,
        UpdateService          updateService,
        AppSettings            settings)
    {
        _homeVm        = homeVm;
        _settingsVm    = settingsVm;
        _profileVm     = profileVm;
        _telegram      = telegram;
        _updateService = updateService;
        _settings      = settings;
        Notifications  = notifications;

        _telegram.InputHandler = HandleTelegramInputAsync;

        _homeVm.DialogRequested     += OnCallDialogRequested;
        _homeVm.CallReportRequested += OnCallReportRequested;
        _currentViewModel = _homeVm;

        NavigateHomeCommand       = new RelayCommand(() => CurrentViewModel = _homeVm);
        NavigateRecordingsCommand = new RelayCommand(() => CurrentViewModel = recordingsVm);
        NavigateSettingsCommand   = new RelayCommand(() => CurrentViewModel = _settingsVm);
        NavigateProfileCommand    = new RelayCommand(() => CurrentViewModel = _profileVm);
        CheckForUpdatesCommand    = new AsyncRelayCommand(CheckAndShowUpdateAsync);
    }

    public async Task StartupUpdateCheckAsync()
    {
        _pendingUpdate = await _updateService.CheckForUpdateAsync();
        if (_pendingUpdate is null) return;

        UpdateAvailable = true;

        if (_settings.UpdateDismissedDate?.Date == DateTime.Today) return;

        ShowUpdateDialog(_pendingUpdate);
    }

    private async Task CheckAndShowUpdateAsync()
    {
        _pendingUpdate ??= await _updateService.CheckForUpdateAsync();

        if (_pendingUpdate is null)
        {
            NotificationService.ShowSuccess("Встановлена остання версія програми.");
            return;
        }

        UpdateAvailable = true;
        ShowUpdateDialog(_pendingUpdate);
    }

    private void ShowUpdateDialog(UpdateInfo info)
    {
        var vm = new UpdateDialogViewModel(_updateService, info, onDismiss: () =>
        {
            _settings.UpdateDismissedDate = DateTime.Today;
            Dialog = null;
        });
        RestoreIfMinimized();
        Dialog = vm;
    }

    private void OnCallDialogRequested(CallDialogViewModel? vm)
    {
        if (vm is null)
        {
            if (Dialog is CallDialogViewModel) Dialog = null;
            CloseToast();
            return;
        }

        RestoreIfMinimized();

        if (Application.Current.MainWindow?.IsVisible == true)
            Dialog = vm;
        else
            ShowToast(vm);
    }

    private void ShowToast(CallDialogViewModel vm)
    {
        CloseToast();
        _toast = new CallToastWindow(vm);
        _toast.Show();
    }

    private void CloseToast()
    {
        _toast?.Close();
        _toast = null;
    }

    private void OnCallReportRequested(CallReportViewModel? vm)
    {
        if (vm is null)
        {
            if (Dialog is CallReportViewModel) Dialog = null;
            return;
        }
        RestoreIfMinimized();
        Dialog = vm;
    }

    public void ShowInputDialog(InputDialogViewModel vm)
    {
        RestoreIfMinimized();
        Dialog = vm;
    }

    public void HideInputDialog() => Dialog = null;

    private Task<string?> HandleTelegramInputAsync(string prompt)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        RestoreIfMinimized();
        var vm = new InputDialogViewModel(prompt, result =>
        {
            HideInputDialog();
            tcs.TrySetResult(result);
        });
        ShowInputDialog(vm);
        return tcs.Task;
    }

    private static void RestoreIfMinimized()
    {
        var window = Application.Current.MainWindow;
        if (window is { WindowState: WindowState.Minimized })
        {
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    public void Dispose()
    {
        _telegram.InputHandler = null;
        _homeVm.DialogRequested     -= OnCallDialogRequested;
        _homeVm.CallReportRequested -= OnCallReportRequested;
        _homeVm.Dispose();
        _settingsVm.Dispose();
        _profileVm.Dispose();
        CloseToast();
    }
}
