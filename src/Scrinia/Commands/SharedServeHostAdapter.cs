using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Commands;

/// <summary>
/// Executable composition root for <c>scri shared serve</c>: resolves the Core services, fails before
/// transport when no base is enabled, and opens every enabled base that Core can validate. Unavailable
/// bases (deleted or otherwise invalid roots) are skipped so a single bad base never fails startup.
/// </summary>
internal sealed class SharedServeHostAdapter
{
    private readonly ISharedBaseRegistry _registry;
    private readonly ISharedBaseRuntimeFactory _runtimeFactory;

    public SharedServeHostAdapter(
        IHomeConfigStore homeConfigStore,
        IWorkspaceConfig workspaceConfig,
        ISharedBaseRegistry registry,
        ISharedBaseRuntimeFactory runtimeFactory)
    {
        HomeConfigStore = homeConfigStore ?? throw new ArgumentNullException(nameof(homeConfigStore));
        WorkspaceConfig = workspaceConfig ?? throw new ArgumentNullException(nameof(workspaceConfig));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _runtimeFactory = runtimeFactory ?? throw new ArgumentNullException(nameof(runtimeFactory));
    }

    public IHomeConfigStore HomeConfigStore { get; }
    public IWorkspaceConfig WorkspaceConfig { get; }

    /// <summary>
    /// Opens every enabled base that Core can validate. Fails before transport when the Enabled key is
    /// absent; an enabled base whose root is unavailable is skipped rather than failing startup.
    /// </summary>
    public IReadOnlyList<SharedBaseRuntime> OpenEnabledBases()
    {
        EnsureEnabledConfiguration(WorkspaceConfig);
        return OpenOpenableBases();
    }

    internal void ValidateStartup(SharedServer server)
    {
        EnsureEnabledConfiguration(WorkspaceConfig);
        SharedEnvelope? firstFailure = null;
        foreach (string name in WorkspaceConfig.GetEnabledBases().Order(StringComparer.Ordinal))
        {
            SharedEnvelope result = server.Refresh(name, CancellationToken.None);
            if (!result.IsError) return;
            firstFailure ??= result;
        }
        throw new SharedServeStartupException(firstFailure!.ErrorCode!, firstFailure.Error!);
    }

    private static void EnsureEnabledConfiguration(IWorkspaceConfig workspaceConfig)
    {
        if (workspaceConfig.GetValue(CoreWorkspaceConfig.EnabledKey) is null)
            throw new SharedServeStartupException("NO_SHARED_BASES_ENABLED", "No shared bases are enabled.");

        if (workspaceConfig.GetEnabledBases().Count == 0)
            throw new SharedServeStartupException("NO_SHARED_BASES_ENABLED", "No shared bases are enabled.");
    }

    private SharedBaseRuntime[] OpenOpenableBases()
    {
        var runtimes = new List<SharedBaseRuntime>();
        var server = new SharedServer(WorkspaceConfig, _registry, _runtimeFactory);
        foreach (string name in WorkspaceConfig.GetEnabledBases())
        {
            SharedBaseEntry? entry = _registry.Lookup(name);
            if (entry is null)
                continue;
            try
            {
                runtimes.Add(server.OpenSelectedBase(name));
            }
            catch
            {
                // An unavailable base must not fail startup; it surfaces as Unavailable on
                // shared_bases and a freshness error on targeted operations.
            }
        }
        return runtimes.ToArray();
    }
}

internal sealed class SharedServeStartupException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
