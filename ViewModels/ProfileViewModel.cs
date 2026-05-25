using Replixer.Infrastructure;
using Replixer.Models;
using Replixer.Services.Upload;
using Replixer.ViewModels.Dialogs;
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
    private readonly KommoService _kommo;
    private readonly RecordingsViewModel _recordings;

    public static IReadOnlyList<string> Positions => Dialogs.CallReportViewModel.Positions;

    public string ManagerName
    {
        get => _settings.ManagerName;
        set => _settings.ManagerName = value;
    }

    public string Position
    {
        get => _settings.Position;
        set => _settings.Position = value;
    }

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

    public bool IsTelegramVisible => PositionPolicy.IsTelegramVisible(_settings.Position);

    public IReadOnlyList<TelegramChat> FilteredTelegramChats =>
        _settings.Position == "Кваліфікатор"
            ? TelegramChats.Where(c => c.Name == "Чат Kvalifikatory Team").ToList()
            : TelegramChats;

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

    public bool IsKommoEnabled
    {
        get => _settings.IsKommoEnabled;
        set => _settings.IsKommoEnabled = value;
    }

    public string KommoSubdomain
    {
        get => _settings.KommoSubdomain;
        set => _settings.KommoSubdomain = value;
    }

    public string KommoApiToken
    {
        get => _settings.KommoApiToken;
        set => _settings.KommoApiToken = value;
    }

    private bool? _isKommoConnected;
    public bool? IsKommoConnected
    {
        get => _isKommoConnected;
        private set { SetField(ref _isKommoConnected, value); _settings.IsKommoConnected = value; }
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

    public RelayCommand ClearAllDataCommand { get; }

    public ProfileViewModel(
        AppSettings settings,
        GoogleDriveUploadService uploader,
        TelegramUploadService telegram,
        KommoService kommo,
        RecordingsViewModel recordings)
    {
        _settings   = settings;
        _uploader   = uploader;
        _telegram   = telegram;
        _kommo      = kommo;
        _recordings = recordings;

        _isDriveConnected    = settings.IsGoogleDriveConnected;
        _isTelegramConnected = telegram.IsAuthorized ? true : settings.IsTelegramConnected;
        _isKommoConnected    = settings.IsKommoConnected;
        _selectedTelegramChat = settings.Position == "Кваліфікатор"
            ? TelegramChats.FirstOrDefault(c => c.Name == "Чат Kvalifikatory Team")
            : TelegramChats.FirstOrDefault(c => c.Id == settings.TelegramChatId && c.TopicId == settings.TelegramTopicId);

        TestDriveConnectionCommand = new AsyncRelayCommand(TestDriveConnectionAsync);
        TelegramActionCommand      = new RelayCommand(TelegramAction);
        LogoutTelegramCommand      = new RelayCommand(LogoutTelegram);
        TestKommoConnectionCommand = new AsyncRelayCommand(TestKommoConnectionAsync);
        ClearAllDataCommand        = new RelayCommand(ClearAllData);

        _settings.PropertyChanged += OnSettingsChanged;
    }

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

    private async Task TestKommoConnectionAsync()
    {
        IsKommoConnected     = null;
        KommoConnectionError = null;
        IsCheckingKommo      = true;
        string? error = await _kommo.TestConnectionAsync(_settings.KommoSubdomain, _settings.KommoApiToken);
        Application.Current.Dispatcher.Invoke(() =>
        {
            IsCheckingKommo      = false;
            IsKommoConnected     = error is null;
            KommoConnectionError = error;
        });
    }

    private void TelegramAction() => _ = AuthorizeTelegramAsync();

    private async Task AuthorizeTelegramAsync()
    {
        _telegram.InputHandler ??= HandleTelegramInputAsync;
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

    private Task<string?> HandleTelegramInputAsync(string prompt)
    {
        if (Application.Current.MainWindow?.DataContext is not IDialogHost mainVm)
        {
            Debug.WriteLine("[Profile] HandleTelegramInputAsync: MainWindow is not an IDialogHost — cannot show input dialog");
            return Task.FromResult<string?>(null);
        }

        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm  = new InputDialogViewModel(prompt, result =>
        {
            mainVm.HideInputDialog();
            tcs.TrySetResult(result);
        });
        mainVm.ShowInputDialog(vm);
        return tcs.Task;
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
        _settings.Position               = "Менеджер";
        _settings.UserFolderName         = string.Empty;
        _settings.UserFolderId           = string.Empty;
        _settings.IsKommoEnabled   = false;
        _settings.KommoSubdomain   = string.Empty;
        _settings.KommoApiToken    = string.Empty;
        _settings.IsKommoConnected = null;
        _settings.IsSetupComplete = false;
        _settings.Flush();

        var exe = Environment.ProcessPath ?? Process.GetCurrentProcess().MainModule?.FileName;
        if (exe != null)
            Process.Start(exe);

        Application.Current.Dispatcher.Invoke(() => Application.Current.Shutdown());
    }

    public void Dispose() => _settings.PropertyChanged -= OnSettingsChanged;

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppSettings.ManagerName):
                OnPropertyChanged(nameof(ManagerName));
                break;
            case nameof(AppSettings.Position):
                OnPropertyChanged(nameof(Position));
                OnPropertyChanged(nameof(FilteredTelegramChats));
                OnPropertyChanged(nameof(IsTelegramVisible));
                if (_settings.Position == "Кваліфікатор")
                {
                    var kChat = TelegramChats.FirstOrDefault(c => c.Name == "Чат Kvalifikatory Team");
                    if (kChat is not null) SelectedTelegramChat = kChat;
                }
                if (_settings.Position == "Діагност")
                {
                    _telegram.Logout();
                    _settings.IsTelegramEnabled   = false;
                    _settings.IsTelegramConnected = null;
                    IsTelegramConnected           = null;
                }
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
            case nameof(AppSettings.IsKommoEnabled):
                OnPropertyChanged(nameof(IsKommoEnabled));
                break;
            case nameof(AppSettings.KommoSubdomain):
                OnPropertyChanged(nameof(KommoSubdomain));
                IsKommoConnected     = null;
                KommoConnectionError = null;
                IsCheckingKommo      = false;
                break;
            case nameof(AppSettings.KommoApiToken):
                OnPropertyChanged(nameof(KommoApiToken));
                IsKommoConnected     = null;
                KommoConnectionError = null;
                IsCheckingKommo      = false;
                break;
        }
    }
}
