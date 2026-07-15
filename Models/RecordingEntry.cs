using Replixer.Infrastructure;
using Replixer.ViewModels.Dialogs;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Replixer.Models;

public enum RecordingStatus { Loading, Saved, Error, Draft }

public class RecordingEntry : INotifyPropertyChanged
{
    public string   Platform  { get; }
    public DateTime StartedAt { get; }

    private string? _driveUrl;
    public string? DriveUrl
    {
        get => _driveUrl;
        set
        {
            if (_driveUrl == value) return;
            _driveUrl = value;
            OnPropertyChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private string? _filePath;
    public string? FilePath
    {
        get => _filePath;
        set
        {
            if (_filePath == value) return;
            _filePath = value;
            OnPropertyChanged();
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        }
    }

    private string? _sourcePath;
    public string? SourcePath
    {
        get => _sourcePath;
        set
        {
            if (_sourcePath == value) return;
            _sourcePath = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasRetryableFile));
        }
    }

    public bool HasRetryableFile =>
        (!string.IsNullOrEmpty(_sourcePath) && File.Exists(_sourcePath)) ||
        (!string.IsNullOrEmpty(_filePath)   && File.Exists(_filePath));

    private int? _telegramMessageId;
    public int? TelegramMessageId
    {
        get => _telegramMessageId;
        set
        {
            if (_telegramMessageId == value) return;
            _telegramMessageId = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasTelegramMessage));
        }
    }

    private long _telegramChatId;
    public long TelegramChatId
    {
        get => _telegramChatId;
        set { if (_telegramChatId == value) return; _telegramChatId = value; OnPropertyChanged(); }
    }

    private int? _telegramTopicId;
    public int? TelegramTopicId
    {
        get => _telegramTopicId;
        set { if (_telegramTopicId == value) return; _telegramTopicId = value; OnPropertyChanged(); }
    }

    private long? _kommoNoteId;
    public long? KommoNoteId
    {
        get => _kommoNoteId;
        set { if (_kommoNoteId == value) return; _kommoNoteId = value; OnPropertyChanged(); }
    }

    private CallReportData? _reportData;
    public CallReportData? ReportData
    {
        get => _reportData;
        set { if (_reportData == value) return; _reportData = value; OnPropertyChanged(); }
    }

    public bool HasTelegramMessage => _telegramMessageId.HasValue;

    // ── Прапорці "цей крок мав відбутись, але не вдався (мережа/сервіс)" ────
    // На відміну від DriveUrl/TelegramMessageId/KommoNoteId == null (що може означати
    // і "крок не потрібен", і "крок впав"), ці прапорці однозначно кажуть фоновому
    // PendingUploadRetryService, що саме треба спробувати доробити.
    // Персистяться разом з рештою запису в recordings.json.

    private bool _driveFailed;
    public bool DriveFailed
    {
        get => _driveFailed;
        set { if (_driveFailed == value) return; _driveFailed = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsBackgroundRetry)); }
    }

    private bool _telegramFailed;
    public bool TelegramFailed
    {
        get => _telegramFailed;
        set { if (_telegramFailed == value) return; _telegramFailed = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsBackgroundRetry)); }
    }

    private bool _kommoFailed;
    public bool KommoFailed
    {
        get => _kommoFailed;
        set { if (_kommoFailed == value) return; _kommoFailed = value; OnPropertyChanged(); OnPropertyChanged(nameof(NeedsBackgroundRetry)); }
    }

    // True, якщо є хоч один незавершений крок і файл ще фізично існує — саме такі
    // записи фоновий сервіс тихо добиває, без діалогів і участі користувача.
    // ReportData має бути заповненим — якщо запис ще "Draft" (форма не заповнена),
    // фоновий retry його не займає, це прерогатива ручного "Дозаповнити".
    public bool NeedsBackgroundRetry =>
        (_driveFailed || _telegramFailed || _kommoFailed) &&
        _reportData is not null &&
        HasRetryableFile;

    // Позначено, що фоновий сервіс саме зараз обробляє цей запис — щоб головний потік
    // (ручний Retry/новий запис) не зачепив той самий entry одночасно.
    // Транзієнтне поле, у recordings.json (через RecordingDto) не потрапляє.
    // PropertyChanged свідомо НЕ піднімаємо (не забайндовано в XAML), але щоб кнопки
    // Retry/Редагувати миттєво (без очікування на випадкову UI-подію) перевірили
    // CanExecute, явно штовхаємо CommandManager.RequerySuggested при зміні значення.
    private bool _isBackgroundRetrying;
    public bool IsBackgroundRetrying
    {
        get => _isBackgroundRetrying;
        set
        {
            if (_isBackgroundRetrying == value) return;
            _isBackgroundRetrying = value;
            CommandManager.InvalidateRequerySuggested();
        }
    }

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

    private ICommand? _resumeDraftCommand;
    public ICommand? ResumeDraftCommand
    {
        get => _resumeDraftCommand;
        set { if (_resumeDraftCommand == value) return; _resumeDraftCommand = value; OnPropertyChanged(); }
    }

    private TimeSpan _callDuration;
    public TimeSpan CallDuration
    {
        get => _callDuration;
        set { if (_callDuration == value) return; _callDuration = value; OnPropertyChanged(); }
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
            OnPropertyChanged(nameof(IsDraft));
        }
    }

    public bool IsError => _status == RecordingStatus.Error;
    public bool IsDraft => _status == RecordingStatus.Draft;

    public bool    IsManual            => !PlatformHelper.IsKnownPlatform(Platform);

    public string  PlatformDisplayName => PlatformHelper.ToDisplayName(Platform);

    public string? IconPath            => PlatformHelper.ToRelativeIconPath(Platform);

    public string DateDisplay      => StartedAt.ToString("dd.MM.yyyy");
    public string TimeOfDayDisplay => StartedAt.ToString("HH:mm");

    public string StatusText => Status switch
    {
        RecordingStatus.Loading => "Завантаження...",
        RecordingStatus.Saved   => "Збережено та відправлено",
        RecordingStatus.Error   => "Помилка",
        RecordingStatus.Draft   => "Дозаповніть форму",
        _                       => string.Empty
    };

    public RecordingEntry(string platform) : this(platform, DateTime.Now) { }

    public RecordingEntry(string platform, DateTime startedAt)
    {
        Platform  = platform;
        StartedAt = startedAt;

        OpenInDriveCommand = new RelayCommand(
            execute:    () => Process.Start(new ProcessStartInfo(_driveUrl!) { UseShellExecute = true }),
            canExecute: () => !string.IsNullOrEmpty(_driveUrl));

        OpenInExplorerCommand = new RelayCommand(
            execute:    () => Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{_filePath}\"") { UseShellExecute = true }),
            canExecute: () => !string.IsNullOrEmpty(_filePath) && File.Exists(_filePath));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
