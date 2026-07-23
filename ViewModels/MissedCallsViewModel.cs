using Replixer.Models;
using Replixer.Services;
using Replixer.Services.Upload;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Windows;

namespace Replixer.ViewModels;

// Постійна історія недодзвонів для розділу "НЕДОЗВОНИ" (і, впереміш із записами дзвінків,
// для "ОСТАННІ ЗАПИСИ" — див. HomeViewModel.RecentActivity). Побудовано за тим самим
// принципом, що й RecordingsViewModel: ObservableCollection + дебаунсений async-запис у
// JSON. На відміну від MissedCallDeliveryService.PendingMissedCall (черга доставки, звідки
// запис зникає одразу по успіху), записи тут лишаються назавжди — лише оновлюють свій
// KommoDelivered/SheetDelivered "наживо" через подію DeliveryStatusChanged.
public class MissedCallsViewModel : ViewModelBase, IDisposable
{
    private static readonly string SavePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "Replixer", "missed_calls_history.json");

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly MissedCallDeliveryService _delivery;
    private readonly SemaphoreSlim _saveLock = new(1, 1);
    private List<MissedCallDto>? _pendingSave;
    private bool _loading;

    public ObservableCollection<MissedCallEntry> MissedCalls { get; } = new();

    public bool IsEmpty => MissedCalls.Count == 0;

    public MissedCallsViewModel(MissedCallDeliveryService delivery)
    {
        _delivery = delivery;
        Load();

        MissedCalls.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(IsEmpty));
            ScheduleSave();
        };

        _delivery.DeliveryStatusChanged += OnDeliveryStatusChanged;
    }

    // Викликається з HomeViewModel.SubmitMissedCallAsync одразу при сабміті форми, ще ДО
    // виклику MissedCallDeliveryService.SubmitAsync — обидва отримують один і той самий id,
    // щоб фонові оновлення статусу доставки потрапляли саме в цей рядок.
    public MissedCallEntry AddEntry(
        Guid id, string manager, string callType, DateTime missedAt, string crmUrl,
        IReadOnlyList<string>? screenshotUrls)
    {
        var entry = new MissedCallEntry(id, manager, callType, missedAt, crmUrl, screenshotUrls);
        SubscribeEntry(entry);
        MissedCalls.Insert(0, entry);
        return entry;
    }

    private void SubscribeEntry(MissedCallEntry entry)
        => entry.PropertyChanged += (_, _) => ScheduleSave();

    // DeliverAsync у MissedCallDeliveryService виконується у фоні (таймер на 10с — без UI
    // SynchronizationContext), тож оновлення прив'язаної до XAML властивості обов'язково
    // маршрутизуємо через Dispatcher, інакше WPF-байндинг може повестись непередбачувано.
    private void OnDeliveryStatusChanged(Guid id, bool kommoDelivered, bool sheetDelivered)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var entry = MissedCalls.FirstOrDefault(e => e.Id == id);
            if (entry is null) return;
            entry.KommoDelivered = kommoDelivered;
            entry.SheetDelivered = sheetDelivered;
        });
    }

    private void Load()
    {
        _loading = true;
        try
        {
            if (!File.Exists(SavePath)) return;

            var dtos = JsonSerializer.Deserialize<List<MissedCallDto>>(File.ReadAllText(SavePath), JsonOptions);
            if (dtos is null) return;

            foreach (var dto in dtos)
            {
                var entry = new MissedCallEntry(
                    dto.Id, dto.Manager, dto.CallType, dto.MissedAt, dto.CrmUrl,
                    dto.ScreenshotUrls, dto.KommoDelivered, dto.SheetDelivered);
                SubscribeEntry(entry);
                MissedCalls.Add(entry);
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("MissedCallsViewModel", "Не вдалося завантажити історію недодзвонів", ex);
        }
        finally
        {
            _loading = false;
        }
    }

    private void ScheduleSave()
    {
        if (_loading) return;

        _pendingSave = MissedCalls
            .Select(e => new MissedCallDto(
                e.Id, e.Manager, e.CallType, e.MissedAt, e.CrmUrl, e.ScreenshotUrls,
                e.KommoDelivered, e.SheetDelivered))
            .ToList();

        _ = SaveAsync();
    }

    private async Task SaveAsync()
    {
        if (!await _saveLock.WaitAsync(millisecondsTimeout: 0)) return;
        try
        {
            List<MissedCallDto>? dtos;
            while ((dtos = Interlocked.Exchange(ref _pendingSave, null)) is not null)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(SavePath)!);
                var json = JsonSerializer.Serialize(dtos, JsonOptions);
                await File.WriteAllTextAsync(SavePath, json);
            }
        }
        catch (Exception ex)
        {
            ErrorReporter.Report("MISSED_CALLS_HISTORY_SAVE", "Не вдалося зберегти історію недодзвонів.", ex);
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

        _delivery.DeliveryStatusChanged -= OnDeliveryStatusChanged;

        _saveLock.Wait(millisecondsTimeout: 5_000);
        _saveLock.Dispose();
    }

    private record MissedCallDto(
        Guid     Id,
        string   Manager,
        string   CallType,
        DateTime MissedAt,
        string   CrmUrl,
        IReadOnlyList<string>? ScreenshotUrls,
        bool     KommoDelivered,
        bool     SheetDelivered);
}
