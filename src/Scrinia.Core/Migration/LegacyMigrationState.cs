using Scrinia.Core.Configuration;
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

    public LegacyFilesystemObservation Inspect(string workspaceRoot)
    {
        try
        {
            using FileLock fileLock = AcquireConfigurationLock(workspaceRoot);
            Recover(workspaceRoot);
            return InspectLocked(workspaceRoot);
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
        catch (IOException)
        {
            return new(LegacyMigrationState.LegacyUnsafe, ConfigurationErrorCode.IoFailure,
                ConfigurationExitClass.WriteFailure, null, ManagedIgnoreValidation.Drift);
        }
    }

    public LegacyMigrationInput Read(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        Dictionary<string, string> values = WorkspaceConfig.LoadValuesUncached(workspaceRoot);
        TrackingPolicy? policy = values.TryGetValue(WorkspaceConfig.TrackingKey, out string? raw)
            ? WorkspaceConfig.ParseTracking(raw) switch
            {
                TrackingPolicy.Unset => null,
                var value => value,
            } : null;
        string directory = Path.Combine(workspaceRoot, ".scrinia");
        return new(
            HasAuthoritativeData(directory),
            policy,
            ReadText(Path.Combine(directory, ".gitattributes")),
            ReadText(Path.Combine(directory, "merge.config")),
            false, false, ReadText(Path.Combine(directory, ".gitignore")));
    }

    public LegacyMigrationResult Migrate(string workspaceRoot, TrackingPolicy targetPolicy)
    {
        ArgumentException.ThrowIfNullOrEmpty(workspaceRoot);
        string directory = Path.Combine(workspaceRoot, ".scrinia");
        string configPath = Path.Combine(directory, "config.json");
        string ignorePath = Path.Combine(directory, ".gitignore");
        string attributesPath = Path.Combine(directory, ".gitattributes");
        string mergePath = Path.Combine(directory, "merge.config");
        Directory.CreateDirectory(directory);
        FileLock fileLock;
        try { fileLock = AcquireConfigurationLock(workspaceRoot); }
        catch (FileLockTimeoutException) { return new(ConfigurationErrorCode.ConfigLockTimeout, ConfigurationExitClass.WriteFailure); }
        using (fileLock)
        {
        try { Recover(workspaceRoot); }
        catch (Exception exception) { return MapFailure(exception); }
        LegacyFilesystemObservation observation = InspectLocked(workspaceRoot);
        if (observation.ErrorCode is not ConfigurationErrorCode.None and not ConfigurationErrorCode.TrackingPolicyRequired)
            return new(observation.ErrorCode, observation.ExitClass, false);
        LegacyMigrationInput input = observation.Input!;
        LegacyMigrationPlan plan;
        try { plan = LegacyMigrationStateMachine.PlanMigration(input, targetPolicy); }
        catch (ScriniaConfigurationException) { return new(ConfigurationErrorCode.TrackingPolicyInvalid, ConfigurationExitClass.PolicyFailure); }
        LegacyMigrationResult gate = LegacyMigrationStateMachine.ApplyCommand(plan.State, targetPolicy, isMigration: true);
        if (!gate.Succeeded) return gate;
        if (plan.State == (targetPolicy == TrackingPolicy.Local ? LegacyMigrationState.Local : LegacyMigrationState.ProjectGit))
            return gate with { Changed = false, Warnings = plan.Warnings, DriverUnsetArgv = plan.DriverUnsetArgv };
        var originals = new Dictionary<string, byte[]?>(StringComparer.OrdinalIgnoreCase)
        {
            [configPath] = ReadBytes(configPath), [ignorePath] = ReadBytes(ignorePath),
            [attributesPath] = ReadBytes(attributesPath), [mergePath] = ReadBytes(mergePath),
        };
        try
        {
            Dictionary<string, string> values = WorkspaceConfig.LoadValuesUncached(workspaceRoot);
            values[WorkspaceConfig.TrackingKey] = targetPolicy switch
            {
                TrackingPolicy.Local => "local", TrackingPolicy.ProjectGit => "project-git",
                _ => throw new ScriniaConfigurationException("Unsupported migration target."),
            };
            WriteJournal(configPath, originals);
            DurableFile.AtomicWriteAllText(configPath, System.Text.Json.JsonSerializer.Serialize(values, ConfigJsonContext.Default.DictionaryStringString));
            DurableFile.AtomicWriteAllText(ignorePath, ManagedIgnoreBlock.Apply(ReadText(ignorePath), targetPolicy));
            WriteOrDelete(attributesPath, plan.GitAttributesContent, plan.DeleteGitAttributes);
            WriteOrDelete(mergePath, input.MergeConfig, plan.DeleteMergeConfig);
            DurableFile.AtomicWriteAllText(Path.Combine(directory, RecoveryJournalName), "complete\n");
            File.Delete(Path.Combine(directory, RecoveryJournalName));
            WorkspaceConfig.RefreshCache(workspaceRoot, values);
            return gate with { Warnings = plan.Warnings, DriverUnsetArgv = plan.DriverUnsetArgv };
        }
        catch (DurableWriteException)
        {
            TryRestore(originals);
            return gate with { ErrorCode = ConfigurationErrorCode.ConfigWriteFailed, ExitClass = ConfigurationExitClass.WriteFailure,
                Changed = false, Warnings = [], DriverUnsetArgv = [] };
        }
        catch (UnauthorizedAccessException)
        {
            TryRestore(originals);
            return gate with { ErrorCode = ConfigurationErrorCode.IoFailure, ExitClass = ConfigurationExitClass.WriteFailure,
                Changed = false, Warnings = [], DriverUnsetArgv = [] };
        }
        catch (IOException)
        {
            TryRestore(originals);
            return gate with { ErrorCode = ConfigurationErrorCode.IoFailure, ExitClass = ConfigurationExitClass.WriteFailure,
                Changed = false, Warnings = [], DriverUnsetArgv = [] };
        }
        }
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
            .Any(path => Path.GetFileName(path) is not ("config.json" or ".gitignore" or ".gitattributes" or "merge.config" or RecoveryJournalName));
    }

    private static void WriteJournal(string configPath, Dictionary<string, byte[]?> originals)
    {
        string directory = Path.GetDirectoryName(configPath)!;
        var lines = new List<string> { "prepared" };
        foreach ((string path, byte[]? bytes) in originals)
            lines.Add(Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(path)) + "|" + (bytes is null ? "-" : Convert.ToBase64String(bytes)));
        DurableFile.AtomicWriteAllText(Path.Combine(directory, RecoveryJournalName), string.Join('\n', lines) + '\n');
    }

    private static void Recover(string workspaceRoot)
    {
        string path = Path.Combine(workspaceRoot, ".scrinia", RecoveryJournalName);
        if (!File.Exists(path)) return;
        string[] lines = File.ReadAllLines(path);
        if (lines.Length == 0) throw new ScriniaConfigurationException("The tracking recovery journal is malformed.");
        if (lines[0] == "complete") { File.Delete(path); return; }
        if (lines[0] != "prepared") throw new ScriniaConfigurationException("The tracking recovery journal is malformed.");
        string directory = Path.GetFullPath(Path.Combine(workspaceRoot, ".scrinia"));
        var allowedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "config.json", ".gitignore", ".gitattributes", "merge.config" };
        var destinations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.Skip(1))
        {
            string[] parts = line.Split('|', 2);
            if (parts.Length != 2) throw new ScriniaConfigurationException("The tracking recovery journal is malformed.");
            string destination = System.Text.Encoding.UTF8.GetString(Convert.FromBase64String(parts[0]));
            string fullDestination = Path.GetFullPath(destination);
            if (!PathContainment.IsWithin(directory, fullDestination) ||
                !string.Equals(Path.GetDirectoryName(fullDestination), directory, StringComparison.OrdinalIgnoreCase) ||
                !allowedNames.Contains(Path.GetFileName(fullDestination)) || !destinations.Add(fullDestination))
                throw new ScriniaConfigurationException("The tracking recovery journal contains an unsafe path.");
            byte[]? bytes = parts[1] == "-" ? null : Convert.FromBase64String(parts[1]);
            if (bytes is null) { if (File.Exists(fullDestination)) File.Delete(fullDestination); }
            else DurableFile.AtomicWriteAllBytes(fullDestination, bytes);
        }
        File.Delete(path);
    }

    private static LegacyMigrationResult MapFailure(Exception exception) => exception switch
    {
        FileLockTimeoutException => new(ConfigurationErrorCode.ConfigLockTimeout, ConfigurationExitClass.WriteFailure),
        DurableWriteException => new(ConfigurationErrorCode.ConfigWriteFailed, ConfigurationExitClass.WriteFailure),
        ScriniaConfigurationException { InnerException: DurableWriteException } => new(ConfigurationErrorCode.ConfigWriteFailed, ConfigurationExitClass.WriteFailure),
        UnauthorizedAccessException or IOException => new(ConfigurationErrorCode.IoFailure, ConfigurationExitClass.WriteFailure),
        _ => new(ConfigurationErrorCode.IoFailure, ConfigurationExitClass.WriteFailure),
    };

    private static FileLock AcquireConfigurationLock(string workspaceRoot) =>
        FileLock.AcquireExclusive(ConfigurationLock.GetPath(Path.Combine(workspaceRoot, ".scrinia", "config.json")));

    private LegacyFilesystemObservation InspectLocked(string workspaceRoot)
    {
        LegacyMigrationInput input = Read(workspaceRoot);
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

    private static string? ReadText(string path) => File.Exists(path) ? new UTF8Encoding(false, true).GetString(File.ReadAllBytes(path)) : null;
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

    private static void TryRestore(Dictionary<string, byte[]?> originals)
    {
        try { Restore(originals); }
        catch { /* The original write failure remains the declared boundary result. */ }
    }
}
