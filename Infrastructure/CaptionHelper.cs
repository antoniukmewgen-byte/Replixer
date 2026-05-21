namespace Replixer.Infrastructure;

/// <summary>
/// Shared helpers for formatting Telegram/Kommo captions.
/// </summary>
internal static class CaptionHelper
{
    /// <summary>
    /// Splits a formatted caption into its body and an optional trailing hashtag line.
    /// E.g. "text\n#оплата" → ("text", "#оплата").
    /// </summary>
    public static (string body, string? hashtagLine) SplitHashtagSuffix(string caption)
    {
        var t  = caption.TrimEnd();
        var nl = t.LastIndexOf('\n');
        if (nl >= 0)
        {
            var last = t[(nl + 1)..].TrimStart('\r');
            if (last.StartsWith('#'))
                return (t[..nl].TrimEnd(), last);
        }
        return (t, null);
    }

    /// <summary>Removes the trailing hashtag line, returning only the body.</summary>
    public static string StripHashtags(string caption)
    {
        var (body, _) = SplitHashtagSuffix(caption);
        return body;
    }
}
