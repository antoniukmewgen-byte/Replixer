using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Audio;
using Replixer.Services.Recording;
using Replixer.Services.Upload;
using Replixer.Services.Window;
using Replixer.Services.Window.Detectors;
using Replixer.ViewModels.Call;
using Replixer.ViewModels.Dialogs;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using RecordingStatus = Replixer.Models.RecordingStatus;

namespace Replixer.ViewModels;

public sealed class HomeViewModel : ViewModelBase, IDisposable
{
    // MainViewModel subscribes to wire up the dialog overlay.
    public event Action<CallDialogViewModel?>?  DialogRequested;
    public event Action<CallReportViewModel?>?  CallReportRequested;

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

        // Wire edit commands for entries already loaded from disk.
        foreach (var entry in _recordingsVm.Recordings)
            WireEntryEditCommand(entry);

        _recordingsVm.Recordings.CollectionChanged += OnRecordingsChanged;
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
        App.WindowManager.ShowCheatSheet();
    }

    private async void StopRecording()
    {
        DismissDialog();
        _isRecording = false;
        var callStartTime   = _recordingStartedAt;   // capture before clearing
        _recordingStartedAt = null;
        OnPropertyChanged(nameof(IsRecording));
        CallContent         = new IdleCallViewModel(StartRecording);

        var entry = _recordingsVm.AddEntry(_lastDetectedApp);
        WireEntryEditCommand(entry);
        try
        {
            string? path = await _recorder.StopRecordingAsync();
            App.WindowManager.CloseCheatSheet();

            if (path is null)
            {
                entry.Status = RecordingStatus.Error;
                return;
            }

            CallReportData? reportData = null;
            string? caption = null;
            if (_orchestrator.IsTelegramReady || _orchestrator.IsKommoEnabled)
            {
                var result = await RequestCallReportAsync();
                caption    = result?.FormatCaption();
                reportData = result;
            }

            bool isRingostat = _lastDetectedApp.Contains("Ringostat", StringComparison.OrdinalIgnoreCase);
            var upload = await _orchestrator.UploadAsync(path, caption, isRingostat ? null : reportData?.CrmUrl, callStartTime);
            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.ReportData        = reportData;
            entry.Status            = RecordingStatus.Saved;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HomeVM] StopRecording error: {ex.Message}");
            App.WindowManager.CloseCheatSheet();
            entry.Status = RecordingStatus.Error;
        }
    }

    // ── Call report form ──────────────────────────────────────────────────────

    private Task<CallReportData?> RequestCallReportAsync(CallReportData? existing = null)
    {
        App.WindowManager.ShowMainWindow();

        var tcs = new TaskCompletionSource<CallReportData?>();
        var vm  = new CallReportViewModel(
            onComplete: data =>
            {
                DismissCallReport();
                tcs.TrySetResult(data);
            },
            managerName: _settings.ManagerName,
            existing:    existing);
        CallReportRequested?.Invoke(vm);
        return tcs.Task;
    }

    private void DismissCallReport() => CallReportRequested?.Invoke(null);

    // ── Edit report ───────────────────────────────────────────────────────────

    private void WireEntryEditCommand(RecordingEntry entry)
    {
        entry.EditReportCommand = new RelayCommand(
            execute:  () => _ = EditEntryReportAsync(entry),
            canExecute: () => entry.HasTelegramMessage);
    }

    private async Task EditEntryReportAsync(RecordingEntry entry)
    {
        var newData = await RequestCallReportAsync(existing: entry.ReportData);
        if (newData is null) return;

        var caption = newData.FormatCaption();
        var ok = await _orchestrator.EditTelegramCaptionAsync(
            entry.TelegramMessageId!.Value,
            entry.TelegramChatId,
            entry.TelegramTopicId,
            caption);

        if (ok)
            entry.ReportData = newData;
        else
            Debug.WriteLine("[HomeVM] EditTelegramCaptionAsync returned false");
    }

    private void OnRecordingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Wire commands only for newly inserted entries not already wired (e.g. added via AddEntry).
        // Entries loaded from disk are handled in the constructor.
        if (e.NewItems is null) return;
        foreach (RecordingEntry entry in e.NewItems)
        {
            if (entry.EditReportCommand is null)
                WireEntryEditCommand(entry);
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
        _recordingsVm.Recordings.CollectionChanged -= OnRecordingsChanged;
        Unsubscribe(_activeMonitor);
        _activeMonitor.Stop();
        _windowMonitor.Dispose();
        _micMonitor.Dispose();
        _recorder.Dispose();
    }
}
