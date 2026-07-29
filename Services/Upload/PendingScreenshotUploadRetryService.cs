using Replixer.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Replixer.Services.Upload;

/// <summary>
/// Фоновий добір скріншотів (зі "Швидких дій" у формі недодзвону), які не вдалось одразу залити
/// на Google Drive — той самий патерн, що й <see cref="MissedCallDeliveryService"/>: кожен
/// невдалий скрін одразу персистується в pending_screenshots.json, і фоновий таймер (кожні 10с)
/// тихо повторює завантаження, без діалогів і участі користувача, переживаючи навіть перезапуск
/// застосунку.
///
/// Якщо форму недодзвону вже було відправлено до того, як скрін довантажився у фоні — посилання
/// не губиться: сервіс додає окрему нотатку в лід (KommoLeadUrl) із запізнілим лінком, а не
/// намагається "довписати" вже надіслану нотатку чи рядок Google Таблиці заднім числом.
/// </summary>
public sealed class PendingScreenshotUploadRetryService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "pending_screenshots.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly GoogleDriveUploadService _drive;
    private readonly IUploadOrchestrator _orchestrator;
    private readonly List<PendingScreenshotUpload> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private Timer? _timer;

    public PendingScreenshotUploadRetryService(GoogleDriveUploadService drive, IUploadOrchestrator orchestrator)
    {
        _drive        = drive;
        _orchestrator = orchestrator;
        Load();
    }

    public void Start()
    {
        if (_timer is not null) return; // вже запущено
        _timer = new Timer(_ => _ = TickAsync(), null, Interval, Interval);

        // Те, що лишилось незавершеним з минулого запуску застосунку, добираємо одразу,
        // не чекаючи першого тіку таймера.
        _ = TickAsync();
        Debug.WriteLine("[ScreenshotRetry] Фоновий сервіс довантаження скріншотів запущено (кожні 10с)");
    }

    // Викликається одразу після невдалої спроби залити скрін інлайн (див.
    // MissedCallReportViewModel.CaptureAndUploadScreenshotAsync): ставить файл у чергу (щоб він
    // пережив навіть падіння застосунку до наступної вдалої спроби) і одразу пробує ще раз —
    // раптом це була разова мережева гикавка.
    public async Task EnqueueAsync(string localPath, string? folderId, string messenger, string? kommoLeadUrl)
    {
        var entry = new PendingScreenshotUpload(Guid.NewGuid(), localPath, folderId, messenger, kommoLeadUrl, DateTime.Now);

        lock (_pending) _pending.Add(entry);
        await SaveAsync();

        if (_inFlight.TryAdd(entry.Id, 0))
            await RetryOneAsync(entry, isFirstAttempt: true);
    }

    private async Task TickAsync()
    {
        List<PendingScreenshotUpload> candidates;
        lock (_pending)
            candidates = _pending.Where(e => _inFlight.TryAdd(e.Id, 0)).ToList();

        foreach (var entry in candidates)
            await RetryOneAsync(entry, isFirstAttempt: false);
    }

    private async Task RetryOneAsync(PendingScreenshotUpload entry, bool isFirstAttempt)
    {
        try
        {
            if (!File.Exists(entry.LocalPath))
            {
                // Файл кудись подівся (видалений вручну тощо) — більше нема що вантажити.
                Debug.WriteLine($"[ScreenshotRetry] Локальний файл зник, знімаємо із черги: {entry.LocalPath}");
                RemoveEntry(entry.Id);
                await SaveAsync();
                return;
            }

            string? url = await _drive.UploadAsync(entry.LocalPath, entry.FolderId, mimeType: "image/png");
            if (url is null)
            {
                // Черговий невдалий тик — мовчки лишаємо запис у черзі й спробуємо за 10с.
                return;
            }

            RemoveEntry(entry.Id);
            await SaveAsync();

            try { File.Delete(entry.LocalPath); }
            catch (Exception ex) { Debug.WriteLine($"[ScreenshotRetry] Не вдалось видалити локальний файл: {ex.Message}"); }

            if (!string.IsNullOrWhiteSpace(entry.KommoLeadUrl))
            {
                var note = $"📎 Скрін ({entry.Messenger}), довантажено пізніше: {url}";
                var result = await _orchestrator.PostKommoNoteAsync(entry.KommoLeadUrl, note, callStartTime: null, callType: null);
                if (result.Warning is not null)
                    Debug.WriteLine($"[ScreenshotRetry] Скрін довантажено, але нотатку в Kommo додати не вдалось: {result.Warning}");
            }

            NotificationService.ShowSuccess(
                $"Скріншот ({entry.Messenger}), який раніше не вдалось завантажити, тепер збережено на Google Drive.");
        }
        catch (Exception ex)
        {
            if (isFirstAttempt)
                Debug.WriteLine($"[ScreenshotRetry] Перша спроба для {entry.Id} не вдалась: {ex.Message}");
            else
                Debug.WriteLine($"[ScreenshotRetry] Повтор для {entry.Id} не вдався: {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(entry.Id, out _);
        }
    }

    private void RemoveEntry(Guid id)
    {
        lock (_pending) _pending.RemoveAll(e => e.Id == id);
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;

            var items = JsonSerializer.Deserialize<List<PendingScreenshotUpload>>(File.ReadAllText(SavePath), JsonOptions);
            if (items is not null) _pending.AddRange(items);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("SCREENSHOT_RETRY_LOAD", "Не вдалося завантажити чергу скріншотів.", ex);
        }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            List<PendingScreenshotUpload> snapshot;
            lock (_pending) snapshot = _pending.ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(SavePath, json);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("SCREENSHOT_RETRY_SAVE", "Не вдалося зберегти чергу скріншотів.", ex);
        }
        finally
        {
            _saveLock.Release();
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _timer?.Dispose();
        _timer = null;

        // Дочекатись поточного SaveAsync, щоб не порвати pending_screenshots.json при виході.
        _saveLock.Wait(millisecondsTimeout: 5_000);
        _saveLock.Dispose();
    }
}
