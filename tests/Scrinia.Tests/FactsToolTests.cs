using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

// GlobalWorkspaceConfigCollection: Cli_Facts_Entities_PrintsYaml and
// Cli_Facts_Intersect_SplitsCommaSeparatedTarget drive ScriniaCommands.Facts, which calls
// WorkspaceSetup.Configure (a plain static field guarded only by a lock for writes) — the same
// hazard the collection exists to serialize. They also redirect the process-wide Console.Out
// around that call; without this collection, a class outside it (e.g. CliEphemeralRefusalTests)
// can run its own Console.SetOut/restore concurrently and either class ends up reading the
// other's captured output.
[Collection(GlobalWorkspaceConfigCollection.Name)]
public class FactsToolTests
{
    private static ArtifactEntry Note(string name, params (string Key, string[] Vals)[] attrs)
    {
        var d = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (k, v) in attrs) d[k] = v;
        return new ArtifactEntry(name, "uri", 1, 1, new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero),
            ContentPreview: "prose", Attributes: d);
    }

    private static void Seed(IMemoryStore store)
    {
        store.Upsert(Note("trip", ("person", ["jolene"]), ("place", ["france"])), "local:findings");
        store.Upsert(Note("solo", ("person", ["jolene"])), "local:findings");
        store.Upsert(Note("plan1", ("person", ["jolene"])), "local:plan");
    }

    [Fact]
    public async Task Entities_ListsVocabulary()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("entities");
        yaml.Should().Contain("status: success").And.Contain("person:jolene");
    }

    [Fact]
    public async Task Profile_ReturnsDeepDive()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("profile", entity: "person:jolene");
        yaml.Should().Contain("profile: person:jolene").And.Contain("mentions: 3"); // trip+solo+plan1 cross-scope
    }

    [Fact]
    public async Task Intersect_ReturnsSharedMemories()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("intersect", entities: ["person:jolene", "place:france"]);
        yaml.Should().Contain("memories: 1").And.Contain("trip");
    }

    [Fact]
    public async Task Scopes_Narrow_ExcludeTopic_DropsPlanScope()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var yaml = await new ScriniaMcpTools().Facts("profile", entity: "person:jolene", excludeTopics: "plan");
        yaml.Should().Contain("mentions: 2"); // plan1 excluded
    }

    [Fact]
    public async Task UnknownOp_IsError()
    {
        using var scope = new TestHelpers.StoreScope();
        var yaml = await new ScriniaMcpTools().Facts("bogus");
        yaml.Should().Contain("status: error").And.Contain("INVALID_PARAMETER");
    }

    [Fact]
    public async Task Profile_MissingEntity_IsError()
    {
        using var scope = new TestHelpers.StoreScope();
        var yaml = await new ScriniaMcpTools().Facts("profile");
        yaml.Should().Contain("status: error");
    }

    [Fact]
    public async Task Intersect_FewerThanTwo_IsError()
    {
        using var scope = new TestHelpers.StoreScope();
        var yaml = await new ScriniaMcpTools().Facts("intersect", entities: ["person:jolene"]);
        yaml.Should().Contain("status: error");
    }

    [Fact]
    public async Task Cli_Facts_Entities_PrintsYaml()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var prevOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            int code = await new Scrinia.Commands.ScriniaCommands()
                .Facts("entities", workspaceRoot: scope.WorkspaceDir);
            code.Should().Be(0);
        }
        finally { Console.SetOut(prevOut); }

        sw.ToString().Should().Contain("action: facts").And.Contain("person:jolene");
    }

    [Fact]
    public async Task Cli_Facts_Intersect_SplitsCommaSeparatedTarget()
    {
        using var scope = new TestHelpers.StoreScope();
        Seed(MemoryStoreContext.Current!);
        var prevOut = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try
        {
            int code = await new Scrinia.Commands.ScriniaCommands()
                .Facts("intersect", target: "person:jolene,place:france", workspaceRoot: scope.WorkspaceDir);
            code.Should().Be(0);
        }
        finally { Console.SetOut(prevOut); }

        sw.ToString().Should().Contain("memories: 1").And.Contain("trip");
    }
}
