using Replixer.Infrastructure;
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

    // Holds either CallDialogViewModel or InputDialogViewModel
    private ViewModelBase? _dialog;
    public ViewModelBase? Dialog
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
    private readonly ProfileViewModel _profileVm;
    private CallToastWindow? _toast;

    public MainViewModel(
        HomeViewModel homeVm,
        RecordingsViewModel recordingsVm,
        SettingsViewModel settingsVm,
        ProfileViewModel profileVm)
    {
        _homeVm    = homeVm;
        _settingsVm = settingsVm;
        _profileVm  = profileVm;

        _homeVm.DialogRequested    += OnCallDialogRequested;
        _homeVm.CallReportRequested += OnCallReportRequested;
        _currentViewModel = _homeVm;

        NavigateHomeCommand       = new RelayCommand(() => CurrentViewModel = _homeVm);
        NavigateRecordingsCommand = new RelayCommand(() => CurrentViewModel = recordingsVm);
        NavigateSettingsCommand   = new RelayCommand(() => CurrentViewModel = _settingsVm);
        NavigateProfileCommand    = new RelayCommand(() => CurrentViewModel = profileVm);
    }

    // ── Call dialog (from HomeViewModel) ─────────────────────────────────────

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

    // ── Call report dialog (from HomeViewModel) ───────────────────────────────

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

    // ── Input dialog (from TelegramUploadService) ─────────────────────────────

    public void ShowInputDialog(InputDialogViewModel vm)
    {
        RestoreIfMinimized();
        Dialog = vm;
    }

    public void HideInputDialog() => Dialog = null;

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void RestoreIfMinimized()
    {
        var window = Application.Current.MainWindow;
        if (window is { WindowState: WindowState.Minimized })
        {
            window.WindowState = WindowState.Normal;
            window.Activate();
        }
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _homeVm.DialogRequested     -= OnCallDialogRequested;
        _homeVm.CallReportRequested -= OnCallReportRequested;
        _homeVm.Dispose();
        _settingsVm.Dispose();
        _profileVm.Dispose();
        CloseToast();
    }
}
