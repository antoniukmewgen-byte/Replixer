namespace Replixer.Infrastructure;

/// <summary>
/// Centralises all position-based business rules so every consumer
/// reads from one place instead of scattering magic strings.
/// </summary>
internal static class PositionPolicy
{
    // ── Visibility ────────────────────────────────────────────────────────────

    public static bool IsTelegramVisible(string? position)   => position != "Діагност";
    public static bool IsCallTypeVisible(string? position)   => position == "Менеджер";
    public static bool IsLeadSourceVisible(string? position) => position != "Діагност";
    public static bool IsRatingVisible(string? position)     => position == "Менеджер";
    public static bool IsOutcomeVisible(string? position)    => position == "Менеджер";

    // ── Telegram sending threshold ────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the recording should NOT be sent to Telegram
    /// (call too short, or position never sends).
    /// </summary>
    public static bool ShouldSkipTelegram(string? position, TimeSpan duration) => position switch
    {
        "Кваліфікатор" => duration < TimeSpan.FromMinutes(1),
        "Менеджер"     => duration < TimeSpan.FromMinutes(10),
        _              => true,   // Діагност and any unknown — never send
    };
}
