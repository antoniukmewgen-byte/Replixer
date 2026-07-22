using Replixer.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Replixer.Services.Upload;

/// <summary>
/// Відправка нотатки про недодзвон у Kommo з тим самим захистом від мережевих неполадок,
/// що й у <see cref="PendingUploadRetryService"/> для звичайних записів: кожен недодзвон
/// одразу персистується в pending_missed_calls.json, і якщо перша спроба відправки впаде —
/// фоновий таймер (кожні 10с) тихо повторює її, без діалогів і участі користувача, і навіть
/// переживає перезапуск застосунку (черга завантажується з диска при старті).
/// Успішно доставлені записи одразу видаляються з черги/файлу.
/// </summary>
public sealed class MissedCallDeliveryService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "pending_missed_calls.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IUploadOrchestrator _orchestrator;
    private readonly List<PendingMissedCall> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private Timer? _timer;

    public MissedCallDeliveryService(IUploadOrchestrator orchestrator)
    {
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
        Debug.WriteLine("[MissedCallRetry] Фоновий сервіс відновлення недодзвонів запущено (кожні 10с)");
    }

    // Викликається одразу після сабміту форми недодзвону: ставить запис у чергу (щоб він
    // пережив навіть падіння застосунку до завершення першої спроби) і одразу пробує надіслати.
    // missedAt — момент натискання кнопки "Не додзвонився" (фіксується ще ДО відкриття форми,
    // див. HomeViewModel.ReportMissedCall), а не момент сабміту цієї форми.
    public async Task SubmitAsync(string crmUrl, string note, string? callType, DateTime missedAt)
    {
        var entry = new PendingMissedCall(Guid.NewGuid(), crmUrl, note, callType, missedAt);

        lock (_pending) _pending.Add(entry);
        await SaveAsync();

        if (_inFlight.TryAdd(entry.Id, 0))
            await DeliverAsync(entry, isFirstAttempt: true);
    }

    private async Task TickAsync()
    {
        List<PendingMissedCall> candidates;
        lock (_pending)
            candidates = _pending.Where(e => _inFlight.TryAdd(e.Id, 0)).ToList();

        foreach (var entry in candidates)
            await DeliverAsync(entry, isFirstAttempt: false);
    }

    private async Task DeliverAsync(PendingMissedCall entry, bool isFirstAttempt)
    {
        try
        {
            string? warning = await _orchestrator.PostKommoNoteAsync(entry.CrmUrl, entry.Note, entry.MissedAt, entry.CallType);

            if (warning is null)
            {
                lock (_pending) _pending.Remove(entry);
                await SaveAsync();

                NotificationService.ShowSuccess(isFirstAttempt
                    ? "Недодзвон зафіксовано."
                    : "Недодзвон, який раніше не вдалося зафіксувати, тепер успішно відправлено в Kommo.");
            }
            else if (isFirstAttempt)
            {
                ErrorReporter.Report("MISSED_CALL", $"Не вдалося зафіксувати недодзвон у Kommo. Спробуємо ще раз у фоні.\n{warning}");
            }
            // Фоновий повтор, що знову не вдався, — мовчки лишаємо запис у черзі й спробуємо за 10с.
        }
        catch (Exception ex)
        {
            if (isFirstAttempt)
                ErrorReporter.Report("MISSED_CALL", "Помилка при фіксації недодзвону. Спробуємо ще раз у фоні.", ex);
            else
                Debug.WriteLine($"[MissedCallRetry] Повтор для {entry.Id} не вдався: {ex.Message}");
        }
        finally
        {
            _inFlight.TryRemove(entry.Id, out _);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(SavePath)) return;

            var items = JsonSerializer.Deserialize<List<PendingMissedCall>>(File.ReadAllText(SavePath), JsonOptions);
            if (items is not null) _pending.AddRange(items);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("MISSED_CALL_LOAD", "Не вдалося завантажити чергу недодзвонів.", ex);
        }
    }

    private async Task SaveAsync()
    {
        await _saveLock.WaitAsync();
        try
        {
            List<PendingMissedCall> snapshot;
            lock (_pending) snapshot = _pending.ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
            var json = JsonSerializer.Serialize(snapshot, JsonOptions);
            await File.WriteAllTextAsync(SavePath, json);
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("MISSED_CALL_SAVE", "Не вдалося зберегти чергу недодзвонів.", ex);
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

        // Дочекатись поточного SaveAsync, щоб не порвати pending_missed_calls.json при виході.
        _saveLock.Wait(millisecondsTimeout: 5_000);
        _saveLock.Dispose();
    }
}
