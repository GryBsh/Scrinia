using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core.Configuration;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;

namespace Scrinia.Tests.Shared;

public sealed class NoBaseStartupTests
{
    [Fact]
    public void No_enabled_key_fails_before_transport_with_NO_SHARED_BASES_ENABLED()
    {
        var adapter = new SharedServeHostAdapter(
            new HomeConfigStore(),
            new CoreWorkspaceConfig(),
            new ThrowingRegistry(),
            new ThrowingRuntimeFactory());

        Func<IReadOnlyList<SharedBaseRuntime>> open = adapter.OpenEnabledBases;

        open.Should().Throw<SharedServeStartupException>()
            .Which.Code.Should().Be("NO_SHARED_BASES_ENABLED");
    }

    [Fact]
    public async Task Real_shared_serve_command_fails_before_host_transport_for_absent_enabled_key()
    {
        string workspaceRoot = Path.Combine(Path.GetTempPath(), "scrinia-task4-command-" + Guid.NewGuid().ToString("N"));
        string homeRoot = Path.Combine(Path.GetTempPath(), "scrinia-task4-home-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workspaceRoot);
        Directory.CreateDirectory(homeRoot);
        string? previousHome = Environment.GetEnvironmentVariable("SCRINIA_HOME");
        Environment.SetEnvironmentVariable("SCRINIA_HOME", homeRoot);
        try
        {
            Func<Task<int>> serve = () => new SharedServeCommands().Serve(workspaceRoot);

            await serve.Should().ThrowAsync<SharedServeStartupException>()
                .Where(exception => exception.Code == "NO_SHARED_BASES_ENABLED");
        }
        finally
        {
            Environment.SetEnvironmentVariable("SCRINIA_HOME", previousHome);
            Directory.Delete(workspaceRoot, recursive: true);
            Directory.Delete(homeRoot, recursive: true);
        }
    }

    private sealed class ThrowingRegistry : ISharedBaseRegistry
    {
        public SharedBaseResult Add(string name, string root) => throw new InvalidOperationException();
        public SharedBaseResult Remove(string name) => throw new InvalidOperationException();
        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) => throw new InvalidOperationException();
        public SharedBaseResult Enable(string name) => throw new InvalidOperationException();
        public SharedBaseResult Disable(string name) => throw new InvalidOperationException();
        public SharedBaseEntry? Lookup(string name) => throw new InvalidOperationException("registry must not be consulted");
        public IReadOnlyList<SharedBaseCollision> Collisions() => throw new InvalidOperationException();
    }

    private sealed class ThrowingRuntimeFactory : ISharedBaseRuntimeFactory
    {
        public SharedBaseRuntime Open(SharedBaseEntry entry) => throw new InvalidOperationException("transport must not be constructed");
    }
}
