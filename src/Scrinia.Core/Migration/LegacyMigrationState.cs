using Scrinia.Core.Configuration;

namespace Scrinia.Core.Migration;

public enum LegacyMigrationState
{
    Fresh,
    PolicyRequired,
    LegacyClean,
    LegacyMixed,
    LegacyManual,
    LegacyUnsafe,
    Local,
    ProjectGit,
    PolicyDrift,
}

/// <summary>Filesystem observations consumed by the migration-only state machine.</summary>
public sealed record LegacyMigrationInput(
    bool HasAuthoritativeData,
    TrackingPolicy? TrackingPolicy,
    string? GitAttributes,
    string? MergeConfig,
    bool? Unsafe = false,
    bool ManualCleanupRequired = false);

public sealed record LegacyMigrationPlan(
    LegacyMigrationState State,
    TrackingPolicy TargetPolicy,
    string? GitAttributesContent,
    bool DeleteGitAttributes,
    bool DeleteMergeConfig,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<string> DriverUnsetArgv,
    IReadOnlyList<IReadOnlyList<string>> GitCommands);

public sealed record LegacyMigrationResult(
    ConfigurationErrorCode ErrorCode = ConfigurationErrorCode.None,
    ConfigurationExitClass ExitClass = ConfigurationExitClass.Success,
    bool Changed = false,
    string GitIndexStatus = "UNVERIFIED",
    IReadOnlyList<string>? Warnings = null,
    IReadOnlyList<string>? DriverUnsetArgv = null)
{
    public bool Succeeded => ErrorCode == ConfigurationErrorCode.None;
}

public static class LegacyMigrationStateMachine
{
    public static LegacyMigrationState Classify(LegacyMigrationInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.Unsafe == true) return LegacyMigrationState.LegacyUnsafe;
        if (input.TrackingPolicy is { } policy)
        {
            if (HasLegacy(input)) return LegacyMigrationState.PolicyDrift;
            return policy switch
            {
                TrackingPolicy.Local => LegacyMigrationState.Local,
                TrackingPolicy.ProjectGit => LegacyMigrationState.ProjectGit,
                _ => LegacyMigrationState.PolicyDrift,
            };
        }

        if (HasLegacy(input))
            return input.ManualCleanupRequired ? LegacyMigrationState.LegacyManual : HasUnrelatedContent(input)
                ? LegacyMigrationState.LegacyMixed : LegacyMigrationState.LegacyClean;
        return input.HasAuthoritativeData ? LegacyMigrationState.PolicyRequired : LegacyMigrationState.Fresh;
    }

    public static LegacyMigrationPlan PlanMigration(LegacyMigrationInput input, TrackingPolicy targetPolicy)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (targetPolicy is not (TrackingPolicy.Local or TrackingPolicy.ProjectGit))
            throw new ScriniaConfigurationException("Migration target must be exactly 'local' or 'project-git'.");

        LegacyMigrationState state = Classify(input);
        string? attributes = RemoveRecognizedLines(input.GitAttributes, out bool deleteAttributes);
        bool deleteMergeConfig = input.MergeConfig is not null &&
            LegacyMergeRecognition.IsExactLegacyMergeConfig(System.Text.Encoding.UTF8.GetBytes(input.MergeConfig));
        var warnings = new List<string>();
        if (input.MergeConfig is not null && !deleteMergeConfig)
            warnings.Add("The legacy merge.config contains unrecognized content and was preserved.");

        return new LegacyMigrationPlan(state, targetPolicy, attributes, deleteAttributes, deleteMergeConfig,
            warnings, LegacyMergeRecognition.DriverUnsetArgv(), []);
    }

    public static LegacyMigrationResult ApplyCommand(LegacyMigrationState state, TrackingPolicy requestedPolicy, bool isMigration)
    {
        if (requestedPolicy is not (TrackingPolicy.Local or TrackingPolicy.ProjectGit))
            return Failure(ConfigurationErrorCode.TrackingPolicyInvalid);
        if (!isMigration)
        {
            ConfigurationErrorCode? refusal = state switch
            {
                LegacyMigrationState.LegacyClean or LegacyMigrationState.LegacyMixed or LegacyMigrationState.LegacyManual => ConfigurationErrorCode.LegacyMigrationRequired,
                LegacyMigrationState.LegacyUnsafe => ConfigurationErrorCode.ManagedFileUnsafe,
                LegacyMigrationState.PolicyDrift => ConfigurationErrorCode.TrackingPolicyInvalid,
                _ => null,
            };
            return refusal is { } code ? Failure(code) : new(Changed: state is not (LegacyMigrationState.Local or LegacyMigrationState.ProjectGit));
        }

        if (state == LegacyMigrationState.LegacyUnsafe) return Failure(ConfigurationErrorCode.ManagedFileUnsafe);
        if (state == LegacyMigrationState.LegacyManual) return Failure(ConfigurationErrorCode.MigrationManualCleanupRequired);
        if (state == LegacyMigrationState.PolicyDrift) return Failure(ConfigurationErrorCode.TrackingPolicyInvalid);
        return new(ConfigurationErrorCode.None, ConfigurationExitClass.Success, true, "UNVERIFIED", [], LegacyMergeRecognition.DriverUnsetArgv());
    }

    private static LegacyMigrationResult Failure(ConfigurationErrorCode code) =>
        new(code, ConfigurationExitClass.PolicyFailure, false, "UNVERIFIED", [], []);

    private static bool HasLegacy(LegacyMigrationInput input) => input.GitAttributes is not null || input.MergeConfig is not null;

    private static bool HasUnrelatedContent(LegacyMigrationInput input)
    {
        if (input.GitAttributes is { } attributes)
            if (attributes.Split('\n').Any(line => !string.IsNullOrEmpty(line) && !LegacyMergeRecognition.IsRecognizedGitAttributesLine(line)))
                return true;
        return input.MergeConfig is not null && !LegacyMergeRecognition.IsExactLegacyMergeConfig(System.Text.Encoding.UTF8.GetBytes(input.MergeConfig));
    }

    private static string? RemoveRecognizedLines(string? content, out bool delete)
    {
        delete = false;
        if (content is null) return null;
        string normalized = content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        bool endedWithNewline = normalized.EndsWith('\n');
        var lines = normalized.Split('\n').Where(line =>
            !LegacyMergeRecognition.IsRecognizedGitAttributesLine(line)).ToList();
        if (endedWithNewline && lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);
        if (lines.Count == 0) { delete = true; return null; }
        string remaining = string.Join('\n', lines);
        return endedWithNewline ? remaining + '\n' : remaining;
    }
}

