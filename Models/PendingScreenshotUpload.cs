namespace Replixer.Models;

// Один локальний скріншот (зроблений через "Швидкі дії" у формі недодзвону), який не вдалось
// одразу залити на Google Drive — і тому чекає фонового ретраю (див.
// PendingScreenshotUploadRetryService), точно так само, як недодзвони чекають у
// pending_missed_calls.json (MissedCallDeliveryService). LocalPath — файл лишається на диску,
// поки не завантажиться; FolderId — вже повністю розв'язана цільова папка на Drive (з підпапкою
// "Screenshots"), щоб фоновому ретраю не потрібно було повторювати логіку резолву папки.
// KommoLeadUrl — якщо форму вже було відправлено без цього скріна, після успішного фонового
// завантаження посилання не губиться: сервіс додає окрему нотатку в лід із запізнілим лінком.
public record PendingScreenshotUpload(
    Guid     Id,
    string   LocalPath,
    string?  FolderId,
    string   Messenger,
    string?  KommoLeadUrl,
    DateTime CapturedAt);
