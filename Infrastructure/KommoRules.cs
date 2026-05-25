namespace Replixer.Infrastructure;

internal static class KommoRules
{
    private static readonly Dictionary<string, HashSet<string>> FirstContactPipelines =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["MN EB1/2 Квалификация"] = new(StringComparer.OrdinalIgnoreCase) { "Квалификация" },
            ["Відділ продажу ЕК"]     = new(StringComparer.OrdinalIgnoreCase) { "Распределены" },
            ["Квалификация"]          = new(StringComparer.OrdinalIgnoreCase) { "Распределены" },
        };

    public static readonly HashSet<string> SkipFirstContactSources =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "Рекомендація",         "Рекомендация",
            "Реактивація",          "Реактивация",
            "Вторинне опрацювання", "Вторичная проработка",
        };

    public static bool ShouldSetFirstContact(string pipelineName, string statusName)
        => FirstContactPipelines.TryGetValue(pipelineName, out var statuses)
        && statuses.Contains(statusName);

    public static bool ShouldSkipDates(string? source)
        => source is not null && SkipFirstContactSources.Contains(source);
}
