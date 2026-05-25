using Replixer.Models;

namespace Replixer.Infrastructure;

internal static class AppSecrets
{
    internal static int    TelegramApiId   => _apiId;
    internal static string TelegramApiHash => _apiHash;

    private const int    _apiId   = 0;
    private const string _apiHash = string.Empty;

    internal static readonly IReadOnlyList<TelegramChat> TelegramChats =
    [
        new TelegramChat("My Test Group", 1234567890L),
    ];
}
