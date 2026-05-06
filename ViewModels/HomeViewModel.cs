using EchoVault.Services;
using EchoVault.Services.CallDetectors;
using EchoVault.ViewModels.Call;
using EchoVault.ViewModels.Dialogs;
using System.Windows;

namespace EchoVault.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private readonly WindowMonitorService _monitor;
    private readonly Action<CallDialogViewModel?> _setDialog;
    private bool _isRecording;
    private bool _hasActiveDialog;

    private ViewModelBase _callContent;
    public ViewModelBase CallContent
    {
        get => _callContent;
        private set => SetField(ref _callContent, value);
    }

    public HomeViewModel(Action<CallDialogViewModel?> setDialog)
    {
        _setDialog = setDialog;
        _callContent = new IdleCallViewModel(StartRecording);

        _monitor = new WindowMonitorService(new ICallDetector[]
        {
            new TelegramCallDetector(),
            new WhatsAppCallDetector(),
            new ViberCallDetector(),
        });

        _monitor.CallDetected += OnCallDetected;
        _monitor.CallEnded += OnCallEnded;
        _monitor.Start();
    }

    private void OnCallDetected(string app)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (_isRecording) return;
            if (_hasActiveDialog) return;

            ShowDialog(new CallDialogViewModel(
                title: "Виявлено дзвінок",
                message: $"Виявлено дзвінок у {app}. Розпочати запис?",
                primaryLabel: "Почати запис",   onPrimary: StartRecording,
                secondaryLabel: "Пропустити",   onSecondary: DismissDialog
            ));
        });
    }

    private void OnCallEnded(string app)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_isRecording)
            {
                DismissDialog();
                return;
            }

            if (_hasActiveDialog) return;

            ShowDialog(new CallDialogViewModel(
                title: "Дзвінок завершено",
                message: "Дзвінок завершено. Зупинити запис?",
                primaryLabel: "Завершити запис",    onPrimary: StopRecording,
                secondaryLabel: "Продовжити запис", onSecondary: DismissDialog
            ));
        });
    }

    private void ShowDialog(CallDialogViewModel vm)
    {
        _hasActiveDialog = true;
        _setDialog(vm);
    }

    private void DismissDialog()
    {
        _hasActiveDialog = false;
        _setDialog(null);
    }

    private void StartRecording()
    {
        DismissDialog();
        _isRecording = true;
        CallContent = new ActiveCallViewModel(StopRecording);
    }

    private void StopRecording()
    {
        DismissDialog();
        _isRecording = false;
        CallContent = new IdleCallViewModel(StartRecording);
    }
}
