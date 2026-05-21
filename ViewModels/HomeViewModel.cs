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
using System.IO;
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
    private bool _isStopping;
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

        // Wire commands for entries already loaded from disk.
        foreach (var entry in _recordingsVm.Recordings)
        {
            WireEntryEditCommand(entry);
            WireEntryRetryCommand(entry);
        }

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
        if (_isRecording || _isStopping) return;
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
        if (_isStopping) return;
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
        _isStopping  = true;
        var callStartTime   = _recordingStartedAt;   // capture before clearing
        _recordingStartedAt = null;
        var callDuration    = callStartTime.HasValue ? DateTime.Now - callStartTime.Value : TimeSpan.Zero;
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

            // Store before upload — enables retry if upload fails or app closes mid-flight.
            entry.SourcePath = path;

            CallReportData? reportData = null;
            string? caption = null;
            bool telegramMatters = _orchestrator.IsTelegramReady && _settings.Position != "Діагност";
            if (telegramMatters || _orchestrator.IsKommoEnabled)
            {
                var result = await RequestCallReportAsync();
                if (result is not null)
                    result = result with
                    {
                        AppName  = _lastDetectedApp,
                        Duration = callDuration,
                    };
                caption    = result?.FormatCaption();
                reportData = result;
            }

            bool isRingostat   = _lastDetectedApp.Contains("Ringostat", StringComparison.OrdinalIgnoreCase);
            bool skipTelegram  = ShouldSkipTelegram(_settings.Position, callDuration);
            var upload = await _orchestrator.UploadAsync(path, caption, isRingostat ? null : reportData?.CrmUrl, callStartTime, reportData?.LeadSource, skipTelegram);
            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
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
        finally
        {
            _isStopping = false;
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
            position:    _settings.Position,
            existing:    existing);
        CallReportRequested?.Invoke(vm);
        return tcs.Task;
    }

    private void DismissCallReport() => CallReportRequested?.Invoke(null);

    // ── Edit report ───────────────────────────────────────────────────────────

    private void WireEntryEditCommand(RecordingEntry entry)
    {
        entry.EditReportCommand = new RelayCommand(
            execute:    () => _ = EditEntryReportAsync(entry),
            canExecute: () => entry.HasTelegramMessage);
    }

    private void WireEntryRetryCommand(RecordingEntry entry)
    {
        entry.RetryCommand = new RelayCommand(
            execute:    () => _ = RetryEntryAsync(entry),
            canExecute: () => entry.Status == RecordingStatus.Error && entry.HasRetryableFile);
    }

    private async Task RetryEntryAsync(RecordingEntry entry)
    {
        var retryPath = (!string.IsNullOrEmpty(entry.FilePath)   && File.Exists(entry.FilePath))   ? entry.FilePath
                      : (!string.IsNullOrEmpty(entry.SourcePath) && File.Exists(entry.SourcePath)) ? entry.SourcePath
                      : null;
        if (retryPath is null) return;

        // Show call report form (pre-filled with existing data if available).
        var reportData = await RequestCallReportAsync(existing: entry.ReportData);
        if (reportData is not null)
            reportData = reportData with { AppName = entry.Platform, Duration = TimeSpan.Zero };

        entry.Status = RecordingStatus.Loading;
        try
        {
            var caption      = reportData?.FormatCaption();
            var crmUrl       = reportData?.CrmUrl;
            bool isRingostat = entry.Platform.Contains("Ringostat", StringComparison.OrdinalIgnoreCase);

            var upload = await _orchestrator.UploadAsync(
                retryPath,
                caption,
                isRingostat ? null : crmUrl,
                entry.StartedAt,
                reportData?.LeadSource);

            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath ?? entry.FilePath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
            entry.ReportData        = reportData;
            entry.Status            = RecordingStatus.Saved;
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HomeVM] Retry failed: {ex.Message}");
            entry.Status = RecordingStatus.Error;
        }
    }

    private async Task EditEntryReportAsync(RecordingEntry entry)
    {
        var newData = await RequestCallReportAsync(existing: entry.ReportData);
        if (newData is null) return;

        var caption = newData.FormatCaption();

        var tgTask = entry.TelegramMessageId.HasValue
            ? _orchestrator.EditTelegramCaptionAsync(
                entry.TelegramMessageId.Value,
                entry.TelegramChatId,
                entry.TelegramTopicId,
                caption,
                entry.DriveUrl)
            : Task.FromResult(false);

        var kommoBase = StripHashtags(caption);
        var kommoNote = string.IsNullOrWhiteSpace(entry.DriveUrl)
            ? kommoBase
            : kommoBase + $"\n💾 Google Drive: {entry.DriveUrl}";
        var kommoTask = entry.KommoNoteId.HasValue && !string.IsNullOrWhiteSpace(newData.CrmUrl)
            ? _orchestrator.EditKommoNoteAsync(newData.CrmUrl, entry.KommoNoteId.Value, kommoNote)
            : Task.FromResult(false);

        await Task.WhenAll(tgTask, kommoTask);

        if (await tgTask || await kommoTask)
            entry.ReportData = newData;
        else
            Debug.WriteLine("[HomeVM] Both EditTelegramCaptionAsync and EditKommoNoteAsync returned false");
    }

    private static bool ShouldSkipTelegram(string position, TimeSpan duration) => position switch
    {
        "Кваліфікатор" => duration < TimeSpan.FromMinutes(1),
        "Менеджер"     => duration < TimeSpan.FromMinutes(10),
        _              => true,   // Діагност and any unknown — never send
    };

    private static string StripHashtags(string text)
    {
        var t = text.TrimEnd();
        var nl = t.LastIndexOf('\n');
        return nl >= 0 && t[(nl + 1)..].TrimStart('\r').StartsWith('#') ? t[..nl].TrimEnd() : t;
    }

    private void OnRecordingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        // Wire commands only for newly inserted entries not already wired (e.g. added via AddEntry).
        // Entries loaded from disk are handled in the constructor.
        if (e.NewItems is null) return;
        foreach (RecordingEntry entry in e.NewItems)
        {
            if (entry.EditReportCommand is null) WireEntryEditCommand(entry);
            if (entry.RetryCommand      is null) WireEntryRetryCommand(entry);
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
