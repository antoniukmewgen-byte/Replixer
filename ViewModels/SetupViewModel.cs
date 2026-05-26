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
    private readonly KommoService _kommo;

    public event Action? SetupCompleted;

    public static IReadOnlyList<string> Positions => Dialogs.CallReportViewModel.Positions;

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

    private string? _position = null;
    public string? Position
    {
        get => _position;
        set
        {
            if (!SetField(ref _position, value)) return;
            OnPropertyChanged(nameof(FilteredTelegramChats));
            OnPropertyChanged(nameof(IsTelegramVisible));
            OnPropertyChanged(nameof(CanFinish));
            CommandManager.InvalidateRequerySuggested();
            if (_position == "Кваліфікатор")
                SelectedTelegramChat = TelegramChats.FirstOrDefault(c => c.Name == "Чат Kvalifikatory Team");
            if (_position == "Діагност")
            {
                _telegram.Logout();
                IsTelegramConnected = null;
                TelegramAuthError   = null;
            }
        }
    }

    public bool CanFinish => !string.IsNullOrWhiteSpace(_managerName) && !string.IsNullOrWhiteSpace(_position);

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

    public IReadOnlyList<TelegramChat> TelegramChats => TelegramUploadService.Chats;

    public bool IsTelegramVisible => PositionPolicy.IsTelegramVisible(_position);

    public IReadOnlyList<TelegramChat> FilteredTelegramChats =>
        _position == "Кваліфікатор"
            ? TelegramChats.Where(c => c.Name == "Чат Kvalifikatory Team").ToList()
            : TelegramChats;

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

    private string _kommoSubdomain = string.Empty;
    public string KommoSubdomain
    {
        get => _kommoSubdomain;
        set
        {
            SetField(ref _kommoSubdomain, value);
            IsKommoConnected     = null;
            KommoConnectionError = null;
        }
    }

    private string _kommoApiToken = string.Empty;
    public string KommoApiToken
    {
        get => _kommoApiToken;
        set
        {
            SetField(ref _kommoApiToken, value);
            IsKommoConnected     = null;
            KommoConnectionError = null;
        }
    }

    private bool? _isKommoConnected;
    public bool? IsKommoConnected
    {
        get => _isKommoConnected;
        private set => SetField(ref _isKommoConnected, value);
    }

    private bool _isCheckingKommo;
    public bool IsCheckingKommo
    {
        get => _isCheckingKommo;
        private set => SetField(ref _isCheckingKommo, value);
    }

    private string? _kommoConnectionError;
    public string? KommoConnectionError
    {
        get => _kommoConnectionError;
        private set => SetField(ref _kommoConnectionError, value);
    }

    public AsyncRelayCommand TestKommoConnectionCommand { get; }

    private ViewModelBase? _dialog;
    public ViewModelBase? Dialog
    {
        get => _dialog;
        private set => SetField(ref _dialog, value);
    }

    public void ShowInputDialog(InputDialogViewModel vm) => Dialog = vm;
    public void HideInputDialog() => Dialog = null;

    public RelayCommand FinishCommand { get; }

    public SetupViewModel(AppSettings settings, GoogleDriveUploadService driveUploader, TelegramUploadService telegram, KommoService kommo)
    {
        _settings      = settings;
        _driveUploader = driveUploader;
        _telegram      = telegram;
        _kommo         = kommo;

        if (settings.IsSetupComplete)
        {
            _managerName = settings.ManagerName;
            _position    = string.IsNullOrWhiteSpace(settings.Position) ? null : settings.Position;
        }

        _googleDriveFolderId  = settings.GoogleDriveFolderId;
        _telegramPhone        = settings.TelegramPhone;
        _isTelegramConnected  = telegram.IsAuthorized ? true : (bool?)null;
        _selectedTelegramChat = _position == "Кваліфікатор"
            ? TelegramChats.FirstOrDefault(c => c.Name == "Чат Kvalifikatory Team")
            : TelegramChats.FirstOrDefault(c => c.Id == settings.TelegramChatId && c.TopicId == settings.TelegramTopicId);
        _kommoSubdomain       = settings.KommoSubdomain;
        _kommoApiToken        = settings.KommoApiToken;

        TestDriveConnectionCommand = new AsyncRelayCommand(TestDriveConnectionAsync);
        TelegramActionCommand      = new RelayCommand(() => _ = AuthorizeTelegramAsync());
        TestKommoConnectionCommand = new AsyncRelayCommand(TestKommoConnectionAsync);
        FinishCommand              = new RelayCommand(Finish, () => CanFinish);
    }

    private async Task TestDriveConnectionAsync()
    {
        IsDriveConnected     = null;
        DriveConnectionError = null;
        IsCheckingDrive      = true;
        string? error = await _driveUploader.TestFolderAccessAsync(_googleDriveFolderId);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsCheckingDrive      = false;
            IsDriveConnected     = error is null;
            DriveConnectionError = error;
        });
    }

    private async Task AuthorizeTelegramAsync()
    {
        _telegram.InputHandler = HandleTelegramInputAsync;
        TelegramAuthError     = null;
        IsAuthorizingTelegram = true;
        try
        {
            var (ok, error) = await _telegram.AuthorizeAsync(_telegramPhone);
            await Application.Current.Dispatcher.InvokeAsync(() =>
            {
                IsAuthorizingTelegram = false;
                IsTelegramConnected   = ok ? true : (bool?)false;
                TelegramAuthError     = error;
            });
        }
        finally
        {
            _telegram.InputHandler = null;
        }
    }

    private Task<string?> HandleTelegramInputAsync(string prompt)
    {
        var tcs = new TaskCompletionSource<string?>();
        var vm  = new InputDialogViewModel(prompt, result =>
        {
            HideInputDialog();
            tcs.TrySetResult(result);
        });
        ShowInputDialog(vm);
        return tcs.Task;
    }

    private async Task TestKommoConnectionAsync()
    {
        IsKommoConnected     = null;
        KommoConnectionError = null;
        IsCheckingKommo      = true;
        string? error = await _kommo.TestConnectionAsync(_kommoSubdomain, _kommoApiToken);
        await Application.Current.Dispatcher.InvokeAsync(() =>
        {
            IsCheckingKommo      = false;
            IsKommoConnected     = error is null;
            KommoConnectionError = error;
        });
    }

    private void Finish()
    {
        _settings.ManagerName    = _managerName.Trim();
        _settings.Position       = _position!;
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
            {
                _settings.TelegramChatId  = _selectedTelegramChat.Id;
                _settings.TelegramTopicId = _selectedTelegramChat.TopicId;
            }
        }

        if (!string.IsNullOrWhiteSpace(_kommoSubdomain) || !string.IsNullOrWhiteSpace(_kommoApiToken))
        {
            _settings.KommoSubdomain   = _kommoSubdomain;
            _settings.KommoApiToken    = _kommoApiToken;
            _settings.IsKommoEnabled   = _isKommoConnected == true;
            _settings.IsKommoConnected = _isKommoConnected;
        }

        _settings.IsSetupComplete = true;
        _settings.Flush();

        SetupCompleted?.Invoke();
    }
}
