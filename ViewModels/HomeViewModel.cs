using EchoVault.Models;
using EchoVault.Services;
using EchoVault.Services.Audio;
using EchoVault.Services.Recording;
using EchoVault.Services.Window;
using EchoVault.Services.Window.Detectors;
using EchoVault.ViewModels.Call;
using EchoVault.ViewModels.Dialogs;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;

namespace EchoVault.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private readonly Action<CallDialogViewModel?> _setDialog;
    private readonly AppSettings _settings;
    private readonly IMonitorService _windowMonitor;
    private readonly IMonitorService _micMonitor;
    private readonly AudioRecordingService _recorder;
    private IMonitorService _activeMonitor;

    private bool _isRecording;
    private bool _hasActiveDialog;
    private string _lastDetectedApp = string.Empty;

    private ViewModelBase _callContent;
    public ViewModelBase CallContent
    {
        get => _callContent;
        private set
        {
            (_callContent as IDisposable)?.Dispose();
            SetField(ref _callContent, value);
        }
    }

    public HomeViewModel(Action<CallDialogViewModel?> setDialog, AppSettings settings)
    {
        _setDialog = setDialog;
        _settings  = settings;
        _recorder  = new AudioRecordingService(settings);

        _callContent = new IdleCallViewModel(StartRecording);

        _windowMonitor = new WindowMonitorService(new ICallDetector[]
        {
            new TelegramCallDetector(),
            new WhatsAppCallDetector(),
            new ViberCallDetector(),
        });
        _micMonitor = new MicrophoneMonitorService();

        _activeMonitor = GetMonitorForMode(_settings.MonitorMode);
        Subscribe(_activeMonitor);
        _activeMonitor.Start();

        _settings.PropertyChanged += OnSettingsChanged;
    }

    // ── Monitor switching ─────────────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.MonitorMode)) return;

        var next = GetMonitorForMode(_settings.MonitorMode);
        if (next == _activeMonitor) return;

        Unsubscribe(_activeMonitor);
        _activeMonitor.Stop();

        _activeMonitor = next;
        Subscribe(_activeMonitor);
        _activeMonitor.Start();
    }

    private IMonitorService GetMonitorForMode(MonitorMode mode)
        => mode == MonitorMode.Microphone ? _micMonitor : _windowMonitor;

    private void Subscribe(IMonitorService monitor)
    {
        monitor.CallDetected += OnCallDetected;
        monitor.CallEnded    += OnCallEnded;
    }

    private void Unsubscribe(IMonitorService monitor)
    {
        monitor.CallDetected -= OnCallDetected;
        monitor.CallEnded    -= OnCallEnded;
    }

    // ── Call events ───────────────────────────────────────────────────────────

    private void OnCallDetected(string app)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _lastDetectedApp = app;

            if (_isRecording) return;
            if (_hasActiveDialog) return;

            ShowDialog(new CallDialogViewModel(
                title: "Виявлено дзвінок",
                message: $"Виявлено дзвінок у {app}. Розпочати запис?",
                primaryLabel:   "Почати запис", onPrimary:   StartRecording,
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
                primaryLabel:   "Завершити запис",    onPrimary:   StopRecording,
                secondaryLabel: "Продовжити запис",   onSecondary: DismissDialog
            ));
        });
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    private void StartRecording()
    {
        DismissDialog();
        _isRecording = true;

        bool started = _recorder.StartRecording(_lastDetectedApp);
        if (!started)
            Debug.WriteLine("[HomeVM] AudioRecordingService failed to start");

        CallContent = new ActiveCallViewModel(StopRecording);
    }

    private async void StopRecording()
    {
        DismissDialog();
        _isRecording = false;

        // Switch UI back to idle immediately — recording finishes in background
        CallContent = new IdleCallViewModel(StartRecording);

        string? path = await _recorder.StopRecordingAsync();

        if (path != null)
            Debug.WriteLine($"[HomeVM] Saved → {path}");
        else
            Debug.WriteLine("[HomeVM] Recording was not saved");
    }

    // ── Dialog helpers ────────────────────────────────────────────────────────

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
}
