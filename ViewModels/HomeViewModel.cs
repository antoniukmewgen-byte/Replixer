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

    private CallDialogViewModel? _currentDialog;

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

    public HomeViewModel(
        AppSettings settings,
        IUploadOrchestrator orchestrator,
        RecordingsViewModel recordingsVm,
        IWindowManager windowManager,
        AudioRecordingService recorder)
    {
        _settings      = settings;
        _orchestrator  = orchestrator;
        _recordingsVm  = recordingsVm;
        _windowManager = windowManager;
        _recorder      = recorder;

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

        foreach (var entry in _recordingsVm.Recordings)
        {
            WireEntryEditCommand(entry);
            WireEntryRetryCommand(entry);
        }

        _recordingsVm.Recordings.CollectionChanged += OnRecordingsChanged;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(AppSettings.MonitorMode)) return;

        var prev = _activeMonitor;
        var next = GetMonitorForMode(_settings.MonitorMode);
        if (next == prev) return;

        Unsubscribe(prev);
        prev.Stop();

        _activeMonitor = next;
        Subscribe(next);
        next.Start();
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

        if (string.IsNullOrEmpty(_lastDetectedApp))
            _lastDetectedApp = "Ручний запис";

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
        var callStartTime   = _recordingStartedAt;
        _recordingStartedAt = null;
        var callDuration    = callStartTime.HasValue ? DateTime.Now - callStartTime.Value : TimeSpan.Zero;
        OnPropertyChanged(nameof(IsRecording));
        CallContent         = new IdleCallViewModel(StartRecording);

        var entry = _recordingsVm.AddEntry(_lastDetectedApp);
        WireEntryEditCommand(entry);
        WireEntryRetryCommand(entry);
        entry.SourcePath = _recorder.CurrentFilePath;
        try
        {
            bool telegramMatters = _orchestrator.IsTelegramReady && PositionPolicy.IsTelegramVisible(_settings.Position);
            bool needsForm       = telegramMatters || _orchestrator.IsKommoEnabled;

            var stopTask   = _recorder.StopRecordingAsync();
            var reportTask = needsForm ? RequestCallReportAsync() : Task.FromResult<CallReportData?>(null);

            string? path = await stopTask;
            _windowManager.CloseCheatSheet();

            CallReportData? reportData = await reportTask;

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

            entry.SourcePath = path;

            if (reportData is not null)
                reportData = reportData with
                {
                    AppName  = entry.PlatformDisplayName,
                    Duration = callDuration,
                };
            string? caption = reportData?.FormatCaption();

            bool isRingostat  = _lastDetectedApp.Contains("Ringostat", StringComparison.OrdinalIgnoreCase);
            bool skipTelegram = PositionPolicy.ShouldSkipTelegram(_settings.Position, callDuration);
            string? callType  = ResolveCallType(reportData);
            var upload = await _orchestrator.UploadAsync(path, caption, isRingostat ? null : reportData?.CrmUrl, callStartTime, reportData?.LeadSource, skipTelegram, callType);
            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
            entry.ReportData        = reportData;
            entry.Status            = RecordingStatus.Saved;
            ShowUploadNotification(upload);
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

        var reportData = await RequestCallReportAsync(existing: entry.ReportData);
        if (reportData is not null)
            reportData = reportData with
            {
                AppName  = entry.PlatformDisplayName,
                Duration = entry.ReportData?.Duration ?? TimeSpan.Zero,
            };

        entry.Status = RecordingStatus.Loading;
        try
        {
            var caption      = reportData?.FormatCaption();
            var crmUrl       = reportData?.CrmUrl;
            bool isRingostat = entry.Platform.Contains("Ringostat", StringComparison.OrdinalIgnoreCase);
            string? callType = ResolveCallType(reportData);

            var upload = await _orchestrator.UploadAsync(
                retryPath,
                caption,
                isRingostat ? null : crmUrl,
                entry.StartedAt,
                reportData?.LeadSource,
                callType: callType);

            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath ?? entry.FilePath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
            entry.ReportData        = reportData;
            entry.Status            = RecordingStatus.Saved;
            ShowUploadNotification(upload, isRetry: true);
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
            : Task.FromResult<string?>(null);

        var kommoBase = CaptionHelper.StripHashtags(caption);
        var kommoNote = string.IsNullOrWhiteSpace(entry.DriveUrl)
            ? kommoBase
            : kommoBase + $"\n💾 Google Drive: {entry.DriveUrl}";
        var kommoTask = entry.KommoNoteId.HasValue && !string.IsNullOrWhiteSpace(newData.CrmUrl)
            ? _orchestrator.EditKommoNoteAsync(newData.CrmUrl, entry.KommoNoteId.Value, kommoNote)
            : Task.FromResult<string?>(null);

        await Task.WhenAll(tgTask, kommoTask);

        string? tgError    = tgTask.Result;
        string? kommoError = kommoTask.Result;

        if (tgError is null && kommoError is null)
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
        if (e.NewItems is null) return;
        foreach (RecordingEntry entry in e.NewItems)
        {
            if (entry.EditReportCommand is null) WireEntryEditCommand(entry);
            if (entry.RetryCommand      is null) WireEntryRetryCommand(entry);
        }
    }

    private static void ShowUploadNotification(UploadResult upload, bool isRetry = false)
    {
        var warnings = new List<string>();
        if (upload.TelegramAttempted && upload.TelegramWarning is not null)
            warnings.Add(upload.TelegramWarning);
        if (upload.KommoAttempted && upload.KommoWarning is not null)
            warnings.Add(upload.KommoWarning);

        if (warnings.Count == 0)
        {
            NotificationService.ShowSuccess(isRetry ? "Запис повторно відправлено." : "Запис збережено та відправлено.");
        }
        else
        {
            var detail = string.Join("\n", warnings);
            NotificationService.ShowError($"Запис збережено, але не всі сервіси спрацювали:\n{detail}");
        }
    }

    private static string? ResolveCallType(CallReportData? reportData)
    {
        if (reportData is null || string.IsNullOrEmpty(reportData.CallType)) return null;
        return reportData.CallType == "Інший" && !string.IsNullOrWhiteSpace(reportData.CustomCallType)
            ? reportData.CustomCallType
            : reportData.CallType;
    }

    private void ShowDialog(CallDialogViewModel vm)
    {
        _currentDialog?.Dispose();
        _currentDialog   = vm;
        _hasActiveDialog = true;
        DialogRequested?.Invoke(vm);
    }

    private void DismissDialog()
    {
        _currentDialog?.Dispose();
        _currentDialog   = null;
        _hasActiveDialog = false;
        DialogRequested?.Invoke(null);
    }

    public void Dispose()
    {
        _currentDialog?.Dispose();
        _settings.PropertyChanged -= OnSettingsChanged;
        _recordingsVm.Recordings.CollectionChanged -= OnRecordingsChanged;
        Unsubscribe(_activeMonitor);
        _activeMonitor.Stop();
        _windowMonitor.Dispose();
        _micMonitor.Dispose();
    }
}
