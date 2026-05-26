using System.Diagnostics;

namespace Replixer.Infrastructure;

public static class PlatformHelper
{
    private static readonly HashSet<string> s_knownKeys =
    [
        "Telegram",
        "Viber",
        "WhatsApp.Root",
        "Ringostat Smart Phone",
    ];

    private static readonly IReadOnlyDictionary<string, string> s_relativeIconPaths =
        new Dictionary<string, string>
        {
            ["Telegram"]              = "/Assets/Icons/telegram.png",
            ["Viber"]                 = "/Assets/Icons/viber.png",
            ["WhatsApp.Root"]         = "/Assets/Icons/whatsapp.png",
            ["Ringostat Smart Phone"] = "/Assets/Icons/ringostat.png",
        };

    public static bool IsKnownPlatform(string platformKey)
        => s_knownKeys.Contains(platformKey);

    public static string? ToRelativeIconPath(string platformKey)
        => s_relativeIconPaths.TryGetValue(platformKey, out var p) ? p : null;

    public static string ToDisplayName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "Ручний запис";

        var lower = rawName.ToLowerInvariant();
        if (lower == "viber")                   return "Viber";
        if (lower.Contains("whatsapp"))         return "WhatsApp";
        if (lower.Contains("ringostat"))        return "Ringostat";
        if (lower == "telegram")                return "Telegram";
        if (lower == "ручний запис")            return "Ручний запис";

        Debug.Assert(false,
            $"[PlatformHelper] Unknown platform '{rawName}' — add it to PlatformHelper and the detector list.");

        return rawName;
    }

    public static string ToIconUri(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "pack://application:,,,/Assets/Icons/telegram.png";
        return rawName.ToLowerInvariant() switch
        {
            "viber"                              => "pack://application:,,,/Assets/Icons/viber.png",
            var s when s.Contains("whatsapp")   => "pack://application:,,,/Assets/Icons/whatsapp.png",
            var s when s.Contains("ringostat")  => "pack://application:,,,/Assets/Icons/ringostat.png",
            _                                   => "pack://application:,,,/Assets/Icons/telegram.png",
        };
    }

    public static string ToBrushKey(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "BrushTelegram";
        return rawName.ToLowerInvariant() switch
        {
            "viber"                              => "BrushViber",
            var s when s.Contains("whatsapp")   => "BrushWhatsApp",
            var s when s.Contains("ringostat")  => "BrushRingostat",
            _                                   => "BrushTelegram",
        };
    }

    public static string ToFileName(string rawName)
    {
        if (string.IsNullOrEmpty(rawName)) return "Телеграм";
        return rawName.ToLowerInvariant() switch
        {
            "viber"                              => "Viber",
            var s when s.StartsWith("whatsapp") => "WhatsApp",
            var s when s.Contains("ringostat")  => "Ringostat",
            _                                   => "Телеграм",
        };
    }
}
