using System.Text.Json.Serialization;

namespace Scrinia.Core.Configuration;

public enum ConfigurationErrorCode
{
    None,
    CliInvalidArgument,
    WorkspaceConfigMalformed,
    TrackingPolicyInvalid,
    ConfigWriteFailed,
    SharedBaseAlreadyExists,
    SharedBaseNotFound,
    SharedBaseRootCollision,
    SharedBaseUnsafePath,
    SharedBaseNotGitRoot,
    SharedTrackingPolicyDrift,
    UserConfigMalformed,
    UserConfigSchemaUnsupported,
    LegacyMigrationRequired,
    ManagedFileUnsafe,
    TrackingPolicyRequired,
    MigrationManualCleanupRequired,
    ConfigLockTimeout,
    IoFailure,
    ConfigRecoveryRequired,
}
public enum ConfigurationExitClass { Success = 0, InvalidConfiguration = 2, PolicyFailure = 3, WriteFailure = 4 }
public sealed record ConfigurationResult(
    ConfigurationErrorCode ErrorCode = ConfigurationErrorCode.None,
    ConfigurationExitClass ExitClass = ConfigurationExitClass.Success,
    Exception? Exception = null)
{
    public bool Succeeded => ErrorCode == ConfigurationErrorCode.None;
}

public sealed record SharedBaseResult(
    ConfigurationErrorCode ErrorCode = ConfigurationErrorCode.None,
    ConfigurationExitClass ExitClass = ConfigurationExitClass.Success,
    SharedBaseEntry? Base = null,
    Exception? Exception = null)
{
    public bool Succeeded => ErrorCode == ConfigurationErrorCode.None;
}

public sealed record SharedBaseCollision(string Name, string Root, string OtherName, string OtherRoot);

public static class ConfigResults
{
    private static readonly Dictionary<ConfigurationErrorCode, (string Code, ConfigurationExitClass ExitClass)> ErrorContracts =
        new Dictionary<ConfigurationErrorCode, (string, ConfigurationExitClass)>
        {
            [ConfigurationErrorCode.WorkspaceConfigMalformed] = ("WORKSPACE_CONFIG_MALFORMED", ConfigurationExitClass.InvalidConfiguration),
            [ConfigurationErrorCode.CliInvalidArgument] = ("CLI_INVALID_ARGUMENT", ConfigurationExitClass.InvalidConfiguration),
            [ConfigurationErrorCode.TrackingPolicyInvalid] = ("TRACKING_POLICY_INVALID", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.ConfigWriteFailed] = ("CONFIG_WRITE_FAILED", ConfigurationExitClass.WriteFailure),
            [ConfigurationErrorCode.SharedBaseAlreadyExists] = ("SHARED_BASE_ALREADY_EXISTS", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.SharedBaseNotFound] = ("SHARED_BASE_NOT_FOUND", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.SharedBaseRootCollision] = ("SHARED_BASE_ROOT_COLLISION", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.SharedBaseUnsafePath] = ("SHARED_BASE_UNSAFE_PATH", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.SharedBaseNotGitRoot] = ("SHARED_BASE_NOT_GIT_ROOT", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.SharedTrackingPolicyDrift] = ("SHARED_TRACKING_POLICY_DRIFT", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.UserConfigMalformed] = ("USER_CONFIG_MALFORMED", ConfigurationExitClass.InvalidConfiguration),
            [ConfigurationErrorCode.UserConfigSchemaUnsupported] = ("USER_CONFIG_SCHEMA_UNSUPPORTED", ConfigurationExitClass.InvalidConfiguration),
            [ConfigurationErrorCode.LegacyMigrationRequired] = ("LEGACY_MIGRATION_REQUIRED", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.ManagedFileUnsafe] = ("MANAGED_FILE_UNSAFE", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.TrackingPolicyRequired] = ("TRACKING_POLICY_REQUIRED", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.MigrationManualCleanupRequired] = ("MIGRATION_MANUAL_CLEANUP_REQUIRED", ConfigurationExitClass.PolicyFailure),
            [ConfigurationErrorCode.ConfigLockTimeout] = ("CONFIG_LOCK_TIMEOUT", ConfigurationExitClass.WriteFailure),
            [ConfigurationErrorCode.IoFailure] = ("IO_FAILURE", ConfigurationExitClass.WriteFailure),
            [ConfigurationErrorCode.ConfigRecoveryRequired] = ("CONFIG_RECOVERY_REQUIRED", ConfigurationExitClass.WriteFailure),
        };

    public static string Code(ConfigurationErrorCode errorCode) =>
        errorCode == ConfigurationErrorCode.None ? "" : ErrorContracts.TryGetValue(errorCode, out var contract)
            ? contract.Code
            : throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "The configuration error has no declared CLI contract.");

    public static ConfigurationExitClass ExitClassFor(ConfigurationErrorCode errorCode) =>
        errorCode == ConfigurationErrorCode.None ? ConfigurationExitClass.Success : ErrorContracts.TryGetValue(errorCode, out var contract)
            ? contract.ExitClass
            : throw new ArgumentOutOfRangeException(nameof(errorCode), errorCode, "The configuration error has no declared CLI contract.");

    public static ConfigurationResult ConfigWriteFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return new ConfigurationResult(ConfigurationErrorCode.ConfigWriteFailed, ConfigurationExitClass.WriteFailure, exception);
    }

    public static ConfigurationResult FromException(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return exception is ScriniaConfigurationException { InnerException: DurableWriteException }
            ? ConfigWriteFailed(exception)
            : throw new ArgumentException("The exception is not a classified configuration failure.", nameof(exception));
    }
}

public sealed record SharedBaseJsonItem(string Name, string Root, string Validation);

public sealed record ConfigurationJsonResult(
    string Status,
    string Command,
    bool Changed,
    IReadOnlyList<string> Warnings,
    string GitIndexStatus,
    IReadOnlyList<IReadOnlyList<string>> GitCommands,
    string? Error = null,
    string? ErrorCode = null,
    string? Base = null,
    IReadOnlyList<string>? Manifest = null,
    IReadOnlyList<SharedBaseJsonItem>? Bases = null,
    string? Tracking = null,
    string? State = null)
{
    public static ConfigurationJsonResult Success(
        string command,
        bool changed,
        string? @base = null,
        IReadOnlyList<string>? manifest = null,
        IReadOnlyList<SharedBaseJsonItem>? bases = null,
        string? tracking = null,
        string? state = null,
        IReadOnlyList<string>? warnings = null,
        IReadOnlyList<IReadOnlyList<string>>? gitCommands = null) =>
        new("success", command, changed, warnings ?? [], "UNVERIFIED", gitCommands ?? [], null, null, @base, manifest, bases, tracking, state);

    public static ConfigurationJsonResult Failure(string command, ConfigurationErrorCode errorCode, string? error = null) =>
        new("error", command, false, [], "UNVERIFIED", [], error, ConfigResults.Code(errorCode));
}
