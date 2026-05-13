using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services.Upload;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;

namespace Replixer.ViewModels;

public sealed class ProfileViewModel : ViewModelBase, IDisposable
{
    private readonly AppSettings _settings;
    private readonly GoogleDriveUploadService _uploader;
    private readonly TelegramUploadService _telegram;
    private readonly RecordingsViewModel _recordings;

    // ── Manager name ──────────────────────────────────────────────────────────

    public string ManagerName
    {
        get => _settings.ManagerName;
        set => _settings.ManagerName = value;
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

    private bool? _isDriveConnected;
    public bool? IsDriveConnected
    {
        get => _isDriveConnected;
        private set { SetField(ref _isDriveConnected, value); _settings.IsGoogleDriveConnected = value; }
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
            if (value != null)
            {
                _settings.TelegramChatId = value.Id;
                _settings.TelegramTopicId = value.TopicId;
            }
        }
    }

    private bool? _isTelegramConnected;
    public bool? IsTelegramConnected
    {
        get => _isTelegramConnected;
        private set { SetField(ref _isTelegramConnected, value); _settings.IsTelegramConnected = value; }
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

    public RelayCommand TelegramActionCommand { get; }
    public RelayCommand LogoutTelegramCommand { get; }

    // ── Clear all data ────────────────────────────────────────────────────────

    public RelayCommand ClearAllDataCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public ProfileViewModel(
        AppSettings settings,
        GoogleDriveUploadService uploader,
        TelegramUploadService telegram,
        RecordingsViewModel recordings)
    {
        _settings   = settings;
        _uploader   = uploader;
        _telegram   = telegram;
        _recordings = recordings;

        _isDriveConnected    = settings.IsGoogleDriveConnected;
        _isTelegramConnected = telegram.IsAuthorized ? true : settings.IsTelegramConnected;
        _selectedTelegramChat = TelegramChats.FirstOrDefault(c => c.Id == settings.TelegramChatId && c.TopicId == settings.TelegramTopicId);

        TestDriveConnectionCommand = new AsyncRelayCommand(TestDriveConnectionAsync);
        TelegramActionCommand      = new RelayCommand(TelegramAction);
        LogoutTelegramCommand      = new RelayCommand(LogoutTelegram);
        ClearAllDataCommand        = new RelayCommand(ClearAllData);

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

    private void TelegramAction() => _ = AuthorizeTelegramAsync();

    private async Task AuthorizeTelegramAsync()
    {
        TelegramAuthError     = null;
        IsAuthorizingTelegram = true;
        var (ok, error) = await _telegram.AuthorizeAsync(_settings.TelegramPhone);
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsAuthorizingTelegram = false;
            IsTelegramConnected   = ok ? true : (bool?)false;
            TelegramAuthError     = error;
        });
    }

    private void LogoutTelegram()
    {
        _telegram.Logout();
        IsTelegramConnected = null;
        TelegramAuthError   = null;
    }

    private void ClearAllData()
    {
        var result = MessageBox.Show(
            "Це видалить усі дані та перезавантажить додаток для повторного налаштування. Продовжити?",
            "Видалити всі дані",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes) return;

        _telegram.Logout();

        var recordingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Replixer", "recordings.json");
        if (File.Exists(recordingsPath))
            try { File.Delete(recordingsPath); } catch { }

        _settings.IsTelegramConnected    = null;
        _settings.IsTelegramEnabled      = false;
        _settings.TelegramPhone          = string.Empty;
        _settings.TelegramChatId         = 0;
        _settings.TelegramTopicId = null;
        _settings.IsGoogleDriveEnabled   = false;
        _settings.GoogleDriveFolderId    = string.Empty;
        _settings.IsGoogleDriveConnected = null;
        _settings.ManagerName            = string.Empty;
        _settings.UserFolderName         = string.Empty;
        _settings.UserFolderId           = string.Empty;
        _settings.IsSetupComplete        = false;
        _settings.Flush();

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exe != null)
            Process.Start(exe);

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    // ── IDisposable ───────────────────────────────────────────────────────────

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

    // ── Settings change relay ─────────────────────────────────────────────────

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ManagerName):
                OnPropertyChanged(nameof(ManagerName));
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
                IsTelegramConnected = null;
                TelegramAuthError   = null;
                break;
        }
    }
}
