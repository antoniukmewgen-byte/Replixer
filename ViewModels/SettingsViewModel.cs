using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services.Upload;
using System.ComponentModel;
using System.Windows;

namespace Replixer.ViewModels;

public sealed class SettingsViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private readonly GoogleDriveUploadService _uploader;
    private readonly TelegramUploadService _telegram;

    // ── Monitor mode ──────────────────────────────────────────────────────────

    public bool IsWindowMonitorActive
    {
        get => _settings.MonitorMode == MonitorMode.Window;
        set
        {
            if (!value) return;
            _settings.MonitorMode = MonitorMode.Window;
        }
    }

    public bool IsMicrophoneMonitorActive
    {
        get => _settings.MonitorMode == MonitorMode.Microphone;
        set
        {
            if (!value) return;
            _settings.MonitorMode = MonitorMode.Microphone;
        }
    }

    // ── Google Drive ──────────────────────────────────────────────────────────

    public bool IsGoogleDriveEnabled
    {
        get => _settings.IsGoogleDriveEnabled;
        set => _settings.IsGoogleDriveEnabled = value;
    }

    public string GoogleDriveFolderId
    {
        get => _settings.GoogleDriveFolderId;
        set => _settings.GoogleDriveFolderId = value;
    }

    // null = не перевірено, true = OK, false = помилка
    private bool? _isDriveConnected;
    public bool? IsDriveConnected
    {
        get => _isDriveConnected;
        private set => SetField(ref _isDriveConnected, value);
    }

    private bool _isCheckingDrive;
    public bool IsCheckingDrive
    {
        get => _isCheckingDrive;
        private set => SetField(ref _isCheckingDrive, value);
    }

    private string? _driveConnectionError;
    public string? DriveConnectionError
    {
        get => _driveConnectionError;
        private set => SetField(ref _driveConnectionError, value);
    }

    public AsyncRelayCommand TestDriveConnectionCommand { get; }

    // ── Telegram ──────────────────────────────────────────────────────────────

    public bool IsTelegramEnabled
    {
        get => _settings.IsTelegramEnabled;
        set => _settings.IsTelegramEnabled = value;
    }

    public string TelegramPhone
    {
        get => _settings.TelegramPhone;
        set => _settings.TelegramPhone = value;
    }

    public IReadOnlyList<TelegramChat> TelegramChats => TelegramUploadService.Chats;

    private TelegramChat? _selectedTelegramChat;
    public TelegramChat? SelectedTelegramChat
    {
        get => _selectedTelegramChat;
        set
        {
            SetField(ref _selectedTelegramChat, value);
            if (value != null) _settings.TelegramChatId = value.Id;
        }
    }

    private bool _isTelegramAuthorized;
    public bool IsTelegramAuthorized
    {
        get => _isTelegramAuthorized;
        private set => SetField(ref _isTelegramAuthorized, value);
    }

    private bool _isAuthorizingTelegram;
    public bool IsAuthorizingTelegram
    {
        get => _isAuthorizingTelegram;
        private set => SetField(ref _isAuthorizingTelegram, value);
    }

    private string? _telegramAuthError;
    public string? TelegramAuthError
    {
        get => _telegramAuthError;
        private set => SetField(ref _telegramAuthError, value);
    }

    public AsyncRelayCommand AuthorizeTelegramCommand { get; }
    public RelayCommand       LogoutTelegramCommand   { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(AppSettings settings, GoogleDriveUploadService uploader, TelegramUploadService telegram)
    {
        _settings = settings;
        _uploader = uploader;
        _telegram = telegram;

        _isTelegramAuthorized = telegram.IsAuthorized;
        _selectedTelegramChat = TelegramChats.FirstOrDefault(c => c.Id == settings.TelegramChatId)
                                ?? TelegramChats.FirstOrDefault();

        if (_selectedTelegramChat != null && settings.TelegramChatId == 0)
            settings.TelegramChatId = _selectedTelegramChat.Id;

        TestDriveConnectionCommand = new AsyncRelayCommand(TestDriveConnectionAsync);
        AuthorizeTelegramCommand   = new AsyncRelayCommand(AuthorizeTelegramAsync);
        LogoutTelegramCommand      = new RelayCommand(LogoutTelegram);

        _settings.PropertyChanged += OnSettingsChanged;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task TestDriveConnectionAsync()
    {
        IsDriveConnected     = null;
        DriveConnectionError = null;
        IsCheckingDrive      = true;
        string? error = await _uploader.TestFolderAccessAsync(_settings.GoogleDriveFolderId);
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsCheckingDrive      = false;
            IsDriveConnected     = error is null;
            DriveConnectionError = error;
        });
    }

    private async Task AuthorizeTelegramAsync()
    {
        TelegramAuthError = null;
        IsAuthorizingTelegram = true;
        var (ok, error) = await _telegram.AuthorizeAsync(_settings.TelegramPhone);
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsAuthorizingTelegram = false;
            IsTelegramAuthorized  = ok;
            TelegramAuthError     = error;
        });
    }

    private void LogoutTelegram()
    {
        _telegram.Logout();
        IsTelegramAuthorized = false;
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

    // ── Settings change relay ─────────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.MonitorMode):
                OnPropertyChanged(nameof(IsWindowMonitorActive));
                OnPropertyChanged(nameof(IsMicrophoneMonitorActive));
                break;
            case nameof(AppSettings.IsGoogleDriveEnabled):
                OnPropertyChanged(nameof(IsGoogleDriveEnabled));
                break;
            case nameof(AppSettings.GoogleDriveFolderId):
                OnPropertyChanged(nameof(GoogleDriveFolderId));
                IsDriveConnected     = null;
                DriveConnectionError = null;
                IsCheckingDrive      = false;
                break;
            case nameof(AppSettings.IsTelegramEnabled):
                OnPropertyChanged(nameof(IsTelegramEnabled));
                break;
            case nameof(AppSettings.TelegramPhone):
                OnPropertyChanged(nameof(TelegramPhone));
                break;
        }
    }
}
