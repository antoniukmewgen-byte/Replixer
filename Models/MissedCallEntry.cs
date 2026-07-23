using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Replixer.Models;

// Постійний історичний запис про недодзвон — на відміну від PendingMissedCall (черга
// доставки, з якої запис зникає одразу після успіху), цей запис лишається назавжди і
// призначений лише для відображення в розділі "НЕДОЗВОНИ" та в "ОСТАННІ ЗАПИСИ" (впереміш
// із RecordingEntry, за MissedAt). KommoDelivered/SheetDelivered оновлюються "наживо" через
// MissedCallDeliveryService.DeliveryStatusChanged — той самий Id передається в обидва місця
// (див. HomeViewModel.SubmitMissedCallAsync), тож рядок у списку сам "дозеленюється", коли
// фоновий ретрай нарешті доставляє недодзвон.
public class MissedCallEntry : INotifyPropertyChanged
{
    public Guid     Id       { get; }
    public string   Manager  { get; }
    public string   CallType { get; }
    public DateTime MissedAt { get; }
    public string   CrmUrl   { get; }

    public IReadOnlyList<string> ScreenshotUrls { get; }

    private bool _kommoDelivered;
    public bool KommoDelivered
    {
        get => _kommoDelivered;
        set
        {
            if (_kommoDelivered == value) return;
            _kommoDelivered = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(StatusText));
        }
    }

    private bool _sheetDelivered;
    public bool SheetDelivered
    {
        get => _sheetDelivered;
        set { if (_sheetDelivered == value) return; _sheetDelivered = value; OnPropertyChanged(); }
    }

    public string DateDisplay      => MissedAt.ToString("dd.MM.yyyy");
    public string TimeOfDayDisplay => MissedAt.ToString("HH:mm");

    // NullToVisibility перевіряє лише на null, а Manager — non-nullable string (за
    // замовчуванням ""), тож без цієї властивості порожній Manager все одно рендерився б
    // видимим (порожнім) сегментом у рядку. Використовується замість прямого біндингу на Manager.
    public bool HasManager => !string.IsNullOrWhiteSpace(Manager);

    // Спрощений статус для інформаційного рядка (див. обговорення "п.4"): орієнтуємось на
    // Kommo як на основну ціль доставки — саме її бачить менеджер у CRM.
    public string StatusText => _kommoDelivered ? "Відправлено" : "У черзі";

    public MissedCallEntry(
        Guid id, string manager, string callType, DateTime missedAt, string crmUrl,
        IReadOnlyList<string>? screenshotUrls = null,
        bool kommoDelivered = false, bool sheetDelivered = false)
    {
        Id              = id;
        Manager         = manager;
        CallType        = callType;
        MissedAt        = missedAt;
        CrmUrl          = crmUrl;
        ScreenshotUrls  = screenshotUrls ?? Array.Empty<string>();
        _kommoDelivered = kommoDelivered;
        _sheetDelivered = sheetDelivered;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
