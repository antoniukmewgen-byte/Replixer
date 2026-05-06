using EchoVault.Infrastructure;
using EchoVault.Models;
using EchoVault.Services.Upload;
using System.ComponentModel;

namespace EchoVault.ViewModels;

public class SettingsViewModel : ViewModelBase
{
    private readonly AppSettings _settings;
    private readonly GoogleDriveUploadService _uploader;

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

    // ── Constructor ───────────────────────────────────────────────────────────

    public SettingsViewModel(AppSettings settings, GoogleDriveUploadService uploader)
    {
        _settings = settings;
        _uploader = uploader;

        _isGoogleAuthorized = uploader.IsAuthorized;

        AuthorizeCommand = new AsyncRelayCommand(AuthorizeAsync);

        _settings.PropertyChanged += OnSettingsChanged;
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    private async Task AuthorizeAsync()
    {
        bool ok = await _uploader.AuthorizeAsync();

        // Google SDK uses ConfigureAwait(false) internally — ensure UI update
        // happens on the dispatcher thread so WPF binding picks it up
        System.Windows.Application.Current.Dispatcher.Invoke(()
            => IsGoogleAuthorized = ok);
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
        }
    }
}
