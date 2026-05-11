using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Audio;
using Replixer.Services.Recording;
using Replixer.Services.Upload;
using Replixer.Services.Window;
using Replixer.Services.Window.Detectors;
using Replixer.ViewModels.Call;
using Replixer.ViewModels.Dialogs;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using RecordingStatus = Replixer.Models.RecordingStatus;

namespace Replixer.ViewModels;

public sealed class HomeViewModel : ViewModelBase, IDisposable
{
    // MainViewModel subscribes to wire up the dialog overlay.
    public event Action<CallDialogViewModel?>? DialogRequested;

    private readonly AppSettings _settings;
    private readonly IUploadOrchestrator _orchestrator;
    private readonly RecordingsViewModel _recordingsVm;
    private readonly IMonitorService _windowMonitor;
    private readonly IMonitorService _micMonitor;
    private readonly AudioRecordingService _recorder;
    private IMonitorService _activeMonitor;

    private bool _isRecording;
    private bool _hasActiveDialog;

    public bool IsRecording => _isRecording;
    private string _lastDetectedApp = string.Empty;
    private DateTime? _recordingStartedAt;

    private static readonly string[] CallHints =
    [
        "1. Привітання, тест на дебіла та встановлення рамок",
        "2. Первинний анамнез та підводимо лінію",
        "3. Вторинний анамнез, розкриваємо досвід клієнта через техніку \"ближче-далі\"",
        "4. Знайшли зачіпку, інтегруємо приклад успішного кейсу та аппрува",
        "5. Прийом \" чашка чаю \" + \" ваш кейс унікальний, антиприклад щойно був порожній кейс \"",
        "6. Ставимо дедлайн, вибираємо день і час",
        "7. Закриття у дзвінку, отримуємо ім'я, прізвище, пошту. І приймаємо оплату на реквізити",
    ];

    private ViewModelBase _callContent;
    public RecordingsViewModel RecordingsVm => _recordingsVm;

    public ViewModelBase CallContent
    {
        get => _callContent;
        private set
        {
            (_callContent as IDisposable)?.Dispose();
            SetField(ref _callContent, value);
        }
    }

    public HomeViewModel(AppSettings settings, IUploadOrchestrator orchestrator, RecordingsViewModel recordingsVm)
    {
        _settings     = settings;
        _orchestrator = orchestrator;
        _recordingsVm = recordingsVm;
        _recorder     = new AudioRecordingService(settings);

        _callContent = new IdleCallViewModel(StartRecording);

        _windowMonitor = new WindowMonitorService(new ICallDetector[]
        {
            new TelegramCallDetector(),
            new WhatsAppCallDetector(),
            new ViberCallDetector(),
            new RingostatCallDetector(),
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
            if (_isRecording || _hasActiveDialog) return;

            ShowDialog(new CallDialogViewModel(
                appName:        app,
                message:        "Виявлено дзвінок. Бажаєте розпочати запис розмови?",
                primaryLabel:   "Почати запис", onPrimary:   StartRecording,
                secondaryLabel: "Пропустити",   onSecondary: DismissDialog
            ));
        });
    }

    private void OnCallEnded(string app)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (!_isRecording) { DismissDialog(); return; }
            if (_hasActiveDialog) return;

            ShowDialog(new CallDialogViewModel(
                appName:        _lastDetectedApp,
                message:        "Дзвінок завершено. Зупинити запис?",
                primaryLabel:   "Завершити запис",  onPrimary:   StopRecording,
                secondaryLabel: "Продовжити запис", onSecondary: DismissDialog,
                recordingStartedAt: _recordingStartedAt
            ));
        });
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    public void ManualStartRecording()
    {
        if (_isRecording) return;
        if (string.IsNullOrEmpty(_lastDetectedApp))
            _lastDetectedApp = "Ручний запис";
        StartRecording();
    }

    public void ManualStopRecording()
    {
        if (_isRecording) StopRecording();
    }

    private void StartRecording()
    {
        DismissDialog();
        _isRecording        = true;
        _recordingStartedAt = DateTime.Now;
        OnPropertyChanged(nameof(IsRecording));

        if (!_recorder.StartRecording(_lastDetectedApp))
            Debug.WriteLine("[HomeVM] AudioRecordingService failed to start");

        CallContent = new ActiveCallViewModel(StopRecording);
        App.WindowManager.ShowCheatSheet(CallHints);
    }

    private async void StopRecording()
    {
        DismissDialog();
        _isRecording        = false;
        _recordingStartedAt = null;
        OnPropertyChanged(nameof(IsRecording));
        CallContent         = new IdleCallViewModel(StartRecording);

        var entry = _recordingsVm.AddEntry(_lastDetectedApp);
        try
        {
            string? path = await _recorder.StopRecordingAsync();
            App.WindowManager.CloseCheatSheet();

            if (path is null)
            {
                entry.Status = RecordingStatus.Error;
                return;
            }

            var result = await _orchestrator.UploadAsync(path);
            entry.DriveUrl = result.DriveUrl;
            entry.FilePath = result.LocalPath;
            entry.Status   = RecordingStatus.Saved;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HomeVM] StopRecording error: {ex.Message}");
            App.WindowManager.CloseCheatSheet();
            entry.Status = RecordingStatus.Error;
        }
    }

    // ── Dialog helpers ────────────────────────────────────────────────────────

    private void ShowDialog(CallDialogViewModel vm)
    {
        _hasActiveDialog = true;
        DialogRequested?.Invoke(vm);
    }

    private void DismissDialog()
    {
        _hasActiveDialog = false;
        DialogRequested?.Invoke(null);
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose()
    {
        _settings.PropertyChanged -= OnSettingsChanged;
        Unsubscribe(_activeMonitor);
        _activeMonitor.Stop();
        _windowMonitor.Dispose();
        _micMonitor.Dispose();
        _recorder.Dispose();
    }
}
