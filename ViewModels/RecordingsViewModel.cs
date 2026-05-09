using EchoVault.Models;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace EchoVault.ViewModels;

public class RecordingsViewModel : ViewModelBase
{
    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "EchoVault", "recordings.json");

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Converters    = { new JsonStringEnumConverter() },
    };

    private bool _loading;

    public ObservableCollection<RecordingEntry> Recordings { get; } = new();

    public bool IsEmpty => Recordings.Count == 0;

    public IReadOnlyList<RecordingEntry> RecentRecordings => Recordings.Take(4).ToList();

    public RecordingsViewModel()
    {
        Load();
        Recordings.CollectionChanged += (_, _) =>
        {
            Save();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(RecentRecordings));
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
        => entry.PropertyChanged += (_, _) => Save();

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
                    Status   = dto.Status,
                    DriveUrl = dto.DriveUrl,
                    FilePath = dto.FilePath,
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

    private void Save()
    {
        if (_loading) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            var dtos = Recordings
                .Select(e => new RecordingDto(e.Platform, e.StartedAt, e.Status, e.DriveUrl, e.FilePath))
                .ToList();
            File.WriteAllText(SavePath, JsonSerializer.Serialize(dtos, JsonOptions));
        }
        catch { }
    }

    private record RecordingDto(
        string          Platform,
        DateTime        StartedAt,
        RecordingStatus Status,
        string?         DriveUrl,
        string?         FilePath);
}
