namespace Replixer.Infrastructure;

// Шаблони "швидких" посилань, які відкривають чат з клієнтом за номером телефону напряму
// в застосунку месенджера (формати надані замовником, {0} — номер без "+" і пробілів).
// Навмисно використовуємо "рідні" URI-схеми застосунків (tg://, whatsapp://), а не
// звичайні https://t.me чи https://wa.me — веб-посилання спочатку відкривають браузер
// (з проміжною сторінкою "Open in app?"), через що автоматичне очікування foreground-вікна
// месенджера (MissedCallReportViewModel.WaitForMessengerForegroundAsync) або не спрацює,
// або зловить не те вікно. Схеми нижче відкривають застосунок напряму, якщо він
// встановлений і зареєстрував свій протокол в ОС — це потребує перевірки на реальній
// машині замовника (особливо для WhatsApp, реєстрація протоколу залежить від версії/джерела встановлення).
public static class MessengerDeepLinkProvider
{
    private static readonly IReadOnlyDictionary<string, string> s_templates = new Dictionary<string, string>
    {
        ["Viber"]    = "viber://chat?number=%2B{0}",
        ["Telegram"] = "tg://resolve?phone={0}",
        ["WhatsApp"] = "whatsapp://send?phone={0}",
    };

    public static IReadOnlyList<string> SupportedMessengers { get; } = ["Viber", "Telegram", "WhatsApp"];

    public static string? BuildDeepLink(string messenger, string phone)
    {
        if (!s_templates.TryGetValue(messenger, out var template)) return null;
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var normalized = phone.Trim().TrimStart('+').Replace(" ", "").Replace("-", "");
        return string.Format(template, normalized);
    }
}
