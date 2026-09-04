namespace Scrinia.Core.Configuration;

public enum TrackingPolicy
{
    Unset,
    Local,
    ProjectGit,
}

public interface ITrackingPolicyStore
{
    TrackingPolicy GetTrackingPolicy();
}

public sealed class TrackingPolicyStore(IWorkspaceConfig workspaceConfig) : ITrackingPolicyStore
{
    public TrackingPolicy GetTrackingPolicy() => workspaceConfig.GetTrackingPolicy();
}
