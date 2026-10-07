using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class LegacyLexicalCandidateTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonpositiveLimitIsRejectedBeforeFilesystemAdmission(int limit)
    {
        using var workspace = new TestHelpers.StoreScope(createStoreDir: false);
        Directory.CreateDirectory(workspace.WorkspaceDir);
        File.WriteAllText(Path.Combine(workspace.WorkspaceDir, ".scrinia"), "blocked");
        using var store = new FileMemoryStore(workspace.WorkspaceDir);

        Action select = () => store.SelectLexicalCandidates(["local:notes"], null, "alpha", limit);

        select.Should().Throw<ArgumentOutOfRangeException>().WithParameterName(nameof(limit));
    }

    [Fact]
    public void LegacySelectionKeepsTheLexicalTopKAndHonorsExcludedTopics()
    {
        using var workspace = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(workspace.WorkspaceDir);
        foreach (string name in new[] { "z-last", "b-alpha", "a-alpha", "c-alpha", "y-last" })
            store.Upsert(new ArtifactEntry(name, "", 1, 1, DateTimeOffset.UtcNow), "local:notes");
        store.Upsert(new ArtifactEntry("alpha-alpha", "", 1, 1, DateTimeOffset.UtcNow), "local:excluded");

        store.SelectLexicalCandidates(["all"], ["excluded"], "alpha", 2)
            .Select(candidate => (candidate.Scope, candidate.Entry.Name))
            .Should().Equal(("local:notes", "a-alpha"), ("local:notes", "b-alpha"));
    }

    [Fact]
    public async Task EphemeralSelectionDoesNotTouchTheFilesystem()
    {
        using var workspace = new TestHelpers.StoreScope(createStoreDir: false);
        Directory.CreateDirectory(workspace.WorkspaceDir);
        using var store = new FileMemoryStore(workspace.WorkspaceDir);
        var address = MemoryAddress.Parse("~alpha");
        await store.WriteAsync(address, MemoryWrite.Create(address, ["body"], new Dictionary<string, IReadOnlyList<string>>(), []));
        File.WriteAllText(Path.Combine(workspace.WorkspaceDir, ".scrinia"), "blocked");

        store.SelectLexicalCandidates(["ephemeral"], null, "alpha", 1)
            .Should().ContainSingle().Which.Scope.Should().Be("ephemeral");
    }
}
