namespace Replixer.Infrastructure;

internal static class PositionPolicy
{
    public static bool IsTelegramVisible(string? position)   => position != "Діагност";
    public static bool IsCallTypeVisible(string? position)   => position == "Менеджер";
    public static bool IsLeadSourceVisible(string? position) => position != "Діагност";
    public static bool IsRatingVisible(string? position)     => position == "Менеджер";
    public static bool IsOutcomeVisible(string? position)    => position == "Менеджер";

    public static bool ShouldSkipTelegram(string? position, TimeSpan duration) => position switch
    {
        "Кваліфікатор" => duration < TimeSpan.FromMinutes(1),
        "Менеджер"     => duration < TimeSpan.FromMinutes(10),
        _              => true,
    };
}
