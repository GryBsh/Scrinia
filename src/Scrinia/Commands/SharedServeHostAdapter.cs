using Scrinia.Core.Configuration;

namespace Scrinia.Commands;

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

    public IReadOnlyList<SharedBaseRuntime> OpenEnabledBases() =>
        OpenConfiguredBases(WorkspaceConfig);

    private SharedBaseRuntime[] OpenConfiguredBases(IWorkspaceConfig workspaceConfig)
    {
        if (workspaceConfig.GetValue(Scrinia.Core.Configuration.WorkspaceConfig.EnabledKey) is null)
            throw new SharedServeStartupException("NO_SHARED_BASES_ENABLED", "No shared bases are enabled.");

        return workspaceConfig.GetEnabledBases()
            .Select(name => _registry.Lookup(name))
            .Where(entry => entry is not null)
            .Select(entry => _runtimeFactory.Open(entry!))
            .ToArray();
    }
}

internal sealed class SharedServeStartupException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}
