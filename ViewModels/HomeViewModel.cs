using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Audio;
using Replixer.Services.Manager;
using Replixer.Services.Recording;
using Replixer.Services.Upload;
using Replixer.ViewModels.Call;
using Replixer.ViewModels.Dialogs;
using System.Collections.Specialized;
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
    private readonly IMonitorService _micMonitor;
    private readonly AudioRecordingService _recorder;
    private readonly IWindowManager _windowManager;

    private bool  _isRecording;
    private bool  _isStopping;
    private bool  _hasActiveDialog;
    private Task? _pendingStopTask;

    public bool IsRecording => _isRecording;
    private string _lastDetectedApp = "Ручний запис";
    private DateTime? _recordingStartedAt;

    private CallDialogViewModel?                  _currentDialog;
    private TaskCompletionSource<CallReportData?>? _reportTcs;
    private CallReportViewModel?                   _activeReportVm;
    private bool             _reportInterrupted;
    private CallReportData?  _interruptedDraft;

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

        _callContent = new IdleCallViewModel(ManualStartRecording);

        _micMonitor = new MicrophoneMonitorService();
        _micMonitor.CallDetected += OnCallDetected;
        _micMonitor.CallEnded    += OnCallEnded;
        _micMonitor.Start();

        foreach (var entry in _recordingsVm.Recordings)
        {
            WireEntryEditCommand(entry);
            WireEntryRetryCommand(entry);
            WireEntryResumeDraftCommand(entry);
        }

        _recordingsVm.Recordings.CollectionChanged += OnRecordingsChanged;
    }

    private void OnCallDetected(string app)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            _lastDetectedApp = app;
            if (_isRecording)
            {
                _recorder.UpdatePlatform(app);
                if (!_hasActiveDialog) return;
            }
            if (_hasActiveDialog) return;

            ShowDialog(new CallDialogViewModel(
                appName:        app,
                message:        "Виявлено дзвінок. Бажаєте розпочати запис розмови?",
                primaryLabel:   "Почати запис", onPrimary:   StartRecording,
                secondaryLabel: "Пропустити",   onSecondary: DismissDialog,
                confirmSecondary: true
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
        _lastDetectedApp = "Ручний запис";  // explicit override regardless of prior detector value
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
            _isRecording        = false;
            _recordingStartedAt = null;
            OnPropertyChanged(nameof(IsRecording));
            CallContent = new IdleCallViewModel(ManualStartRecording);
            var reason = _recorder.LastError;
            var msg    = string.IsNullOrWhiteSpace(reason)
                ? "Не вдалося запустити запис. Перевірте мікрофон."
                : $"Не вдалося запустити запис.\n{reason}";
            ErrorReporter.Report("RECORDING_START", msg);
            return;
        }

        CallContent = new ActiveCallViewModel(StopRecording);
        _windowManager.ShowCheatSheet();
    }

    private void StopRecording() => _pendingStopTask = StopRecordingAsync();

    private async Task StopRecordingAsync()
    {
        DismissDialog();
        _isRecording = false;
        _isStopping  = true;
        var callStartTime   = _recordingStartedAt;
        _recordingStartedAt = null;
        var callDuration    = callStartTime.HasValue ? DateTime.Now - callStartTime.Value : TimeSpan.Zero;
        OnPropertyChanged(nameof(IsRecording));
        CallContent         = new IdleCallViewModel(ManualStartRecording);

        var entry = _recordingsVm.AddEntry(_lastDetectedApp);
        WireEntryEditCommand(entry);
        WireEntryRetryCommand(entry);
        WireEntryResumeDraftCommand(entry);
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
            bool wasInterrupted = _reportInterrupted;
            _reportInterrupted  = false;

            if (path is null)
            {
                entry.Status = RecordingStatus.Error;
                var reason = _recorder.LastError;
                var msg    = string.IsNullOrWhiteSpace(reason)
                    ? "Помилка обробки аудіо. Файл не збережено."
                    : $"Помилка обробки аудіо.\n{reason}";
                ErrorReporter.Report("AUDIO_STOP", msg);
                return;
            }

            entry.SourcePath = path;

            if (wasInterrupted)
            {
                entry.CallDuration = callDuration;
                entry.ReportData   = _interruptedDraft;
                _interruptedDraft  = null;
                entry.Status       = RecordingStatus.Draft;
                return;
            }

            if (reportData is not null)
                reportData = reportData with
                {
                    AppName  = entry.PlatformDisplayName,
                    Duration = callDuration,
                };
            string? caption = reportData?.FormatCaption();

            bool skipTelegram = PositionPolicy.ShouldSkipTelegram(_settings.Position, callDuration);
            string? callType  = ResolveCallType(reportData);
            var upload = await _orchestrator.UploadAsync(path, caption, reportData?.CrmUrl, callStartTime, reportData?.LeadSource, skipTelegram, callType);
            entry.CallDuration      = callDuration;
            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
            entry.ReportData        = reportData;

            if (upload.DriveWarning is not null)
            {
                entry.Status = RecordingStatus.Error;
                ErrorReporter.Report("UPLOAD_DRIVE", $"Запис не збережено:\n{upload.DriveWarning}");
                return;
            }

            if (upload.LocalPathWarning is not null)
            {
                entry.Status = RecordingStatus.Error;
                ErrorReporter.Report("UPLOAD_LOCAL", $"Запис не збережено:\n{upload.LocalPathWarning}");
                return;
            }

            entry.Status = RecordingStatus.Saved;
            ShowUploadNotification(upload);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            _windowManager.CloseCheatSheet();
            entry.Status = RecordingStatus.Error;
            ErrorReporter.Report("STOP_RECORDING", "Помилка збереження запису.", ex);
        }
        finally
        {
            _isStopping = false;
        }
    }

    private Task<CallReportData?> RequestCallReportAsync(CallReportData? existing = null)
    {
        _windowManager.ShowMainWindow();

        var tcs = new TaskCompletionSource<CallReportData?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reportTcs = tcs;

        var vm = new CallReportViewModel(
            onComplete: data =>
            {
                tcs.TrySetResult(data);
                DismissCallReport();
            },
            managerName: _settings.ManagerName,
            position:    _settings.Position,
            existing:    existing);
        _activeReportVm = vm;
        CallReportRequested?.Invoke(vm);
        return tcs.Task;
    }

    private void DismissCallReport(bool interrupted = false)
    {
        // Complete any pending report task with null so StopRecordingAsync never hangs
        // when the dialog is dismissed externally (new call, app closing, manual dismiss).
        if (interrupted && _reportTcs is not null)
        {
            _reportInterrupted  = true;
            _interruptedDraft   = _activeReportVm?.CaptureDraft();
        }
        _reportTcs?.TrySetResult(null);
        _reportTcs      = null;
        _activeReportVm = null;
        CallReportRequested?.Invoke(null);
    }

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

    private void WireEntryResumeDraftCommand(RecordingEntry entry)
    {
        entry.ResumeDraftCommand = new RelayCommand(
            execute:    () => _ = ResumeDraftAsync(entry),
            canExecute: () => entry.Status == RecordingStatus.Draft && entry.HasRetryableFile);
    }

    private async Task ResumeDraftAsync(RecordingEntry entry)
    {
        var filePath = (!string.IsNullOrEmpty(entry.SourcePath) && File.Exists(entry.SourcePath)) ? entry.SourcePath
                     : (!string.IsNullOrEmpty(entry.FilePath)   && File.Exists(entry.FilePath))   ? entry.FilePath
                     : null;
        if (filePath is null)
        {
            entry.Status = RecordingStatus.Error;
            NotificationService.ShowError("Файл запису не знайдено.");
            return;
        }

        var reportData = await RequestCallReportAsync(existing: entry.ReportData);
        if (reportData is null) return;

        reportData = reportData with
        {
            AppName  = entry.PlatformDisplayName,
            Duration = entry.CallDuration,
        };

        entry.Status = RecordingStatus.Loading;
        try
        {
            bool skipTelegram = PositionPolicy.ShouldSkipTelegram(_settings.Position, entry.CallDuration);
            string? callType  = ResolveCallType(reportData);
            string? caption   = reportData.FormatCaption();

            var upload = await _orchestrator.UploadAsync(
                filePath,
                caption,
                reportData.CrmUrl,
                entry.StartedAt,
                reportData.LeadSource,
                skipTelegram,
                callType);

            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath ?? entry.FilePath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
            entry.ReportData        = reportData;

            if (upload.DriveWarning is not null)
            {
                entry.Status = RecordingStatus.Error;
                ErrorReporter.Report("RESUME_DRIVE", $"Запис не збережено:\n{upload.DriveWarning}");
                return;
            }

            if (upload.LocalPathWarning is not null)
            {
                entry.Status = RecordingStatus.Error;
                ErrorReporter.Report("RESUME_LOCAL", $"Запис не збережено:\n{upload.LocalPathWarning}");
                return;
            }

            entry.Status = RecordingStatus.Saved;
            ShowUploadNotification(upload);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            entry.Status = RecordingStatus.Error;
            ErrorReporter.Report("RESUME_DRAFT", "Повторна відправка не вдалась.", ex);
        }
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
                Duration = entry.ReportData?.Duration ?? entry.CallDuration,
            };

        entry.Status = RecordingStatus.Loading;
        try
        {
            var caption      = reportData?.FormatCaption();
            var crmUrl       = reportData?.CrmUrl;
            string? callType = ResolveCallType(reportData);

            var upload = await _orchestrator.UploadAsync(
                retryPath,
                caption,
                crmUrl,
                entry.StartedAt,
                reportData?.LeadSource,
                skipTelegram: entry.TelegramMessageId.HasValue || PositionPolicy.ShouldSkipTelegram(_settings.Position, entry.ReportData?.Duration ?? entry.CallDuration),
                callType: callType);

            entry.DriveUrl          = upload.DriveUrl;
            entry.FilePath          = upload.LocalPath ?? entry.FilePath;
            entry.TelegramMessageId = upload.TelegramMessageId;
            entry.TelegramChatId    = upload.TelegramChatId;
            entry.TelegramTopicId   = upload.TelegramTopicId;
            entry.KommoNoteId       = upload.KommoNoteId;
            entry.ReportData        = reportData;

            if (upload.DriveWarning is not null)
            {
                entry.Status = RecordingStatus.Error;
                ErrorReporter.Report("RETRY_DRIVE", $"Запис не збережено:\n{upload.DriveWarning}");
                return;
            }

            entry.Status = RecordingStatus.Saved;
            ShowUploadNotification(upload, isRetry: true);
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
        catch (Exception ex)
        {
            entry.Status = RecordingStatus.Error;
            ErrorReporter.Report("RETRY_UPLOAD", "Повторна відправка не вдалась.", ex);
        }
    }

    private async Task EditEntryReportAsync(RecordingEntry entry)
    {
        var newData = await RequestCallReportAsync(existing: entry.ReportData);
        if (newData is null) return;

        newData = newData with { AppName = entry.PlatformDisplayName };
        var caption  = newData.FormatCaption();
        var callType = ResolveCallType(newData);

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
            ? _orchestrator.EditKommoNoteAsync(newData.CrmUrl, entry.KommoNoteId.Value, kommoNote, callType)
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
            ErrorReporter.Report("EDIT_REPORT", $"Не вдалося оновити звіт.\n{reason}");
        }
    }

    private void OnRecordingsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is null) return;
        foreach (RecordingEntry entry in e.NewItems)
        {
            if (entry.EditReportCommand  is null) WireEntryEditCommand(entry);
            if (entry.RetryCommand       is null) WireEntryRetryCommand(entry);
            if (entry.ResumeDraftCommand is null) WireEntryResumeDraftCommand(entry);
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
        DismissCallReport(interrupted: true);
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
        // Unblock any awaiting report dialog before waiting on the stop task —
        // otherwise we deadlock: UI thread is blocked here while StopRecordingAsync
        // is suspended waiting for a TCS that only completes via a UI interaction.
        _reportTcs?.TrySetResult(null);
        _activeReportVm = null;

        _pendingStopTask?.Wait(TimeSpan.FromSeconds(30));
        _currentDialog?.Dispose();
        _recordingsVm.Recordings.CollectionChanged -= OnRecordingsChanged;
        _micMonitor.CallDetected -= OnCallDetected;
        _micMonitor.CallEnded    -= OnCallEnded;
        _micMonitor.Stop();
        _micMonitor.Dispose();
    }
}
