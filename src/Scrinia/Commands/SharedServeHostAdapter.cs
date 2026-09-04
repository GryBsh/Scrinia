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
        WorkspaceConfig.GetEnabledBases()
            .Select(name => _registry.Lookup(name))
            .Where(entry => entry is not null)
            .Select(entry => _runtimeFactory.Open(entry!))
            .ToArray();
}
