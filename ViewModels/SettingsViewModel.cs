using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services.Upload;
using System.ComponentModel;
using System.Windows;

namespace Replixer.ViewModels;

public class SettingsViewModel : ViewModelBase
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

    private bool _isGoogleAuthorized;
    public bool IsGoogleAuthorized
    {
        get => _isGoogleAuthorized;
        private set => SetField(ref _isGoogleAuthorized, value);
    }

    public AsyncRelayCommand AuthorizeCommand { get; }

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

    public AsyncRelayCommand AuthorizeTelegramCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(AppSettings settings, GoogleDriveUploadService uploader, TelegramUploadService telegram)
    {
        _settings = settings;
        _uploader = uploader;
        _telegram = telegram;

        _isGoogleAuthorized   = uploader.IsAuthorized;
        _isTelegramAuthorized = telegram.IsAuthorized;
        _selectedTelegramChat = TelegramChats.FirstOrDefault(c => c.Id == settings.TelegramChatId)
                                ?? TelegramChats.FirstOrDefault();

        if (_selectedTelegramChat != null && settings.TelegramChatId == 0)
            settings.TelegramChatId = _selectedTelegramChat.Id;

        AuthorizeCommand         = new AsyncRelayCommand(AuthorizeGoogleAsync);
        AuthorizeTelegramCommand = new AsyncRelayCommand(AuthorizeTelegramAsync);

        _settings.PropertyChanged += OnSettingsChanged;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task AuthorizeGoogleAsync()
    {
        bool ok = await _uploader.AuthorizeAsync();

        // Google SDK uses ConfigureAwait(false) internally — ensure UI update
        // happens on the dispatcher thread so WPF binding picks it up
        Application.Current.Dispatcher.Invoke(() => IsGoogleAuthorized = ok);
    }

    private async Task AuthorizeTelegramAsync()
    {
        bool ok = await _telegram.AuthorizeAsync(_settings.TelegramPhone);
        Application.Current.Dispatcher.Invoke(() => IsTelegramAuthorized = ok);
    }

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
