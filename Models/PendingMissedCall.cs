namespace Replixer.Models;

// Один недодзвон, що очікує (пере)відправки в Kommo і/або Google Таблицю. Note — вже повністю
// відформатований текст (MissedCallReportData.FormatCaption()), тож фоновому ретраю
// не потрібно нічого перебудовувати — лише повторити той самий POST.
//
// Kommo і Google Sheets — незалежні цілі доставки: KommoDelivered/SheetDelivered кажуть, яка
// саме частина вже виконана, тож збій однієї не змушує дублювати вже успішну іншу при
// наступній спробі фонового ретраю (див. MissedCallDeliveryService.DeliverAsync).
// ProcessingSpeed* — рахуються один раз під час доставки в Kommo (там-таки, де є created_at
// ліда та телефон контакту) і кешуються тут, щоб їх можна було продублювати в Google Sheets
// навіть окремою, пізнішою спробою, коли Kommo вже доставлено, а таблиця — ще ні.
public record PendingMissedCall(
    Guid      Id,
    string    CrmUrl,
    string    Note,
    string?   CallType,
    // Момент натискання кнопки "Не додзвонився" (а не момент сабміту форми чи фонового
    // ретраю) — саме цей час іде в поле "Дата и время первого касания" в Kommo.
    DateTime  MissedAt,
    string    Manager = "",
    IReadOnlyList<string>? ScreenshotUrls = null,
    // Ті самі посилання, прив'язані до конкретного месенджера ("Viber"/"Telegram"/"WhatsApp") —
    // потрібно, щоб BuildSheetRow клав скрін саме в свою колонку Google Таблиці, а не за
    // порядком компактного списку ScreenshotUrls (де порядок не гарантує, який саме месенджер).
    IReadOnlyDictionary<string, string>? ScreenshotUrlsByMessenger = null,
    bool      KommoDelivered = false,
    bool      SheetDelivered = false,
    int?      ProcessingSpeedMinutes = null,
    int?      ProcessingSpeedWorkMinutes = null);
