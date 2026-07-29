using System.Text.RegularExpressions;

namespace Replixer.Infrastructure;

// Перевіряє, що рядок є посиланням саме на лід у Kommo
// (напр. https://movenation.kommo.com/leads/detail/25448453), а не будь-яким текстом чи посиланням.
public static partial class UrlValidator
{
    // Достатньо, щоб посилання ПОЧИНАЛОСЯ з https://<піддомен>.kommo.com/leads/detail/<id> —
    // далі може йти будь-що (query-параметри на кшталт "?tab_id=...", trailing slash тощо).
    //
    // \d{6,} (а не просто \d+) — менеджери інколи не встигають виділити/вставити посилання
    // повністю (напр. замість ".../detail/25922763" встигає скопіюватись лише ".../detail/2592")
    // і поспіхом тиснуть "Відправити". З \d+ таке обрізане посилання проходило валідацію так само
    // "успішно", як і повне — а гірше, ID "2592" міг виявитись реальним ID ЗОВСІМ ІНШОГО ліда
    // (ID в Kommo — наскрізний лічильник по всьому акаунту), тож звіт мовчки прикріплювався не до
    // того ліда. Реальні ID зараз 8-значні (напр. 25922763/25448453), тож поріг у 6 цифр певний
    // час залишає запас на зростання лічильника і водночас відсікає явно обрізані вставки.
    [GeneratedRegex(@"^https://[a-zA-Z0-9\-]+\.kommo\.com/leads/detail/\d{8,}", RegexOptions.IgnoreCase)]
    private static partial Regex KommoLeadUrlRegex();

    public static bool IsValidHttpUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) && KommoLeadUrlRegex().IsMatch(url.Trim());
}
