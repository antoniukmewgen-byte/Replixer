using Replixer.Models;

namespace Replixer.Infrastructure;

// Скопіюй цей файл у AppSecrets.cs і заповни реальними значеннями.
// AppSecrets.cs виключений із git — він ніколи не потрапить у репозиторій.
internal static class AppSecrets
{
    internal static int TelegramApiId
        => int.TryParse(Environment.GetEnvironmentVariable("REPLIXER_TG_API_ID"), out var id) && id > 0
            ? id
            : _apiId;

    internal static string TelegramApiHash
        => Environment.GetEnvironmentVariable("REPLIXER_TG_API_HASH") is { Length: > 0 } h
            ? h
            : _apiHash;

    private const int    _apiId   = 0;             // my.telegram.org → API development tools
    private const string _apiHash = string.Empty;   // my.telegram.org → API development tools

    internal static string ErrorBotToken => string.Empty; // токен бота для звітів про помилки
    internal static long   ErrorChatId   => 0L;           // ID чату куди слати звіти

    internal static readonly IReadOnlyList<TelegramChat> TelegramChats =
    [
        new TelegramChat("Назва групи", 1234567890L),
    ];
}
