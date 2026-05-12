using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services.Upload;
using Replixer.ViewModels.Dialogs;
using System.Windows;
using System.Windows.Input;

namespace Replixer.ViewModels;

public sealed class SetupViewModel : ViewModelBase, IDialogHost
{
    private readonly AppSettings _settings;
    private readonly GoogleDriveUploadService _driveUploader;
    private readonly TelegramUploadService _telegram;

    public event Action? SetupCompleted;

    // ── Manager name ──────────────────────────────────────────────────────────

    private string _managerName = string.Empty;
    public string ManagerName
    {
        get => _managerName;
        set
        {
            SetField(ref _managerName, value);
            OnPropertyChanged(nameof(CanFinish));
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanFinish => !string.IsNullOrWhiteSpace(_managerName);

    // ── Google Drive ──────────────────────────────────────────────────────────

    private string _googleDriveFolderId = string.Empty;
    public string GoogleDriveFolderId
    {
        get => _googleDriveFolderId;
        set
        {
            SetField(ref _googleDriveFolderId, value);
            IsDriveConnected = null;
            DriveConnectionError = null;
        }
    }

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

    public IReadOnlyList<TelegramChat> TelegramChats => TelegramUploadService.Chats;

    private TelegramChat? _selectedTelegramChat;
    public TelegramChat? SelectedTelegramChat
    {
        get => _selectedTelegramChat;
        set => SetField(ref _selectedTelegramChat, value);
    }

    private string _telegramPhone = string.Empty;
    public string TelegramPhone
    {
        get => _telegramPhone;
        set
        {
            SetField(ref _telegramPhone, value);
            IsTelegramConnected = null;
            TelegramAuthError = null;
        }
    }

    private bool? _isTelegramConnected;
    public bool? IsTelegramConnected
    {
        get => _isTelegramConnected;
        private set => SetField(ref _isTelegramConnected, value);
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

    // ── Dialog overlay (for Telegram verification code) ───────────────────────

    private ViewModelBase? _dialog;
    public ViewModelBase? Dialog
    {
        get => _dialog;
        private set => SetField(ref _dialog, value);
    }

    public void ShowInputDialog(InputDialogViewModel vm) => Dialog = vm;
    public void HideInputDialog() => Dialog = null;

    // ── Finish ────────────────────────────────────────────────────────────────

    public RelayCommand FinishCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public SetupViewModel(AppSettings settings, GoogleDriveUploadService driveUploader, TelegramUploadService telegram)
    {
        _settings      = settings;
        _driveUploader = driveUploader;
        _telegram      = telegram;

        _googleDriveFolderId  = settings.GoogleDriveFolderId;
        _telegramPhone        = settings.TelegramPhone;
        _isTelegramConnected  = telegram.IsAuthorized ? true : (bool?)null;
        _selectedTelegramChat = TelegramChats.FirstOrDefault(c => c.Id == settings.TelegramChatId)
                                ?? TelegramChats.FirstOrDefault();

        TestDriveConnectionCommand = new AsyncRelayCommand(TestDriveConnectionAsync);
        TelegramActionCommand      = new RelayCommand(() => _ = AuthorizeTelegramAsync());
        FinishCommand              = new RelayCommand(Finish, () => CanFinish);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task TestDriveConnectionAsync()
    {
        IsDriveConnected     = null;
        DriveConnectionError = null;
        IsCheckingDrive      = true;
        string? error = await _driveUploader.TestFolderAccessAsync(_googleDriveFolderId);
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsCheckingDrive      = false;
            IsDriveConnected     = error is null;
            DriveConnectionError = error;
        });
    }

    private async Task AuthorizeTelegramAsync()
    {
        TelegramAuthError     = null;
        IsAuthorizingTelegram = true;
        var (ok, error) = await _telegram.AuthorizeAsync(_telegramPhone);
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsAuthorizingTelegram = false;
            IsTelegramConnected   = ok ? true : (bool?)false;
            TelegramAuthError     = error;
        });
    }

    private void Finish()
    {
        _settings.ManagerName    = _managerName.Trim();
        _settings.UserFolderName = _managerName.Trim();

        if (!string.IsNullOrWhiteSpace(_googleDriveFolderId))
        {
            _settings.GoogleDriveFolderId    = _googleDriveFolderId;
            _settings.IsGoogleDriveEnabled   = _isDriveConnected == true;
            _settings.IsGoogleDriveConnected = _isDriveConnected;
        }

        if (!string.IsNullOrWhiteSpace(_telegramPhone))
        {
            _settings.TelegramPhone       = _telegramPhone;
            _settings.IsTelegramEnabled   = _isTelegramConnected == true;
            _settings.IsTelegramConnected = _isTelegramConnected;
            if (_selectedTelegramChat is not null)
                _settings.TelegramChatId = _selectedTelegramChat.Id;
        }

        _settings.IsSetupComplete = true;
        _settings.Flush();

        SetupCompleted?.Invoke();
    }
}
