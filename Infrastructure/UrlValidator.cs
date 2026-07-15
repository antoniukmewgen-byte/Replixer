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
}
