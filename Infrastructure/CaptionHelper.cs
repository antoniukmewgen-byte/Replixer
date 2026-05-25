namespace Replixer.Infrastructure;

internal static class CaptionHelper
{
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

    public static string StripHashtags(string caption)
    {
        var (body, _) = SplitHashtagSuffix(caption);
        return body;
    }
}
