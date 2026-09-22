namespace Scrinia.Core.Configuration;

public static class EffectivePolicy
{
    public const string EnvSingle = "SCRINIA_WORKSPACE_TRACKING";
    public const string EnvDouble = "SCRINIA__WORKSPACE__TRACKING";
    public const string FileKey = "Scrinia:Workspace:Tracking";

    /// <summary>Single tracking-precedence function: single-underscore env wins over double-underscore env wins over file value wins over Unset. Malformed throws via ParseTracking.</summary>
    public static TrackingPolicy ResolveEffective(string? envSingle, string? envDouble, string? fileValue) =>
        WorkspaceConfig.ParseTracking(envSingle ?? envDouble ?? fileValue);
}
