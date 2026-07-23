using Replixer.Models;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace Replixer.Services.Upload;

/// <summary>
/// Відправка даних про недодзвон у Kommo та (опційно) дублювання в Google Таблицю — з тим самим
/// захистом від мережевих неполадок, що й у <see cref="PendingUploadRetryService"/> для звичайних
/// записів: кожен недодзвон одразу персистується в pending_missed_calls.json, і якщо якась зі
/// спроб впаде — фоновий таймер (кожні 10с) тихо повторює її, без діалогів і участі користувача,
/// і навіть переживає перезапуск застосунку (черга завантажується з диска при старті).
///
/// Kommo і Google Sheets — дві НЕЗАЛЕЖНІ цілі доставки (див. PendingMissedCall.KommoDelivered/
/// SheetDelivered): якщо Kommo пройшов, а Таблиця — ні (чи навпаки), повторюємо лише те, що не
/// вдалося, не дублюючи вже успішну частину. Google Sheets використовує ProcessingSpeedMinutes/
/// ProcessingSpeedWorkMinutes, обчислені під час доставки в Kommo (кешуються на записі), тому не
/// перераховує їх самостійно і може довиконатись окремо навіть значно пізніше за Kommo.
///
/// Із черги (і файлу) запис видаляється лише тоді, коли вже нічого доставляти: Kommo доставлено
/// І (Sheets вимкнено в налаштуваннях АБО Sheets теж доставлено).
/// </summary>
public sealed class MissedCallDeliveryService : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(10);

    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "pending_missed_calls.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly IUploadOrchestrator _orchestrator;
    private readonly AppSettings _settings;
    private readonly GoogleSheetsUploadService _sheets;
    private readonly List<PendingMissedCall> _pending = new();
    private readonly ConcurrentDictionary<Guid, byte> _inFlight = new();
    private readonly SemaphoreSlim _saveLock = new(1, 1);

    private Timer? _timer;

    // Наживо повідомляє про (проміжний чи кінцевий) стан доставки конкретного недодзвону —
    // MissedCallsViewModel підписується, щоб "дозеленити" відповідний рядок в історії, коли
    // Kommo/Таблиця нарешті доставляються у фоні. Id — той самий, що й у постійному історичному
    // записі (спільний Guid, який генерує й передає в обидва місця HomeViewModel.SubmitMissedCallAsync).
    public event Action<Guid, bool, bool>? DeliveryStatusChanged;

    public MissedCallDeliveryService(IUploadOrchestrator orchestrator, AppSettings settings, GoogleSheetsUploadService sheets)
    {
        _orchestrator = orchestrator;
        _settings     = settings;
        _sheets       = sheets;
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
    public async Task SubmitAsync(
        Guid id, string crmUrl, string note, string? callType, DateTime missedAt,
        string manager = "", IReadOnlyList<string>? screenshotUrls = null)
    {
        var entry = new PendingMissedCall(id, crmUrl, note, callType, missedAt, manager, screenshotUrls);

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
            bool    kommoOk          = entry.KommoDelivered;
            int?    speedMinutes     = entry.ProcessingSpeedMinutes;
            int?    speedWorkMinutes = entry.ProcessingSpeedWorkMinutes;
            string? kommoWarning     = null;

            if (!kommoOk)
            {
                var result  = await _orchestrator.PostKommoNoteAsync(entry.CrmUrl, entry.Note, entry.MissedAt, entry.CallType);
                kommoWarning = result.Warning;
                kommoOk      = kommoWarning is null;
                if (kommoOk)
                {
                    speedMinutes     = result.ProcessingSpeedMinutes;
                    speedWorkMinutes = result.ProcessingSpeedWorkMinutes;
                }
            }

            // У Таблицю пишемо лише після того, як Kommo вже гарантовано доставлено — саме звідти
            // беруться значення "Швидкість обробки", які мають потрапити в рядок (див. клас doc).
            bool    needSheet    = _settings.IsGoogleSheetsEnabled && !string.IsNullOrWhiteSpace(_settings.GoogleSheetsId);
            bool    sheetOk      = entry.SheetDelivered || !needSheet;
            string? sheetWarning = null;

            if (kommoOk && needSheet && !entry.SheetDelivered)
            {
                var forSheet = entry with { ProcessingSpeedMinutes = speedMinutes, ProcessingSpeedWorkMinutes = speedWorkMinutes };
                var (success, _, error) = await _sheets.AppendRowAsync(_settings.GoogleSheetsId, _settings.GoogleSheetsTabName, BuildSheetRow(forSheet));
                sheetOk      = success;
                sheetWarning = error;
            }

            var updated = entry with
            {
                KommoDelivered             = kommoOk,
                SheetDelivered             = sheetOk,
                ProcessingSpeedMinutes     = speedMinutes,
                ProcessingSpeedWorkMinutes = speedWorkMinutes,
            };

            DeliveryStatusChanged?.Invoke(entry.Id, kommoOk, sheetOk);

            if (kommoOk && sheetOk)
            {
                RemoveEntry(entry.Id);
                await SaveAsync();

                NotificationService.ShowSuccess(isFirstAttempt
                    ? "Недодзвон зафіксовано."
                    : "Недодзвон, який раніше не вдалося зафіксувати, тепер успішно відправлено.");
            }
            else
            {
                ReplaceEntry(updated);
                await SaveAsync();

                if (isFirstAttempt)
                {
                    if (!kommoOk)
                        ErrorReporter.Report("MISSED_CALL", $"Не вдалося зафіксувати недодзвон у Kommo. Спробуємо ще раз у фоні.\n{kommoWarning}");
                    else if (!sheetOk)
                        ErrorReporter.Report("MISSED_CALL_SHEET", $"Недодзвон зафіксовано в Kommo, але не вдалося продублювати в Google Таблицю. Спробуємо ще раз у фоні.\n{sheetWarning}");
                }
                // Фоновий повтор, що знову не вдався, — мовчки лишаємо запис у черзі й спробуємо за 10с.
            }
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

    // Стандарт — 2 колонки під скріни; якщо їх більше — додаємо ще по одній колонці на кожен
    // наступний (Скрін 3, Скрін 4, ...), а не обрізаємо список.
    private static List<object?> BuildSheetRow(PendingMissedCall entry)
    {
        var now = DateTime.Now;
        var row = new List<object?>
        {
            now.Year,
            now.ToString("dd.MM"),
            now.ToString("HH:mm:ss"),
            entry.Manager,
            entry.CallType ?? "",
            entry.MissedAt.ToString("dd.MM.yyyy HH:mm:ss"),
            entry.ProcessingSpeedMinutes,
            entry.ProcessingSpeedWorkMinutes,
            entry.CrmUrl,
        };

        int screenshotCount = entry.ScreenshotUrls?.Count ?? 0;
        int columns = Math.Max(2, screenshotCount);
        for (int i = 0; i < columns; i++)
            row.Add(i < screenshotCount ? entry.ScreenshotUrls![i] : null);

        return row;
    }

    private void ReplaceEntry(PendingMissedCall updated)
    {
        lock (_pending)
        {
            int idx = _pending.FindIndex(e => e.Id == updated.Id);
            if (idx >= 0) _pending[idx] = updated;
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
