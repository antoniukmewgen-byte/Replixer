using System.ComponentModel;
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

    // ── Properties ───────────────────────────────────────────────────────────

    private MonitorMode _monitorMode = MonitorMode.Window;

    public MonitorMode MonitorMode
    {
        get => _monitorMode;
        set
        {
            if (_monitorMode == value) return;
            _monitorMode = value;
            OnPropertyChanged();
            Save();
        }
    }

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

    // ── Persistence ──────────────────────────────────────────────────────────

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                return JsonSerializer.Deserialize<AppSettings>(json, JsonOptions)
                       ?? new AppSettings();
            }
        }
        catch { /* corrupt file — fall back to defaults */ }

        return new AppSettings();
    }

    private Timer? _saveDebounce;

    private void Save()
    {
        // Debounce: coalesce rapid successive saves (e.g. user typing in a text field)
        // into a single write 500 ms after the last change.
        _saveDebounce?.Dispose();
        _saveDebounce = new Timer(_ => WriteToDisk(), null, dueTime: 500, period: Timeout.Infinite);
    }

    private void WriteToDisk()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch { }
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
