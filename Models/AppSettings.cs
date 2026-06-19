using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Replixer.Models;

public class AppSettings : INotifyPropertyChanged
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "settings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private string _recordingsFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        "Replixer", "Recordings");

    public string RecordingsFolder
    {
        get => _recordingsFolder;
        set
        {
            if (_recordingsFolder == value) return;
            _recordingsFolder = value;
            OnPropertyChanged();
            Save();
        }
    }

    private bool _isGoogleDriveEnabled = false;
    public bool IsGoogleDriveEnabled
    {
        get => _isGoogleDriveEnabled;
        set { if (_isGoogleDriveEnabled == value) return; _isGoogleDriveEnabled = value; OnPropertyChanged(); Save(); }
    }

    private string _googleDriveFolderId = string.Empty;
    public string GoogleDriveFolderId
    {
        get => _googleDriveFolderId;
        set { if (_googleDriveFolderId == value) return; _googleDriveFolderId = value; OnPropertyChanged(); Save(); }
    }

    private bool? _isGoogleDriveConnected;
    public bool? IsGoogleDriveConnected
    {
        get => _isGoogleDriveConnected;
        set { if (_isGoogleDriveConnected == value) return; _isGoogleDriveConnected = value; OnPropertyChanged(); Save(); }
    }

    private bool _isTelegramEnabled = false;
    public bool IsTelegramEnabled
    {
        get => _isTelegramEnabled;
        set { if (_isTelegramEnabled == value) return; _isTelegramEnabled = value; OnPropertyChanged(); Save(); }
    }

    private string _telegramPhone = string.Empty;
    public string TelegramPhone
    {
        get => _telegramPhone;
        set { if (_telegramPhone == value) return; _telegramPhone = value; OnPropertyChanged(); Save(); }
    }

    private long _telegramChatId = 0;
    public long TelegramChatId
    {
        get => _telegramChatId;
        set { if (_telegramChatId == value) return; _telegramChatId = value; OnPropertyChanged(); Save(); }
    }

    private int? _telegramTopicId;
    public int? TelegramTopicId
    {
        get => _telegramTopicId;
        set {if (_telegramTopicId == value) return; _telegramTopicId = value; OnPropertyChanged(); Save(); }
    }

    private bool? _isTelegramConnected;
    public bool? IsTelegramConnected
    {
        get => _isTelegramConnected;
        set { if (_isTelegramConnected == value) return; _isTelegramConnected = value; OnPropertyChanged(); Save(); }
    }

    private string _userFolderName = string.Empty;
    public string UserFolderName
    {
        get => _userFolderName;
        set { if (_userFolderName == value) return; _userFolderName = value; OnPropertyChanged(); Save(); }
    }

    private string _userFolderId = string.Empty;
    public string UserFolderId
    {
        get => _userFolderId;
        set { if (_userFolderId == value) return; _userFolderId = value; OnPropertyChanged(); Save(); }
    }

    private bool _isAutoStartEnabled = true;
    public bool IsAutoStartEnabled
    {
        get => _isAutoStartEnabled;
        set { if (_isAutoStartEnabled == value) return; _isAutoStartEnabled = value; OnPropertyChanged(); Save(); }
    }

    private bool _isSetupComplete = false;
    public bool IsSetupComplete
    {
        get => _isSetupComplete;
        set { if (_isSetupComplete == value) return; _isSetupComplete = value; OnPropertyChanged(); Save(); }
    }

    private string _managerName = string.Empty;
    public string ManagerName
    {
        get => _managerName;
        set { if (_managerName == value) return; _managerName = value; OnPropertyChanged(); Save(); }
    }

    private string _position = "Менеджер";
    public string Position
    {
        get => _position;
        set { if (_position == value) return; _position = value; OnPropertyChanged(); Save(); }
    }

    private bool _isKommoEnabled = false;
    public bool IsKommoEnabled
    {
        get => _isKommoEnabled;
        set { if (_isKommoEnabled == value) return; _isKommoEnabled = value; OnPropertyChanged(); Save(); }
    }

    private string _kommoSubdomain = string.Empty;
    public string KommoSubdomain
    {
        get => _kommoSubdomain;
        set { if (_kommoSubdomain == value) return; _kommoSubdomain = value; OnPropertyChanged(); Save(); }
    }

    private string _kommoApiToken = string.Empty;
    public string KommoApiToken
    {
        get => _kommoApiToken;
        set { if (_kommoApiToken == value) return; _kommoApiToken = value; OnPropertyChanged(); Save(); }
    }

    private bool? _isKommoConnected;
    public bool? IsKommoConnected
    {
        get => _isKommoConnected;
        set { if (_isKommoConnected == value) return; _isKommoConnected = value; OnPropertyChanged(); Save(); }
    }

    private bool _isNotificationsEnabled = true;
    public bool IsNotificationsEnabled
    {
        get => _isNotificationsEnabled;
        set { if (_isNotificationsEnabled == value) return; _isNotificationsEnabled = value; OnPropertyChanged(); Save(); }
    }


    private SynchronizationContext? _uiContext;
    private Timer? _saveDebounce;
    private CancellationTokenSource? _saveCts;
    private readonly object _saveSyncLock = new();

    public AppSettings()
    {
        _uiContext = SynchronizationContext.Current;
    }

    public void InitializeDispatch()
        => _uiContext ??= SynchronizationContext.Current;

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var settings = JsonSerializer.Deserialize<AppSettings>(
                    File.ReadAllText(SettingsPath), JsonOptions);
                if (settings is not null)
                    return settings;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] Load failed: {ex.Message}");
        }
        return new AppSettings();
    }

    private void Save()
    {
        lock (_saveSyncLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = new CancellationTokenSource();
            var token = _saveCts.Token;

            _saveDebounce?.Dispose();
            _saveDebounce = new Timer(_ =>
            {
                if (token.IsCancellationRequested) return;

                if (_uiContext is not null)
                    _uiContext.Post(_ =>
                    {
                        if (token.IsCancellationRequested) return;
                        var json = JsonSerializer.Serialize(this, JsonOptions);
                        _ = PersistAsync(json, token);
                    }, null);
                else
                    WriteToDisk();
            }, null, dueTime: 500, period: Timeout.Infinite);
        }
    }

    public void Flush()
    {
        lock (_saveSyncLock)
        {
            _saveCts?.Cancel();
            _saveCts?.Dispose();
            _saveCts = null;
            _saveDebounce?.Dispose();
            _saveDebounce = null;
        }
        WriteToDisk();
    }

    private void WriteToDisk()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] WriteToDisk failed: {ex.Message}");
        }
    }

    private static async Task PersistAsync(string json, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            await File.WriteAllTextAsync(SettingsPath, json, ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Debug.WriteLine($"[AppSettings] PersistAsync failed: {ex.Message}");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
