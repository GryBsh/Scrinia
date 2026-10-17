using FluentAssertions;
using Scrinia.Core.Configuration;
using Scrinia.Mcp.SharedContracts;

namespace Scrinia.Tests.Shared;

public sealed class SharedOperationTests
{
    [Fact]
    public async Task All_ten_tools_dispatch_and_mutations_are_readable_after_restart()
    {
        using var fixture = new SharedOperationFixture();
        SharedTools tools = fixture.Tools;
        (await tools.Bases()).Should().Contain("availability: available");
        (await tools.Remember("team", "/patterns/alpha", ["First durable chunk", "Second chunk"],
            new() { ["person"] = ["alice"] }, ["durable"])).Should().Contain("mutation: applied");
        (await tools.Remember("team", "/patterns/alpha", ["Third chunk"])).Should().Contain("mutation: applied");
        (await tools.Recall("team", "/patterns/alpha", 3)).Should().Contain("Third chunk");
        (await tools.Search("durable")).Should().Contain("path: /patterns/alpha");
        (await tools.List()).Should().Contain("total: 1");
        (await tools.Facts("team", "entities")).Should().Contain("person:alice");
        (await tools.Remember("team", "/patterns/beta", ["Other memory"])).Should().Contain("mutation: applied");
        (await tools.Link("team", "/patterns/alpha", "/patterns/beta")).Should().Contain("destination: /patterns/beta");
        (await tools.Recall("team", "/patterns/beta")).Should().Contain("/patterns/alpha");
        (await tools.Compact("team", "/patterns/alpha")).Should().Contain("mutation: applied");
        (await tools.Compact("team", "/patterns/alpha")).Should().Contain("mutation: none");
        (await tools.Refresh("team")).Should().Contain("availability: available");
        (await fixture.Restart().Recall("team", "/patterns/alpha")).Should().Contain("Third chunk");
        (await tools.Forget("team", "/patterns/beta")).Should().Contain("mutation: applied");
        (await tools.Recall("team", "/patterns/beta")).Should().Contain("SHARED_ENTRY_NOT_FOUND");
    }

    [Theory]
    [InlineData("/skill/reviewer")]
    [InlineData("/agent/preferences")]
    public async Task File_entities_are_base_local_persistent_and_do_not_fall_back_to_builtins(string path)
    {
        using var fixture = new SharedOperationFixture();
        (await fixture.Tools.Remember("team", path, ["First body"])).Should().Contain("mutation: applied");
        (await fixture.Tools.Append("team", path, "Second body")).Should().Contain("mutation: applied");
        (await fixture.Restart().Recall("team", path)).Should().Contain("First body").And.Contain("Second body");
        (await fixture.Tools.Forget("team", path)).Should().Contain("mutation: applied");
        (await fixture.Tools.Recall("team", "/skill/qa")).Should().Contain("SHARED_ENTRY_NOT_FOUND");
    }
}

internal sealed class SharedOperationFixture : IDisposable
{
    internal string Root { get; } = Path.Combine(Path.GetTempPath(), "scri-shared-" + Guid.NewGuid().ToString("N"));
    internal string BaseRoot => Path.Combine(Root, "team");
    internal SharedTools Tools { get; }
    internal SharedBaseRuntimeFactory Factory { get; } = new();
    internal SharedOperationFixture()
    {
        Directory.CreateDirectory(Path.Combine(BaseRoot, ".git"));
        Directory.CreateDirectory(Path.Combine(BaseRoot, ".scrinia"));
        File.WriteAllText(Path.Combine(BaseRoot, ".scrinia", ".gitignore"), ManagedIgnoreBlock.ProjectGit);
        Tools = CreateTools(Factory);
    }
    internal SharedTools Restart()
        => CreateTools(new SharedBaseRuntimeFactory());

    internal static SharedTools Select(params (string Name, string? Root)[] bases)
    {
        var workspace = new WorkspaceConfig(new Dictionary<string, string>
            { [WorkspaceConfig.EnabledKey] = string.Join(';', bases.Select(item => item.Name)) });
        return new SharedTools(new SharedServer(workspace,
            new FixtureRegistry(bases.Where(item => item.Root is not null)
                .Select(item => new SharedBaseEntry { Name = item.Name, Root = item.Root! }).ToArray()), new SharedBaseRuntimeFactory()));
    }

    private SharedTools CreateTools(SharedBaseRuntimeFactory factory)
    {
        var workspace = new WorkspaceConfig(new Dictionary<string, string> { [WorkspaceConfig.EnabledKey] = "team" });
        return new SharedTools(new SharedServer(workspace, new FixtureRegistry(new SharedBaseEntry { Name = "team", Root = BaseRoot }), factory));
    }
    public void Dispose() => Directory.Delete(Root, true);

    private sealed class FixtureRegistry(params SharedBaseEntry[] entries) : ISharedBaseRegistry
    {
        public SharedBaseEntry? Lookup(string name) => entries.FirstOrDefault(entry => entry.Name == name);
        public SharedBaseResult Add(string name, string root) => throw new NotSupportedException();
        public SharedBaseResult Remove(string name) => throw new NotSupportedException();
        public IReadOnlyList<SharedBaseEntry> List(bool includeDisabled) => entries;
        public SharedBaseResult Enable(string name) => throw new NotSupportedException();
        public SharedBaseResult Disable(string name) => throw new NotSupportedException();
        public IReadOnlyList<SharedBaseCollision> Collisions() => [];
    }
}
