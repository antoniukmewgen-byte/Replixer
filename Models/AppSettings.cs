using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoVault.Models;

public class AppSettings : INotifyPropertyChanged
{
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EchoVault", "settings.json");

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
        "EchoVault", "Recordings");

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

    private void Save()
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
