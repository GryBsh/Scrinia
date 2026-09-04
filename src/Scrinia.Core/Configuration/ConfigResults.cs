namespace Scrinia.Core.Configuration;

public enum ConfigurationErrorCode { None, WorkspaceConfigMalformed, TrackingPolicyInvalid, ConfigWriteFailed }
public enum ConfigurationExitClass { Success = 0, InvalidConfiguration = 2, PolicyFailure = 3, WriteFailure = 4 }
public sealed record ConfigurationResult(ConfigurationErrorCode ErrorCode = ConfigurationErrorCode.None, ConfigurationExitClass ExitClass = ConfigurationExitClass.Success)
{
    public bool Succeeded => ErrorCode == ConfigurationErrorCode.None;
}
