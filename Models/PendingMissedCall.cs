namespace Replixer.Models;

// Один недодзвон, що очікує (пере)відправки нотатки в Kommo. Note — вже повністю
// відформатований текст (MissedCallReportData.FormatCaption()), тож фоновому ретраю
// не потрібно нічого перебудовувати — лише повторити той самий POST.
public record PendingMissedCall(
    Guid      Id,
    string    CrmUrl,
    string    Note,
    string?   CallType,
    DateTime  CreatedAt);
