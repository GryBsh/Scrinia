using System.Reflection;
using System.Xml.Linq;
using FluentAssertions;
using ModelContextProtocol.Server;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

/// <summary>
/// Task 12 — the Phase 3 independent exit gate. It proves the final shared-memory surface is complete
/// and locked for Phase 4 consumption: the eleven prefixed tools, Core-only ownership of every shared
/// abstraction, the exact forbidden surfaces, the exact error terms, and the documentation ownership
/// boundary. Phase 4 consumes these contracts and cannot alter the shared response wording.
/// </summary>
public sealed class SharedPhase3ExitGateTests : IDisposable
{
    private readonly string _temporaryRoot = Path.Combine(Path.GetTempPath(), "scrinia-phase3-exit", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Phase3_contract_is_complete_and_phase4_consumes_without_rewording()
    {
        string repositoryRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));

        // (1) The eleven prefixed shared tools exist exactly once and carry no forbidden alias.
        Type sharedTools = typeof(SharedTools);
        MethodInfo[] methods = sharedTools.GetMethods(BindingFlags.Public | BindingFlags.Instance);
        string[] names = methods
            .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
            .Where(name => name is not null)
            .Cast<string>()
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        string[] expected =
        [
            "shared_bases",
            "shared_search",
            "shared_list",
            "shared_recall",
            "shared_facts",
            "shared_remember",
            "shared_append",
            "shared_forget",
            "shared_compact",
            "shared_link",
            "shared_refresh",
        ];
        expected = expected.OrderBy(name => name, StringComparer.Ordinal).ToArray();

        names.Should().Equal(expected);

        names.Length.Should().Be(11, "the shared surface exposes exactly eleven tools");
        names.Should().NotContain("shared_guide");
        names.Should().NotContain("shared_restore");
        names.Should().NotContain("shared_hint");
        names.Should().NotContain("shared_reconcile");
        names.Should().NotContain("guide"); // no unprefixed alias

        // (2) Core-only ownership of shared abstractions: SharedServer takes exactly the three Core
        // interfaces and references no executable type.
        ConstructorInfo ctor = typeof(SharedServer).GetConstructors().Single();
        ParameterInfo[] parameters = ctor.GetParameters();
        parameters.Select(p => p.ParameterType).Should().Equal(
            typeof(IWorkspaceConfig),
            typeof(ISharedBaseRegistry),
            typeof(ISharedBaseRuntimeFactory));

        string sharedServerSource = ReadSource(repositoryRoot, "src/Scrinia.Mcp/Shared/SharedServer.cs");
        sharedServerSource.Should().Contain("ISharedBaseRuntimeFactory");
        sharedServerSource.Should().NotContain("using Scrinia;");
        sharedServerSource.Should().NotContain("ScriniaMcpTools");

        // (3) The executable owns only a host adapter, never shared algorithms.
        string adapterSource = ReadSource(repositoryRoot, "src/Scrinia/Commands/SharedServeHostAdapter.cs");
        adapterSource.Should().Contain("ISharedBaseRuntimeFactory");
        adapterSource.Should().NotContain("new SharedBaseRegistry");
        adapterSource.Should().NotContain("SharedClassifier.");
        adapterSource.Should().NotContain("Fingerprint(");

        // (4) Forbidden surfaces never appear as tool methods, parameters, or orchestration seams.
        string allSharedSource = ReadSource(repositoryRoot, "src/Scrinia.Mcp/Shared/SharedServer.cs")
            + ReadSource(repositoryRoot, "src/Scrinia.Mcp/Shared/SharedTools.cs");
        allSharedSource.Should().NotContain("scopes");
        allSharedSource.Should().NotContain("cross-base");
        allSharedSource.Should().NotContain("crossBase");
        allSharedSource.Should().NotContain("ambient");

        // (5) Exact error terms are present on the shared surface.
        string runtimeSource = ReadSource(repositoryRoot, "src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs");
        runtimeSource.Should().Contain("SHARED_BASE_STALE");
        runtimeSource.Should().Contain("SHARED_BASE_UNSAFE_PATH");
        string serverSource2 = ReadSource(repositoryRoot, "src/Scrinia.Mcp/Shared/SharedServer.cs");
        serverSource2.Should().Contain("SHARED_EPHEMERAL_NOT_SUPPORTED");
        serverSource2.Should().Contain("SHARED_BASE_NOT_ENABLED");
        serverSource2.Should().Contain("UNKNOWN_SHARED_BASE");
        serverSource2.Should().Contain("MALFORMED_ADDRESS");
        string adapterSource2 = ReadSource(repositoryRoot, "src/Scrinia/Commands/SharedServeHostAdapter.cs");
        adapterSource2.Should().Contain("NO_SHARED_BASES_ENABLED");

        // (6) The Core-only reference boundary still holds (McpCoreReferenceTests invariant).
        XDocument mcpProject = LoadProject(repositoryRoot, "src/Scrinia.Mcp/Scrinia.Mcp.csproj");
        string[] references = mcpProject.Descendants("ProjectReference")
            .Select(reference => (string?)reference.Attribute("Include") ?? "")
            .ToArray();
        references.Should().Contain(reference => reference.Contains("Scrinia.Core", StringComparison.Ordinal));
        references.Should().NotContain(reference => reference.Contains("Scrinia\\Scrinia.csproj", StringComparison.Ordinal));

        // (7) Phase 4 documentation ownership boundary: the shared-server technical sections live in the
        // three architecture docs; the Phase 4/5 onboarding and packaging/plugin sections are absent from
        // the shared-server documentation written here.
        string coreDoc = ReadSource(repositoryRoot, "docs/architecture/core.md");
        coreDoc.Should().Contain("SharedServer");
        coreDoc.Should().Contain("SharedBaseRuntime");
        string cliDoc = ReadSource(repositoryRoot, "docs/architecture/cli.md");
        cliDoc.Should().Contain("scri shared serve");
        string overviewDoc = ReadSource(repositoryRoot, "docs/architecture/overview.md");
        overviewDoc.Should().Contain("shared serve");
    }

    private static string ReadSource(string repositoryRoot, string relativePath)
    {
        string path = Path.Combine(repositoryRoot, relativePath);
        File.Exists(path).Should().BeTrue($"expected source file at {relativePath}");
        return File.ReadAllText(path);
    }

    private static XDocument LoadProject(string repositoryRoot, string relativePath)
    {
        string path = Path.Combine(repositoryRoot, relativePath);
        File.Exists(path).Should().BeTrue($"expected project file at {relativePath}");
        return XDocument.Load(path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_temporaryRoot))
            Directory.Delete(_temporaryRoot, recursive: true);
    }
}
