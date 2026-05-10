using EchoVault.Infrastructure;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace EchoVault.Models;

public enum RecordingStatus { Loading, Saved, Error }

public class RecordingEntry : INotifyPropertyChanged
{
    public string   Platform  { get; }
    public DateTime StartedAt { get; }

    private string? _driveUrl;
    public string? DriveUrl
    {
        get => _driveUrl;
        set { if (_driveUrl == value) return; _driveUrl = value; OnPropertyChanged(); }
    }

    private string? _filePath;
    public string? FilePath
    {
        get => _filePath;
        set { if (_filePath == value) return; _filePath = value; OnPropertyChanged(); }
    }

    public ICommand OpenInDriveCommand    { get; }
    public ICommand OpenInExplorerCommand { get; }

    private RecordingStatus _status = RecordingStatus.Loading;
    public RecordingStatus Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    public bool IsManual => Platform is not ("Telegram" or "Viber" or "WhatsApp.Root" or "Ringostat Smart Phone");

    public string PlatformDisplayName => Platform switch
    {
        "WhatsApp.Root"        => "WhatsApp",
        "Telegram"             => "Telegram",
        "Viber"                => "Viber",
        "Ringostat Smart Phone" => "Ringostat",
        _                      => "Ручний запис"
    };

    public string? IconPath => Platform switch
    {
        "Telegram"              => "/Assets/Icons/telegram.png",
        "Viber"                 => "/Assets/Icons/viber.png",
        "WhatsApp.Root"         => "/Assets/Icons/whatsapp.png",
        "Ringostat Smart Phone" => "/Assets/Icons/ringostat.png",
        _                       => null
    };

    public string DateDisplay      => StartedAt.ToString("dd.MM.yyyy");
    public string TimeOfDayDisplay => StartedAt.ToString("HH:mm");

    public string StatusText => Status switch
    {
        RecordingStatus.Loading => "Завантаження...",
        RecordingStatus.Saved   => "Збережено та відправлено",
        RecordingStatus.Error   => "Помилка",
        _                       => string.Empty
    };

    public RecordingEntry(string platform) : this(platform, DateTime.Now) { }

    public RecordingEntry(string platform, DateTime startedAt)
    {
        Platform  = platform;
        StartedAt = startedAt;
        OpenInDriveCommand = new RelayCommand(
            () => Process.Start(new ProcessStartInfo("chrome.exe", _driveUrl!) { UseShellExecute = true }),
            () => !string.IsNullOrEmpty(_driveUrl));

        OpenInExplorerCommand = new RelayCommand(
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = true }),
            () => !string.IsNullOrEmpty(_filePath));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