/// <summary>Reads and commits a local migration as one locked, recoverable filesystem transaction.</summary>
public sealed class LegacyMigrationService
{
    public LegacyMigrationInput Read(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        Dictionary<string, string> values = WorkspaceConfig.LoadValues(workspaceRoot);
        TrackingPolicy? policy = values.TryGetValue(WorkspaceConfig.TrackingKey, out string? raw)
            ? WorkspaceConfig.ParseTracking(raw) switch
            {
                TrackingPolicy.Unset => null,
                var value => value,
            } : null;
        string directory = Path.Combine(workspaceRoot, ".scrinia");
        return new(
            Directory.Exists(Path.Combine(directory, "memories")) || File.Exists(Path.Combine(directory, "facts.json")),
            policy,
            ReadText(Path.Combine(directory, ".gitattributes")),
            ReadText(Path.Combine(directory, "merge.config")));
    }

    public LegacyMigrationResult Migrate(string workspaceRoot, TrackingPolicy targetPolicy)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        LegacyMigrationInput input = Read(workspaceRoot);
        LegacyMigrationPlan plan = LegacyMigrationStateMachine.PlanMigration(input, targetPolicy);
        LegacyMigrationResult gate = LegacyMigrationStateMachine.ApplyCommand(plan.State, targetPolicy, isMigration: true);
        if (!gate.Succeeded) return gate;

        string directory = Path.Combine(workspaceRoot, ".scrinia");
        string configPath = Path.Combine(directory, "config.json");
        string ignorePath = Path.Combine(directory, ".gitignore");
        string attributesPath = Path.Combine(directory, ".gitattributes");
        string mergePath = Path.Combine(directory, "merge.config");
        Directory.CreateDirectory(directory);
        using FileLock _ = FileLock.AcquireExclusive(ConfigurationLock.GetPath(configPath));
        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase)
        {
            [configPath] = ReadBytes(configPath), [ignorePath] = ReadBytes(ignorePath),
            [attributesPath] = ReadBytes(attributesPath), [mergePath] = ReadBytes(mergePath),
        };
        try
        {
            Dictionary<string, string> values = WorkspaceConfig.LoadValues(workspaceRoot);
            values[WorkspaceConfig.TrackingKey] = targetPolicy switch
            {
                TrackingPolicy.Local => "local", TrackingPolicy.ProjectGit => "project-git",
                _ => throw new ScriniaConfigurationException("Unsupported migration target."),
            };
            DurableFile.AtomicWriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(values, ConfigJsonContext.Default.DictionaryStringString));
            DurableFile.AtomicWriteAllText(ignorePath, ManagedIgnoreBlock.Apply(ReadText(ignorePath), targetPolicy));
            WriteOrDelete(attributesPath, plan.GitAttributesContent, plan.DeleteGitAttributes);
            WriteOrDelete(mergePath, input.MergeConfig, plan.DeleteMergeConfig);
            return gate with { Warnings = plan.Warnings, DriverUnsetArgv = plan.DriverUnsetArgv };
        }
        catch (DurableWriteException)
        {
            Restore(originals);
            return gate with { ErrorCode = ConfigurationErrorCode.ConfigWriteFailed, ExitClass = ConfigurationExitClass.WriteFailure,
                Changed = false, Warnings = [], DriverUnsetArgv = [] };
        }
        catch (IOException)
        {
            Restore(originals);
            return gate with { ErrorCode = ConfigurationErrorCode.IoFailure, ExitClass = ConfigurationExitClass.WriteFailure,
                Changed = false, Warnings = [], DriverUnsetArgv = [] };
        }
    }

    private static string? ReadText(string path) => File.Exists(path) ? System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)) : null;
    private static byte[]? ReadBytes(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;
    private static void WriteOrDelete(string path, string? content, bool delete)
    {
        if (delete) { if (File.Exists(path)) File.Delete(path); return; }
        if (content is not null) DurableFile.AtomicWriteAllText(path, content);
    }
    private static void Restore(Dictionary<string, byte[]?> originals)
    {
        foreach ((string path, byte[]? bytes) in originals)
        {
            if (bytes is null) { if (File.Exists(path)) File.Delete(path); }
            else DurableFile.AtomicWriteAllBytes(path, bytes);
        }
    }
}
