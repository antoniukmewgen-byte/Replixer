using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Audio;
using Replixer.Services.Manager;
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
    private readonly IWindowManager _windowManager;
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

    public HomeViewModel(AppSettings settings, IUploadOrchestrator orchestrator, RecordingsViewModel recordingsVm, IWindowManager windowManager)
    {
        _settings      = settings;
        _orchestrator  = orchestrator;
        _recordingsVm  = recordingsVm;
        _windowManager = windowManager;
        _recorder      = new AudioRecordingService(settings);

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
        {
            Debug.WriteLine("[HomeVM] AudioRecordingService failed to start");
            var reason = _recorder.LastError;
            var msg    = string.IsNullOrWhiteSpace(reason)
                ? "Не вдалося запустити запис. Перевірте мікрофон."
                : $"Не вдалося запустити запис.\n{reason}";
            NotificationService.ShowError(msg);
        }

        CallContent = new ActiveCallViewModel(StopRecording);
        _windowManager.ShowCheatSheet();
    }

    private void StopRecording() => _ = StopRecordingAsync();

    private async Task StopRecordingAsync()
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
        entry.SourcePath = _recorder.CurrentFilePath; // persist path immediately so retry works after crash
        try
        {
            // Stop recording and show the report form in parallel — the form
            // doesn't need the file, so the user sees it immediately.
            bool telegramMatters = _orchestrator.IsTelegramReady && PositionPolicy.IsTelegramVisible(_settings.Position);
            bool needsForm       = telegramMatters || _orchestrator.IsKommoEnabled;

            var stopTask   = _recorder.StopRecordingAsync();
            var reportTask = needsForm ? RequestCallReportAsync() : Task.FromResult<CallReportData?>(null);

            // Close cheat sheet as soon as the file is ready — don't wait for the form.
            string? path = await stopTask;
            _windowManager.CloseCheatSheet();

            await reportTask;

            if (path is null)
            {
                entry.Status = RecordingStatus.Error;
                var reason = _recorder.LastError;
                var msg    = string.IsNullOrWhiteSpace(reason)
                    ? "Помилка обробки аудіо. Файл не збережено."
                    : $"Помилка обробки аудіо.\n{reason}";
                NotificationService.ShowError(msg);
                return;
            }

            // Store before upload — enables retry if upload fails or app closes mid-flight.
            entry.SourcePath = path;

            CallReportData? reportData = reportTask.Result;
            if (reportData is not null)
                reportData = reportData with
                {
                    AppName  = entry.PlatformDisplayName,
                    Duration = callDuration,
                };
            string? caption = reportData?.FormatCaption();

            bool isRingostat   = _lastDetectedApp.Contains("Ringostat", StringComparison.OrdinalIgnoreCase);
            bool skipTelegram  = PositionPolicy.ShouldSkipTelegram(_settings.Position, callDuration);
            var upload = await _orchestrator.UploadAsync(path, caption, isRingostat ? null : reportData?.CrmUrl, callStartTime, reportData?.LeadSource, skipTelegram);
            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
            entry.ReportData        = reportData;
            entry.Status            = RecordingStatus.Saved;
            NotificationService.ShowSuccess("Запис збережено та відправлено.");
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HomeVM] StopRecording error: {ex.Message}");
            _windowManager.CloseCheatSheet();
            entry.Status = RecordingStatus.Error;
            NotificationService.ShowError($"Помилка збереження запису.\n{ex.Message}");
        }
        finally
        {
            _isStopping = false;
        }
    }

    // ── Call report form ──────────────────────────────────────────────────────

    private Task<CallReportData?> RequestCallReportAsync(CallReportData? existing = null)
    {
        _windowManager.ShowMainWindow();

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
            reportData = reportData with { AppName = entry.PlatformDisplayName, Duration = TimeSpan.Zero };

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
            NotificationService.ShowSuccess("Запис повторно відправлено.");
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HomeVM] Retry failed: {ex.Message}");
            entry.Status = RecordingStatus.Error;
            NotificationService.ShowError($"Повторна відправка не вдалась.\n{ex.Message}");
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
            : Task.FromResult<string?>("Telegram: повідомлення не прив'язано");

        var kommoBase = CaptionHelper.StripHashtags(caption);
        var kommoNote = string.IsNullOrWhiteSpace(entry.DriveUrl)
            ? kommoBase
            : kommoBase + $"\n💾 Google Drive: {entry.DriveUrl}";
        var kommoTask = entry.KommoNoteId.HasValue && !string.IsNullOrWhiteSpace(newData.CrmUrl)
            ? _orchestrator.EditKommoNoteAsync(newData.CrmUrl, entry.KommoNoteId.Value, kommoNote)
            : Task.FromResult<string?>("Kommo: нотатка не прив'язана");

        await Task.WhenAll(tgTask, kommoTask);

        string? tgError    = tgTask.Result;
        string? kommoError = kommoTask.Result;

        if (tgError is null || kommoError is null)
        {
            entry.ReportData = newData;
            NotificationService.ShowSuccess("Звіт оновлено.");
        }
        else
        {
            var errors = new List<string>();
            if (tgError    is not null) errors.Add(tgError);
            if (kommoError is not null) errors.Add(kommoError);
            var reason = string.Join("\n", errors);
            Debug.WriteLine($"[HomeVM] Edit failed: {reason}");
            NotificationService.ShowError($"Не вдалося оновити звіт.\n{reason}");
        }
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
