using Replixer.Models;
using Replixer.ViewModels.Dialogs;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;

namespace Replixer.ViewModels;

public class RecordingsViewModel : ViewModelBase
{
    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "recordings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private List<RecordingDto>? _pendingSave;
    private bool _loading;

    public ObservableCollection<RecordingEntry> Recordings { get; } = new();

    public bool IsEmpty => Recordings.Count == 0;

    public IReadOnlyList<RecordingEntry> RecentRecordings => Recordings.Take(4).ToList();

    public RecordingsViewModel()
    {
        Load();
        Recordings.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(RecentRecordings));
            ScheduleSave();
        };
    }

    // Must be called on the UI thread.
    public RecordingEntry AddEntry(string platform)
    {
        var entry = new RecordingEntry(platform);
        SubscribeEntry(entry);
        Recordings.Insert(0, entry);
        return entry;
    }

    private void SubscribeEntry(RecordingEntry entry)
        => entry.PropertyChanged += (_, _) => ScheduleSave();

    // ── Persistence ───────────────────────────────────────────────────────────

    private void Load()
    {
        _loading = true;
        try
        {
            if (!File.Exists(SavePath)) return;

            var dtos = JsonSerializer.Deserialize<List<RecordingDto>>(
                File.ReadAllText(SavePath), JsonOptions);

            if (dtos is null) return;

            foreach (var dto in dtos)
            {
                var entry = new RecordingEntry(dto.Platform, dto.StartedAt)
                {
                    Status            = dto.Status,
                    DriveUrl          = dto.DriveUrl,
                    FilePath          = dto.FilePath,
                    TelegramMessageId = dto.TelegramMessageId,
                    TelegramChatId    = dto.TelegramChatId,
                    TelegramTopicId   = dto.TelegramTopicId,
                    ReportData        = dto.ReportData,
                };
                SubscribeEntry(entry);
                Recordings.Add(entry);
            }
        }
        catch { /* corrupt file — start fresh */ }
        finally
        {
            _loading = false;
        }
    }

    private void ScheduleSave()
    {
        if (_loading) return;

        // Snapshot on the UI thread; SaveAsync picks up the latest snapshot.
        _pendingSave = Recordings
            .Select(e => new RecordingDto(
                e.Platform, e.StartedAt, e.Status, e.DriveUrl, e.FilePath,
                e.TelegramMessageId, e.TelegramChatId, e.TelegramTopicId, e.ReportData))
            .ToList();

        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        // If another save is already running it will loop back and pick up _pendingSave.
        if (!await _saveLock.WaitAsync(millisecondsTimeout: 0)) return;
        try
        {
            List<RecordingDto>? dtos;
            while ((dtos = Interlocked.Exchange(ref _pendingSave, null)) is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                var json = JsonSerializer.Serialize(dtos, JsonOptions);
                await File.WriteAllTextAsync(SavePath, json);
            }
        }
        catch { }
        finally
        {
            _saveLock.Release();
        }
    }

    private record RecordingDto(
        string          Platform,
        DateTime        StartedAt,
        RecordingStatus Status,
        string?         DriveUrl,
        string?         FilePath,
        int?            TelegramMessageId,
        long            TelegramChatId,
        int?            TelegramTopicId,
        CallReportData? ReportData);
}
