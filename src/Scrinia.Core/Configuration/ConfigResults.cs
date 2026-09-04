namespace Scrinia.Core.Configuration;

public enum ConfigurationErrorCode
{
    None,
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
