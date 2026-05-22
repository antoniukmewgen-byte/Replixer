namespace Replixer.Infrastructure;

/// <summary>
/// Centralises all Kommo CRM business rules (pipeline conditions, source exclusions)
/// separately from the HTTP communication layer.
/// </summary>
internal static class KommoRules
{
    // ── First-contact field rules ─────────────────────────────────────────────

    /// <summary>
    /// Pipeline → statuses for which the first-contact date and processing speed
    /// fields should be set on the lead.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> FirstContactPipelines =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MN EB1/2 Квалификация"] = new(StringComparer.OrdinalIgnoreCase) { "Квалификация" },
            ["Відділ продажу ЕК"]     = new(StringComparer.OrdinalIgnoreCase) { "Распределены" },
            ["Квалификация"]          = new(StringComparer.OrdinalIgnoreCase) { "Распределены" },
        };

    /// <summary>
    /// Lead sources for which first-contact date and processing speed must NOT be set
    /// (both from the app selection and from the CRM-side source field).
    /// </summary>
    public static readonly HashSet<string> SkipFirstContactSources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Рекомендація",         "Рекомендация",
            "Реактивація",          "Реактивация",
            "Вторинне опрацювання", "Вторичная проработка",
        };

    // ── Helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns <c>true</c> when the given pipeline/status combination should
    /// trigger a first-contact date update.
    /// </summary>
    public static bool ShouldSetFirstContact(string pipelineName, string statusName)
        => FirstContactPipelines.TryGetValue(pipelineName, out var statuses)
        && statuses.Contains(statusName);

    /// <summary>
    /// Returns <c>true</c> when the lead source means first-contact / processing-speed
    /// fields must be skipped.
    /// </summary>
    public static bool ShouldSkipDates(string? source)
        => source is not null && SkipFirstContactSources.Contains(source);
}
