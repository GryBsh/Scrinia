using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

public sealed class CiPortabilityRegressionTests
{
    [SkippableFact]
    public async Task MetadataRead_ResolvesStoredCase_ButRejectsMissingBody()
    {
        using var directory = CaseSensitiveDirectory.TryCreate("scrinia-read-case");
        Skip.If(directory is null, "A case-sensitive directory is unavailable.");
        using var store = new FileMemoryStore(directory!.Path);
        var stored = MemoryAddress.Parse("/api/Notes");
        await store.WriteAsync(stored, MemoryWrite.Create(stored, ["stored content"], new Dictionary<string, IReadOnlyList<string>>(), []));

        var found = await store.ReadAsync(MemoryAddress.Parse("/api/NOTES"));
        found.Should().NotBeNull();
        found!.Name.Should().Be("Notes");

        File.Delete(store.ArtifactPath("Notes", "local:api"));
        (await store.ReadAsync(MemoryAddress.Parse("/api/NOTES"))).Should().BeNull();
    }

    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void LinkTarget_WithLinkedAncestor_IsFullyResolved(bool outside)
    {
        using var scope = new TestHelpers.StoreScope();
        string physical = Path.Combine(scope.WorkspaceDir, "physical");
        string target = Path.Combine(physical, outside ? "outside" : "boundary", "target");
        string boundary = Path.Combine(physical, "boundary");
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(boundary);
        string alias = Path.Combine(scope.WorkspaceDir, "alias");
        string member = Path.Combine(boundary, "member");
        try
        {
            Directory.CreateSymbolicLink(alias, physical);
            Directory.CreateSymbolicLink(member, Path.Combine(alias, outside ? "outside" : "boundary", "target"));
        }
        catch (Exception e) when (e is UnauthorizedAccessException or PlatformNotSupportedException or IOException)
        { Skip.If(true, "Symbolic links unavailable: " + e.GetType().Name); }

        PathContainment.ResolveCanonical(Path.Combine(member, "missing.md"))
            .Should().Be(Path.Combine(target, "missing.md"));
        PathContainment.IsWithin(boundary, Path.Combine(member, "missing.md")).Should().Be(!outside);
    }

    [Fact]
    public async Task FullList_EqualTimestamps_UsesQualifiedNameForPagination()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;
        var timestamp = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        store.Upsert(new ArtifactEntry("zeta", "uri", 1, 1, timestamp), "local:z");
        store.Upsert(new ArtifactEntry("alpha", "uri", 1, 1, timestamp), "local:a");
        var tools = new ScriniaMcpTools();

        string first = await tools.MemoryList(mode: "full", offset: 0, limit: 1);
        string second = await tools.MemoryList(mode: "full", offset: 1, limit: 1);
        first.Should().Contain("/a/alpha").And.NotContain("/z/zeta");
        second.Should().Contain("/z/zeta").And.NotContain("/a/alpha");
    }
}
