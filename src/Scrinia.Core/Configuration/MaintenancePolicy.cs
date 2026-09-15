using System.Globalization;

namespace Scrinia.Core.Configuration;

public sealed record MaintenancePolicy(
    int MaxArchivedVersions,
    int MinimumAgeDays,
    int JournalCleanupBatchSize,
    int TimeBudgetSeconds,
    bool BackgroundEnabled)
{
    public const int DefaultMaxArchivedVersions = 10;
    public const int DefaultMinimumAgeDays = 30;
    public const int DefaultJournalCleanupBatchSize = 50;
    public const int DefaultTimeBudgetSeconds = 5;
    public const bool DefaultBackgroundEnabled = true;

    public static MaintenancePolicyLoadResult Load(IWorkspaceConfig config) =>
        MaintenancePolicyLoader.Load(config);
}

public sealed record MaintenancePolicyLoadResult(
    MaintenancePolicy? Policy,
    IReadOnlyList<MaintenancePolicyError> Errors)
{
    public bool IsValid => Policy is not null && Errors.Count == 0;
}

public sealed record MaintenancePolicyError(string Key, string Code);

internal static class MaintenancePolicyLoader
{
    private const string MaxArchivedVersionsKey = "Scrinia:Retention:MaxArchivedVersions";
    private const string MinimumAgeDaysKey = "Scrinia:Retention:MinimumAgeDays";
    private const string JournalCleanupBatchSizeKey = "Scrinia:Maintenance:JournalCleanupBatchSize";
    private const string TimeBudgetSecondsKey = "Scrinia:Maintenance:TimeBudgetSeconds";
    private const string BackgroundEnabledKey = "Scrinia:Maintenance:BackgroundEnabled";

    internal static MaintenancePolicyLoadResult Load(IWorkspaceConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        string? maxArchivedVersions = config.GetValue(MaxArchivedVersionsKey);
        string? minimumAgeDays = config.GetValue(MinimumAgeDaysKey);
        string? journalCleanupBatchSize = config.GetValue(JournalCleanupBatchSizeKey);
        string? timeBudgetSeconds = config.GetValue(TimeBudgetSecondsKey);
        string? backgroundEnabled = config.GetValue(BackgroundEnabledKey);

        var errors = new List<MaintenancePolicyError>();
        int? maxVersions = ParseInteger(maxArchivedVersions, MaxArchivedVersionsKey, 1, 100000, errors);
        int? minimumAge = ParseInteger(minimumAgeDays, MinimumAgeDaysKey, 0, 36500, errors);
        int? journalBatch = ParseInteger(journalCleanupBatchSize, JournalCleanupBatchSizeKey, 1, 50, errors);
        int? timeBudget = ParseInteger(timeBudgetSeconds, TimeBudgetSecondsKey, 1, 3600, errors);
        bool? background = ParseBoolean(backgroundEnabled, BackgroundEnabledKey, errors);

        if (errors.Count > 0)
            return new MaintenancePolicyLoadResult(null, errors);

        return new MaintenancePolicyLoadResult(
            new MaintenancePolicy(
                maxVersions!.Value,
                minimumAge!.Value,
                journalBatch!.Value,
                timeBudget!.Value,
                background!.Value),
            []);
    }

    private static int? ParseInteger(
        string? value,
        string key,
        int minimum,
        int maximum,
        List<MaintenancePolicyError> errors)
    {
        if (value is null)
            return key switch
            {
                MaxArchivedVersionsKey => MaintenancePolicy.DefaultMaxArchivedVersions,
                MinimumAgeDaysKey => MaintenancePolicy.DefaultMinimumAgeDays,
                JournalCleanupBatchSizeKey => MaintenancePolicy.DefaultJournalCleanupBatchSize,
                TimeBudgetSecondsKey => MaintenancePolicy.DefaultTimeBudgetSeconds,
                _ => throw new ArgumentOutOfRangeException(nameof(key), key, "Unknown maintenance policy key."),
            };

        if (value.Length == 0 || value.Any(character => character is < '0' or > '9') ||
            !int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsed))
        {
            errors.Add(new MaintenancePolicyError(key, "INVALID_INTEGER"));
            return null;
        }

        if (parsed < minimum || parsed > maximum)
        {
            errors.Add(new MaintenancePolicyError(key, "OUT_OF_RANGE"));
            return null;
        }

        return parsed;
    }

    private static bool? ParseBoolean(
        string? value,
        string key,
        List<MaintenancePolicyError> errors)
    {
        if (value is null)
            return MaintenancePolicy.DefaultBackgroundEnabled;
        if (value == "true")
            return true;
        if (value == "false")
            return false;

        errors.Add(new MaintenancePolicyError(key, "INVALID_BOOLEAN"));
        return null;
    }
}
