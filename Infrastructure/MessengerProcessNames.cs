namespace Replixer.Infrastructure;

// Назви процесів десктопних застосунків месенджерів (без ".exe") — потрібні, щоб
// визначити, що foreground-вікно належить саме потрібному месенджеру, перш ніж
// автоматично зробити скрін. УВАГА: точні назви процесів (особливо для WhatsApp,
// який на Windows буває встановлений як застосунок з Microsoft Store) потребують
// перевірки на реальній машині замовника.
public static class MessengerProcessNames
{
    private static readonly IReadOnlyDictionary<string, string[]> s_processNames = new Dictionary<string, string[]>
    {
        ["Viber"]    = ["Viber"],
        ["Telegram"] = ["Telegram"],
        ["WhatsApp"] = ["WhatsApp"],
    };

    // Store/UWP-версія WhatsApp (і теоретично будь-який інший застосунок з Microsoft Store)
    // не має власного "видимого" вікна — воно фактично належить процесу-хосту
    // (ApplicationFrameHost.exe), який лише малює рамку навколо контенту застосунку.
    // GetWindowThreadProcessId для такого вікна повертає ApplicationFrameHost, а не WhatsApp,
    // тому перевірка лише за іменем процесу ніколи не спрацьовує для Store-встановлення.
    // Як резерв — якщо foreground-процес є одним із відомих "хостів", звіряємо заголовок
    // вікна з ключовим словом месенджера.
    private static readonly string[] s_uwpHostProcessNames = ["ApplicationFrameHost", "WWAHost"];

    private static readonly IReadOnlyDictionary<string, string[]> s_titleKeywords = new Dictionary<string, string[]>
    {
        ["Viber"]    = ["Viber"],
        ["Telegram"] = ["Telegram"],
        ["WhatsApp"] = ["WhatsApp"],
    };

    public static bool Matches(string messenger, string processName, string? windowTitle = null)
    {
        // Contains, а не точна рівність — узгоджено з тим самим підходом у
        // MicrophoneMonitorService._targetProcesses: реальне ім'я процесу пакетованого
        // (Store/UWP) застосунку може мати суфікс/префікс, тож точна рівність надто крихка.
        if (s_processNames.TryGetValue(messenger, out var candidates)
            && candidates.Any(c => processName.Contains(c, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (!string.IsNullOrEmpty(windowTitle)
            && s_uwpHostProcessNames.Contains(processName, StringComparer.OrdinalIgnoreCase)
            && s_titleKeywords.TryGetValue(messenger, out var keywords)
            && keywords.Any(k => windowTitle.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return false;
    }
}
