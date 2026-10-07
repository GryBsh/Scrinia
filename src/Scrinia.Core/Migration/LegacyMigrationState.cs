using Scrinia.Core.Configuration;
using Scrinia.Core.Bundles;
using System.Text.Json;
using System.Text;

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
    bool ManualCleanupRequired = false,
    string? GitIgnore = null);

public sealed record LegacyFilesystemObservation(
    LegacyMigrationState State,
    ConfigurationErrorCode ErrorCode,
    ConfigurationExitClass ExitClass,
    LegacyMigrationInput? Input,
    ManagedIgnoreValidation IgnoreValidation,
    string GitIndexStatus = "UNVERIFIED");

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
    IReadOnlyList<string>? DriverUnsetArgv = null,
    Exception? Exception = null)
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

    private static bool HasLegacy(LegacyMigrationInput input) =>
        HasRecognizedAttributes(input.GitAttributes) ||
        (input.MergeConfig is not null && LegacyMergeRecognition.IsExactLegacyMergeConfig(System.Text.Encoding.UTF8.GetBytes(input.MergeConfig)));

    private static bool HasRecognizedAttributes(string? content) => content is not null &&
        content.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n')
            .Any(LegacyMergeRecognition.IsRecognizedGitAttributesLine);

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
    private const string RecoveryJournalName = ".tracking-recovery.json";

    public LegacyFilesystemObservation Inspect(string workspaceRoot, string? envSingle, string? envDouble)
    {
        try
        {
            workspaceRoot = TrackingRecoveryJournal.ResolveWorkspaceRoot(workspaceRoot);
            using WorkspaceLease lease = AcquireWorkspaceLease(workspaceRoot);
            using FileLock fileLock = AcquireConfigurationLock(workspaceRoot);
            TrackingTransaction.RecoverLocked(workspaceRoot, lease);
            return InspectLocked(workspaceRoot, envSingle, envDouble);
        }
        catch (TrackingRecoveryRequiredException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.ConfigRecoveryRequired,
                ConfigurationExitClass.WriteFailure, null, ManagedIgnoreValidation.Drift);
        }
        catch (ScriniaConfigurationException exception) when (exception.InnerException is JsonException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.WorkspaceConfigMalformed,
                ConfigurationExitClass.InvalidConfiguration, null, ManagedIgnoreValidation.Drift);
        }
        catch (ScriniaConfigurationException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.WorkspaceConfigMalformed,
                ConfigurationExitClass.InvalidConfiguration, null, ManagedIgnoreValidation.Drift);
        }
        catch (FileLockTimeoutException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.ConfigLockTimeout,
                ConfigurationExitClass.WriteFailure, null, ManagedIgnoreValidation.Drift);
        }
        catch (UnauthorizedAccessException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.IoFailure,
                ConfigurationExitClass.WriteFailure, null, ManagedIgnoreValidation.Drift);
        }
        catch (DecoderFallbackException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.ManagedFileUnsafe,
                ConfigurationExitClass.PolicyFailure, null, ManagedIgnoreValidation.Drift);
        }
        catch (FormatException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.WorkspaceConfigMalformed,
                ConfigurationExitClass.InvalidConfiguration, null, ManagedIgnoreValidation.Drift);
        }
        catch (IOException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.IoFailure,
                ConfigurationExitClass.WriteFailure, null, ManagedIgnoreValidation.Drift);
        }
    }

    public LegacyMigrationInput Read(string workspaceRoot, string? envSingle, string? envDouble)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        Dictionary<string, string> values = WorkspaceConfig.LoadValuesUncached(workspaceRoot);
        TrackingPolicy? policy = EffectivePolicy.ResolveEffective(
            envSingle, envDouble,
            values.GetValueOrDefault(WorkspaceConfig.TrackingKey)) switch
        {
            TrackingPolicy.Unset => null,
            var value => value,
        };
        string directory = Path.Combine(workspaceRoot, ".scrinia");
        return new(
            HasAuthoritativeData(directory),
            policy,
            ReadText(Path.Combine(directory, ".gitattributes")),
            ReadText(Path.Combine(directory, "merge.config")),
            false, false, ReadText(Path.Combine(directory, ".gitignore")));
    }

    public LegacyMigrationResult Migrate(string workspaceRoot, TrackingPolicy targetPolicy, string? envSingle = null, string? envDouble = null) =>
        Execute(workspaceRoot, targetPolicy, envSingle, envDouble, isMigration: true);

    public LegacyMigrationResult Set(string workspaceRoot, TrackingPolicy targetPolicy, string? envSingle = null, string? envDouble = null) =>
        Execute(workspaceRoot, targetPolicy, envSingle, envDouble, isMigration: false);

    private LegacyMigrationResult Execute(string workspaceRoot, TrackingPolicy targetPolicy,
        string? envSingle, string? envDouble, bool isMigration)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        if (targetPolicy is not (TrackingPolicy.Local or TrackingPolicy.ProjectGit))
            return new(ConfigurationErrorCode.TrackingPolicyInvalid, ConfigurationExitClass.PolicyFailure);
        try
        {
            workspaceRoot = TrackingRecoveryJournal.ResolveWorkspaceRoot(workspaceRoot);
            using WorkspaceLease lease = AcquireWorkspaceLease(workspaceRoot);
            using FileLock fileLock = AcquireConfigurationLock(workspaceRoot);
            TrackingTransaction.RecoverLocked(workspaceRoot, lease);
            LegacyFilesystemObservation observation = InspectLocked(workspaceRoot, envSingle, envDouble);
            if (observation.ErrorCode is not ConfigurationErrorCode.None and not ConfigurationErrorCode.TrackingPolicyRequired)
                return new(observation.ErrorCode, observation.ExitClass);
            LegacyMigrationInput input = observation.Input!;
            LegacyMigrationPlan? plan = isMigration ? LegacyMigrationStateMachine.PlanMigration(input, targetPolicy) : null;
            LegacyMigrationResult gate = LegacyMigrationStateMachine.ApplyCommand(plan?.State ?? observation.State, targetPolicy, isMigration);
            if (!gate.Succeeded) return gate;
            if (observation.State == (targetPolicy == TrackingPolicy.Local ? LegacyMigrationState.Local : LegacyMigrationState.ProjectGit))
                return gate with { Changed = false, Warnings = plan?.Warnings, DriverUnsetArgv = plan?.DriverUnsetArgv };

            var after = TrackingRecoveryJournal.MemberNames.ToDictionary(name => name,
                name => TrackingTransaction.CaptureState(Path.Combine(workspaceRoot, ".scrinia", name)).Bytes,
                StringComparer.Ordinal);
            Dictionary<string, string> values = WorkspaceConfig.LoadValuesUncached(workspaceRoot);
            values[WorkspaceConfig.TrackingKey] = targetPolicy == TrackingPolicy.Local ? "local" : "project-git";
            after["config.json"] = JsonSerializer.SerializeToUtf8Bytes(values, ConfigJsonContext.Default.DictionaryStringString);
            after[".gitignore"] = System.Text.Encoding.UTF8.GetBytes(ManagedIgnoreBlock.Apply(input.GitIgnore, targetPolicy));
            if (plan is not null)
            {
                if (plan.DeleteGitAttributes) after[".gitattributes"] = null;
                else if (plan.GitAttributesContent is not null && plan.GitAttributesContent != input.GitAttributes)
                    after[".gitattributes"] = System.Text.Encoding.UTF8.GetBytes(plan.GitAttributesContent);
                if (plan.DeleteMergeConfig) after["merge.config"] = null;
            }
            LegacyMigrationResult result = TrackingTransaction.CommitLocked(workspaceRoot, lease,
                isMigration ? "migrate" : "set", after);
            return result.Succeeded ? result with
            {
                Warnings = (result.Warnings ?? []).Concat(plan?.Warnings ?? []).ToArray(),
                DriverUnsetArgv = plan?.DriverUnsetArgv,
            } : result;
        }
        catch (Exception exception) { return MapInspectionFailure(exception); }
    }

    // Temporary explicit admission route; the final shared tracking adapter is supplied by Task 6.
    private static WorkspaceLease AcquireWorkspaceLease(string workspaceRoot)
    {
        TrackingRecoveryJournal.InspectPath(Path.Combine(workspaceRoot, ".scrinia"), file: false);
        WorkspaceLease lease = new WorkspaceAdmission(workspaceRoot, recover: null)
            .EnterRestore(TimeSpan.FromSeconds(5), default);
        try
        {
            BundleRestoreCoordinator.RecoverUnderLease(lease, BundleReadLimits.Default, default);
            return lease;
        }
        catch { lease.Dispose(); throw; }
    }

    private static LegacyMigrationState ClassifyFilesystem(LegacyMigrationInput input, ManagedIgnoreValidation validation)
    {
        if (input.Unsafe == true || (input.GitIgnore is not null && HasMalformedMarker(input.GitIgnore)))
            return LegacyMigrationState.LegacyUnsafe;
        if (input.TrackingPolicy is { } policy)
        {
            if (validation == ManagedIgnoreValidation.Drift) return LegacyMigrationState.PolicyDrift;
            LegacyMigrationState legacyState = LegacyMigrationStateMachine.Classify(input with { TrackingPolicy = null });
            if (legacyState is LegacyMigrationState.LegacyClean or LegacyMigrationState.LegacyMixed or LegacyMigrationState.LegacyManual)
                return legacyState;
            return policy == TrackingPolicy.Local ? LegacyMigrationState.Local : LegacyMigrationState.ProjectGit;
        }
        return LegacyMigrationStateMachine.Classify(input);
    }

    private static bool HasMalformedMarker(string content) =>
        (content.Contains("# >>> Scrinia managed:", StringComparison.Ordinal) ||
         content.Contains("# <<< Scrinia managed:", StringComparison.Ordinal)) &&
        ManagedIgnoreBlock.Validate(content, TrackingPolicy.ProjectGit) == ManagedIgnoreValidation.Drift;

    private static bool HasAuthoritativeData(string directory)
    {
        if (Directory.Exists(Path.Combine(directory, "memories")) || File.Exists(Path.Combine(directory, "facts.json"))) return true;
        if (!Directory.Exists(directory)) return false;
        return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
            .Any(path => Path.GetFileName(path) is not ("config.json" or ".gitignore" or ".gitattributes" or "merge.config" or RecoveryJournalName or ".bundle-restore.lock"));
    }

    private static LegacyMigrationResult MapInspectionFailure(Exception exception) => exception switch
    {
        TrackingRecoveryRequiredException => new(ConfigurationErrorCode.ConfigRecoveryRequired, ConfigurationExitClass.WriteFailure, Exception: exception),
        FileLockTimeoutException => new(ConfigurationErrorCode.ConfigLockTimeout, ConfigurationExitClass.WriteFailure),
        DurableWriteException => new(ConfigurationErrorCode.ConfigWriteFailed, ConfigurationExitClass.WriteFailure),
        ScriniaConfigurationException =>
            new(ConfigurationErrorCode.WorkspaceConfigMalformed, ConfigurationExitClass.InvalidConfiguration),
        DecoderFallbackException => new(ConfigurationErrorCode.ManagedFileUnsafe, ConfigurationExitClass.PolicyFailure),
        UnauthorizedAccessException or IOException => new(ConfigurationErrorCode.IoFailure, ConfigurationExitClass.WriteFailure),
        FormatException => new(ConfigurationErrorCode.WorkspaceConfigMalformed, ConfigurationExitClass.InvalidConfiguration),
        _ => new(ConfigurationErrorCode.IoFailure, ConfigurationExitClass.WriteFailure),
    };

    private static FileLock AcquireConfigurationLock(string workspaceRoot) =>
        FileLock.AcquireExclusive(ConfigurationLock.GetPath(Path.Combine(workspaceRoot, ".scrinia", "config.json")));

    private LegacyFilesystemObservation InspectLocked(string workspaceRoot, string? envSingle, string? envDouble)
    {
        LegacyMigrationInput input = Read(workspaceRoot, envSingle, envDouble);
        ManagedIgnoreValidation validation = input.TrackingPolicy is { } policy && input.GitIgnore is not null
            ? ManagedIgnoreBlock.Validate(input.GitIgnore, policy) : ManagedIgnoreValidation.Missing;
        LegacyMigrationState state = ClassifyFilesystem(input, validation);
        ConfigurationErrorCode error = state switch
        {
            LegacyMigrationState.PolicyDrift => ConfigurationErrorCode.TrackingPolicyInvalid,
            LegacyMigrationState.LegacyUnsafe => ConfigurationErrorCode.ManagedFileUnsafe,
            LegacyMigrationState.PolicyRequired => ConfigurationErrorCode.TrackingPolicyRequired,
            _ => ConfigurationErrorCode.None,
        };
        return new(state, error, error == ConfigurationErrorCode.None ? ConfigurationExitClass.Success : ConfigurationExitClass.PolicyFailure,
            input, validation);
    }

    private static string? ReadText(string path)
    {
        byte[]? bytes = TrackingTransaction.CaptureState(path).Bytes;
        return bytes is null ? null : new UTF8Encoding(false, true).GetString(bytes);
    }
}
