using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class TryUpdateEntryTests
{
    [Fact]
    public void MutateReturningEntry_Persists_AndInvalidatesFactStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("note", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new() { ["mention"] = ["seraphim"] }), "local:findings");

        var before = store.GetEntityFactStore().MentionCount(new EntityRef("mention", "seraphim"));
        before.Should().Be(1);

        bool changed = store.TryUpdateEntry("note", "local:findings", e =>
        {
            var attrs = new Dictionary<string, string[]>(e.Attributes!, StringComparer.OrdinalIgnoreCase)
            {
                ["thing"] = ["seraphim"],
            };
            attrs.Remove("mention");
            return e with { Attributes = attrs };
        });

        changed.Should().BeTrue();
        var reloaded = store.LoadIndex("local:findings").Single(x => x.Name == "note");
        reloaded.Attributes!.Should().ContainKey("thing").And.NotContainKey("mention");
        // fact store rebuilt: mention gone, thing present
        store.GetEntityFactStore().MentionCount(new EntityRef("mention", "seraphim")).Should().Be(0);
        store.GetEntityFactStore().MentionCount(new EntityRef("thing", "seraphim")).Should().Be(1);
    }

    /// <summary>
    /// Regression guard: TryUpdateEntry must not re-date an entry just to force a fact-store
    /// rebuild. The content-aware sidecar token (EntityFactSidecar.ComputeToken +
    /// FileMemoryStore.EntitySignature) is what invalidates the fact store on an attribute-only
    /// change — CreatedAt/UpdatedAt must come out byte-identical to what mutate returned.
    /// </summary>
    [Fact]
    public void MutateReturningEntry_InvalidatesFactStore_WithoutRedatingTheEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        var created = DateTimeOffset.Parse("2023-01-01T00:00:00Z");
        store.Upsert(new ArtifactEntry("note", "uri", 1, 1, created,
            Attributes: new() { ["mention"] = ["seraphim"] }), "local:findings");

        var before = store.LoadIndex("local:findings").Single(x => x.Name == "note");

        bool changed = store.TryUpdateEntry("note", "local:findings", e =>
        {
            var attrs = new Dictionary<string, string[]>(e.Attributes!, StringComparer.OrdinalIgnoreCase)
            {
                ["thing"] = ["seraphim"],
            };
            attrs.Remove("mention");
            return e with { Attributes = attrs };
        });

        changed.Should().BeTrue();

        // (a) fact store reflects the change
        store.GetEntityFactStore().MentionCount(new EntityRef("mention", "seraphim")).Should().Be(0);
        store.GetEntityFactStore().MentionCount(new EntityRef("thing", "seraphim")).Should().Be(1);

        // (b) no fidelity regression: the entry is not re-dated to force that invalidation
        var after = store.LoadIndex("local:findings").Single(x => x.Name == "note");
        after.CreatedAt.Should().Be(before.CreatedAt);
        after.UpdatedAt.Should().Be(before.UpdatedAt);
    }

    [Fact]
    public void MutateReturningNull_WritesNothing()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("note", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new() { ["mention"] = ["seraphim"] }), "local:findings");

        bool changed = store.TryUpdateEntry("note", "local:findings", _ => null);

        changed.Should().BeFalse();
        store.LoadIndex("local:findings").Single(x => x.Name == "note")
            .Attributes!.Should().ContainKey("mention");
    }

    [Fact]
    public void MissingEntry_ReturnsFalse()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.TryUpdateEntry("nope", "local:findings", e => e).Should().BeFalse();
    }
}
