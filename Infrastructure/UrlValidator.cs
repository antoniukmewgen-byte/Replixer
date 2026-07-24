using System.Text.RegularExpressions;

namespace Replixer.Infrastructure;

// Перевіряє, що рядок є посиланням саме на лід у Kommo
// (напр. https://movenation.kommo.com/leads/detail/25448453), а не будь-яким текстом чи посиланням.
public static partial class UrlValidator
{
    // Достатньо, щоб посилання ПОЧИНАЛОСЯ з https://<піддомен>.kommo.com/leads/detail/<id> —
    // далі може йти будь-що (query-параметри на кшталт "?tab_id=...", trailing slash тощо).
    [GeneratedRegex(@"^https://[a-zA-Z0-9\-]+\.kommo\.com/leads/detail/\d+", RegexOptions.IgnoreCase)]
    private static partial Regex KommoLeadUrlRegex();

    public static bool IsValidHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && KommoLeadUrlRegex().IsMatch(url.Trim());

    // Скріншоти менеджери надсилають посиланнями на Lightshot (напр. https://prnt.sc/eRDSnZFKNIqV) —
    // достатньо, щоб посилання ПОЧИНАЛОСЯ з https://prnt.sc/ — далі може йти будь-який ID
    // (Lightshot інколи додає символи на кшталт "_" чи "-", тож не обмежуємо формат хвоста).
    [GeneratedRegex(@"^https://prnt\.sc/.+", RegexOptions.IgnoreCase)]
    private static partial Regex PrntScUrlRegex();

    public static bool IsValidScreenshotUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && PrntScUrlRegex().IsMatch(url.Trim());
}
