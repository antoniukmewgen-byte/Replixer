using EchoVault.Models;
using EchoVault.Services;
using EchoVault.Services.Audio;
using EchoVault.Services.Recording;
using EchoVault.Services.Upload;
using EchoVault.Services.Window;
using EchoVault.Services.Window.Detectors;
using EchoVault.ViewModels.Call;
using EchoVault.ViewModels.Dialogs;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using RecordingStatus = EchoVault.Models.RecordingStatus;

namespace EchoVault.ViewModels;

public class HomeViewModel : ViewModelBase
{
    private readonly Action<CallDialogViewModel?> _setDialog;
    private readonly AppSettings _settings;
    private readonly GoogleDriveUploadService _uploader;
    private readonly TelegramUploadService _telegram;
    private readonly RecordingsViewModel _recordingsVm;
    private readonly IMonitorService _windowMonitor;
    private readonly IMonitorService _micMonitor;
    private readonly AudioRecordingService _recorder;
    private IMonitorService _activeMonitor;

    private bool _isRecording;
    private bool _hasActiveDialog;
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

    public HomeViewModel(Action<CallDialogViewModel?> setDialog, AppSettings settings, GoogleDriveUploadService uploader, TelegramUploadService telegram, RecordingsViewModel recordingsVm)
    {
        _setDialog    = setDialog;
        _settings     = settings;
        _uploader     = uploader;
        _telegram     = telegram;
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

            if (_isRecording) return;
            if (_hasActiveDialog) return;

            ShowDialog(new CallDialogViewModel(
                appName: app,
                message: "Виявлено дзвінок. Бажаєте розпочати запис розмови?",
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
                appName: _lastDetectedApp,
                message: "Дзвінок завершено. Зупинити запис?",
                primaryLabel:   "Завершити запис",  onPrimary:   StopRecording,
                secondaryLabel: "Продовжити запис", onSecondary: DismissDialog,
                recordingStartedAt: _recordingStartedAt
            ));
        });
    }

    // ── Recording ─────────────────────────────────────────────────────────────

    private void StartRecording()
    {
        DismissDialog();
        _isRecording = true;
        _recordingStartedAt = DateTime.Now;

        bool started = _recorder.StartRecording(_lastDetectedApp);
        if (!started)
            Debug.WriteLine("[HomeVM] AudioRecordingService failed to start");

        CallContent = new ActiveCallViewModel(StopRecording);

        var hints = new[] { 
            "1. Привітання, тест на дебіла та встановлення рамок",
            "2. Первинний анамнез та підводимо лінію",
            "3. Вторинний анамнез, розкриваємо досвід клієнта через техніку \"ближче-далі\"",
            "4. Знайшли зачіпку, інтегруємо приклад успішного кейсу та аппрува",
            "5. Прийом \" чашка чаю \" + \" ваш кейс унікальний, антиприклад щойно був порожній кейс \"",
            "6. Ставимо дедлайн, вибираємо день і час",
            "7. Закриття у дзвінку, отримуємо ім'я, прізвище, пошту. І приймаємо оплату на реквізити",
        };

        App.WindowManager.ShowCheatSheet(hints);
    }

    private async void StopRecording()
    {
        DismissDialog();
        _isRecording = false;
        _recordingStartedAt = null;

        // Switch UI back to idle immediately — recording finishes in background
        CallContent = new IdleCallViewModel(StartRecording);

        var entry = _recordingsVm.AddEntry(_lastDetectedApp);

        Debug.WriteLine("[HomeVM] ── StopRecording ──────────────────────────");
        Debug.WriteLine($"[HomeVM] IsGoogleDriveEnabled : {_settings.IsGoogleDriveEnabled}");
        Debug.WriteLine($"[HomeVM] IsGoogleAuthorized   : {_uploader.IsAuthorized}");
        Debug.WriteLine($"[HomeVM] GoogleDriveFolderId  : '{_settings.GoogleDriveFolderId}'");

        string? path = await _recorder.StopRecordingAsync();
        App.WindowManager.CloseCheatSheet();

        if (path is null)
        {
            entry.Status = RecordingStatus.Error;
            Debug.WriteLine("[HomeVM] ✗ StopRecordingAsync returned null — no file created");
            return;
        }

        bool fileExists = File.Exists(path);
        long fileSize   = fileExists ? new FileInfo(path).Length : -1;
        Debug.WriteLine($"[HomeVM] Temp file : {path}");
        Debug.WriteLine($"[HomeVM] Exists    : {fileExists}  |  Size: {fileSize} bytes");

        if (_settings.IsTelegramEnabled && _telegram.IsAuthorized)
        {
            Debug.WriteLine($"[HomeVM] → sending to Telegram (chatId: {_settings.TelegramChatId}) …");
            bool ok = await _telegram.SendFileAsync(path, _settings.TelegramChatId);
            Debug.WriteLine(ok ? "[HomeVM] ✓ Telegram send OK" : "[HomeVM] ✗ Telegram send FAILED");
        }
        else
        {
            Debug.WriteLine($"[HomeVM] Telegram skip — enabled: {_settings.IsTelegramEnabled}, authorized: {_telegram.IsAuthorized}");
        }

        if (_settings.IsGoogleDriveEnabled)
        {
            Debug.WriteLine("[HomeVM] → uploading to Google Drive …");

            string? driveUrl = await _uploader.UploadAsync(
                path,
                string.IsNullOrWhiteSpace(_settings.GoogleDriveFolderId)
                    ? null
                    : _settings.GoogleDriveFolderId);

            if (driveUrl is not null)
            {
                SafeDeleteFile(path);
                Debug.WriteLine($"[HomeVM] ✓ Upload OK — temp file deleted");
                if (!string.IsNullOrEmpty(driveUrl))
                    entry.DriveUrl = driveUrl;
            }
            else
            {
                Debug.WriteLine("[HomeVM] ✗ Upload FAILED — falling back to local save");
                entry.FilePath = MoveToRecordingsFolder(path);
            }
        }
        else
        {
            Debug.WriteLine("[HomeVM] → Google Drive disabled, saving locally …");
            entry.FilePath = MoveToRecordingsFolder(path);
        }

        entry.Status = RecordingStatus.Saved;
        Debug.WriteLine("[HomeVM] ───────────────────────────────────────────");
    }

    private string? MoveToRecordingsFolder(string tempPath)
    {
        try
        {
            Directory.CreateDirectory(_settings.RecordingsFolder);
            string dest = Path.Combine(_settings.RecordingsFolder, Path.GetFileName(tempPath));
            File.Move(tempPath, dest, overwrite: true);
            Debug.WriteLine($"[HomeVM] Moved → {dest}");
            return dest;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[HomeVM] Move failed: {ex.Message}");
            return null;
        }
    }

    private static void SafeDeleteFile(string path)
    {
        try { File.Delete(path); } catch { }
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
