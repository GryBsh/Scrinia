using System.Linq;
using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using CoreWorkspaceConfig = Scrinia.Core.Configuration.WorkspaceConfig;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Proves the shared surface is driven by Core implementations, not by temporary adapter seams or
/// executable contract references. The local/scoped seams that stood in for the real wiring during
/// earlier phases must be gone: an enabled, valid base opens through the Core runtime factory.
/// </summary>
public sealed class SharedNoAdapterTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "scrinia-task10-adapter", Guid.NewGuid().ToString("N"));

    [Fact]
    public void No_temporary_shared_adapter_or_executable_contract_reference_remains()
    {
        // The host adapter wires the runtime factory, not a placeholder return: an enabled and
        // valid persistent base is opened through Core and handed back by the startup gate.
        using ValidRoot alpha = ValidRoot.Create("alpha");
        var workspace = new CoreWorkspaceConfig(new Dictionary<string, string>
        {
            [CoreWorkspaceConfig.EnabledKey] = "alpha"
        });
        var registry = new FixtureRegistry();
        registry.Register("alpha", alpha.FullPath);
        var factory = new SharedBaseRuntimeFactory();
        var adapter = new SharedServeHostAdapter(new HomeConfigStore(), workspace, registry, factory);

        IReadOnlyList<SharedBaseRuntime> opened = adapter.OpenEnabledBases();

        opened.Should().ContainSingle();
        opened.Single().BaseName.Should().Be("alpha");
        opened.Single().RegisteredRoot.Should().Be(PathContainment.ResolveCanonical(alpha.FullPath));
        opened.Single().Snapshot.Identity.BaseName.Should().Be("alpha");

        // The Core/MCP boundary holds: Scrinia.Mcp references Core and never the executable.
        XDocument mcpProject = LoadProject("src/Scrinia.Mcp/Scrinia.Mcp.csproj");
        string[] references = mcpProject.Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? "")
            .ToArray();
        references.Should().Contain(reference => reference.Contains("Scrinia.Core", StringComparison.Ordinal));
        references.Should().NotContain(reference => reference.Contains("Scrinia\\Scrinia.csproj", StringComparison.Ordinal));

        // Shared orchestration consumes Core interfaces only; no executable or McpResponse seam leaks in.
        ConstructorInfo constructor = typeof(SharedServer).GetConstructors().Single();
        constructor.GetParameters()
            .Select(parameter => parameter.ParameterType.Name)
            .Should().BeEquivalentTo(
                nameof(IWorkspaceConfig),
                nameof(ISharedBaseRegistry),
                nameof(ISharedBaseRuntimeFactory));
    }

    private sealed class FixtureRegistry : ISharedBaseRegistry
    {
        private readonly Dictionary<string, SharedBaseEntry> _entries = new(StringComparer.Ordinal);

        public SharedBaseResult Add(string name, string root) => throw new NotSupportedException();
        public SharedBaseResult Remove(string name) => throw new NotSupportedException();
        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) => _entries.Values.ToArray();
        public SharedBaseResult Enable(string name) => throw new NotSupportedException();
        public SharedBaseResult Disable(string name) => throw new NotSupportedException();

        public SharedBaseEntry? Lookup(string name) =>
            _entries.TryGetValue(name, out SharedBaseEntry? entry) ? entry : null;

        public IReadOnlyList<SharedBaseCollision> Collisions() => [];

        public void Register(string name, string root) =>
            _entries[name] = new SharedBaseEntry { Name = name, Root = root };
    }

    private sealed class ValidRoot : IDisposable
    {
        public required string FullPath { get; init; }

        public static ValidRoot Create(string name)
        {
            string path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "scrinia-task10-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(System.IO.Path.Combine(path, ".scrinia"));
            return new ValidRoot { FullPath = path };
        }

        public void Dispose() => Directory.Delete(FullPath, recursive: true);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }

    private static XDocument LoadProject(string relativePath) =>
        XDocument.Load(Path.Combine(RepositoryRoot, relativePath));

    private static string RepositoryRoot =>
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
}
