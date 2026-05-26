using Replixer.Models;
using Replixer.ViewModels.Dialogs;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Diagnostics;
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

    private IReadOnlyList<RecordingEntry>? _recentRecordings;
    public  IReadOnlyList<RecordingEntry>  RecentRecordings
        => _recentRecordings ??= Recordings.Take(4).ToList();

    public RecordingsViewModel()
    {
        Load();
        Recordings.CollectionChanged += (_, _) =>
        {
            _recentRecordings = null;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(RecentRecordings));
            ScheduleSave();
        };
    }

    public RecordingEntry AddEntry(string platform)
    {
        var entry = new RecordingEntry(platform);
        SubscribeEntry(entry);
        Recordings.Insert(0, entry);
        return entry;
    }

    private void SubscribeEntry(RecordingEntry entry)
        => entry.PropertyChanged += (_, _) => ScheduleSave();

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
                    Status            = dto.Status == RecordingStatus.Loading ? RecordingStatus.Error : dto.Status,
                    DriveUrl          = dto.DriveUrl,
                    FilePath          = dto.FilePath,
                    SourcePath        = dto.SourcePath,
                    TelegramMessageId = dto.TelegramMessageId,
                    TelegramChatId    = dto.TelegramChatId,
                    TelegramTopicId   = dto.TelegramTopicId,
                    KommoNoteId       = dto.KommoNoteId,
                    ReportData        = dto.ReportData,
                };
                SubscribeEntry(entry);
                Recordings.Add(entry);
            }
        }
        catch { }
        finally
        {
            _loading = false;
        }
    }

    private void ScheduleSave()
    {
        if (_loading) return;

        _pendingSave = Recordings
            .Select(e => new RecordingDto(
                e.Platform, e.StartedAt, e.Status, e.DriveUrl, e.FilePath, e.SourcePath,
                e.TelegramMessageId, e.TelegramChatId, e.TelegramTopicId, e.KommoNoteId, e.ReportData))
            .ToList();

        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
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
        catch (Exception ex)
        {
            Debug.WriteLine($"[Recordings] Save failed: {ex.Message}");
        }
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
        string?         SourcePath,
        int?            TelegramMessageId,
        long            TelegramChatId,
        int?            TelegramTopicId,
        long?           KommoNoteId,
        CallReportData? ReportData);
}
