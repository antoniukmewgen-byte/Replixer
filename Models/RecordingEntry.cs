using Replixer.Infrastructure;
using Replixer.ViewModels.Dialogs;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Replixer.Models;

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

    // Path to the original MP3 before upload (temp folder). Set immediately after recording,
    // before any upload attempt — allows retry even if upload never completed.
    private string? _sourcePath;
    public string? SourcePath
    {
        get => _sourcePath;
        set { if (_sourcePath == value) return; _sourcePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasRetryableFile)); }
    }

    public bool HasRetryableFile =>
        (!string.IsNullOrEmpty(_sourcePath) && File.Exists(_sourcePath)) ||
        (!string.IsNullOrEmpty(_filePath)   && File.Exists(_filePath));

    private int? _telegramMessageId;
    public int? TelegramMessageId
    {
        get => _telegramMessageId;
        set { if (_telegramMessageId == value) return; _telegramMessageId = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasTelegramMessage)); }
    }

    public long  TelegramChatId  { get; set; }
    public int?  TelegramTopicId { get; set; }
    public long? KommoNoteId     { get; set; }

    private CallReportData? _reportData;
    public CallReportData? ReportData
    {
        get => _reportData;
        set { if (_reportData == value) return; _reportData = value; OnPropertyChanged(); }
    }

    public bool HasTelegramMessage => _telegramMessageId.HasValue;

    public ICommand OpenInDriveCommand    { get; }
    public ICommand OpenInExplorerCommand { get; }

    private ICommand? _editReportCommand;
    public ICommand? EditReportCommand
    {
        get => _editReportCommand;
        set { if (_editReportCommand == value) return; _editReportCommand = value; OnPropertyChanged(); }
    }

    private ICommand? _retryCommand;
    public ICommand? RetryCommand
    {
        get => _retryCommand;
        set { if (_retryCommand == value) return; _retryCommand = value; OnPropertyChanged(); }
    }

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
            OnPropertyChanged(nameof(IsError));
        }
    }

    public bool IsError => _status == RecordingStatus.Error;

    // Single source of truth for all platform-specific data.
    // Adding a new platform only requires a new entry in each dictionary.
    private static readonly Dictionary<string, string> s_displayNames = new()
    {
        ["Telegram"]              = "Telegram",
        ["Viber"]                 = "Viber",
        ["WhatsApp.Root"]         = "WhatsApp",
        ["Ringostat Smart Phone"] = "Ringostat",
    };

    private static readonly Dictionary<string, string> s_iconPaths = new()
    {
        ["Telegram"]              = "/Assets/Icons/telegram.png",
        ["Viber"]                 = "/Assets/Icons/viber.png",
        ["WhatsApp.Root"]         = "/Assets/Icons/whatsapp.png",
        ["Ringostat Smart Phone"] = "/Assets/Icons/ringostat.png",
    };

    public bool    IsManual           => !s_displayNames.ContainsKey(Platform);
    public string  PlatformDisplayName => s_displayNames.TryGetValue(Platform, out var n) ? n : "Ручний запис";
    public string? IconPath            => s_iconPaths.TryGetValue(Platform, out var p) ? p : null;

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
            () => Process.Start(new ProcessStartInfo(_driveUrl!) { UseShellExecute = true }));

        OpenInExplorerCommand = new RelayCommand(
            () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = true }));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
