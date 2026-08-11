using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.TestDoubles;
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

    /// <summary>
    /// Regression guard: the entry lookup inside TryUpdateEntry must match names
    /// case-insensitively, the way <c>UpdateMeta</c> matched before it was rerouted through
    /// TryUpdateEntry. A prior fix wave changed the name match from
    /// <c>e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase)</c> to an ordinal
    /// <c>SanitizeName(e.Name) == SanitizeName(subject)</c>, which silently broke update()/link() for
    /// any case-mismatched name — stored as "MixedCase", looked up as "mixedcase".
    /// <para>This test passes on Windows for a second, weaker reason as well, which is worth
    /// knowing when reading it: NTFS folds case by default, so the <c>File.Exists(metaPath)</c>
    /// fast path the method used to open with also reported the entry present here, on an ordinary
    /// (case-insensitive) volume. That gate has since been removed — it was the same defect one line
    /// up. What actually pins its removal is
    /// <see cref="CaseSensitiveFileStoreTests.TryUpdateEntry_ResolvesCaseMismatchedSubject"/>
    /// (tests/Scrinia.Tests/CaseSensitiveFileStoreTests.cs), which runs against a directory carrying
    /// NTFS's per-directory case-sensitivity flag instead of relying on the default.</para>
    /// </summary>
    [Fact]
    public void MutateWithDifferentCasing_StillFindsAndUpdatesTheEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;
        store.Upsert(new ArtifactEntry("MixedCase", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new() { ["mention"] = ["seraphim"] }), "local:findings");

        bool changed = store.TryUpdateEntry("mixedcase", "local:findings", e =>
            e with { Attributes = new(e.Attributes!, StringComparer.OrdinalIgnoreCase) { ["thing"] = ["seraphim"] } });

        changed.Should().BeTrue();
        var reloaded = store.LoadIndex("local:findings").Single(x => x.Name == "MixedCase");
        reloaded.Attributes!.Should().ContainKey("thing");
    }

    /// <summary>
    /// A <see cref="FakeMemoryStore"/> that actually stores things. The shared double is a
    /// deliberate no-op (its Upsert is empty and its LoadIndex returns []), which is right for the
    /// five test files that use it and useless for pinning a lookup. Overriding the two members
    /// this needs is the route its own doc comment prescribes, and it leaves every other consumer
    /// of the shared double untouched.
    /// </summary>
    private sealed class PersistingFakeStore : FakeMemoryStore
    {
        private readonly List<ArtifactEntry> _entries = [];
        public override IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => _entries;
        public override void Upsert(ArtifactEntry entry, string scope = "local")
        {
            _entries.RemoveAll(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
        }
    }

    /// <summary>
    /// The same guard as <see cref="MutateWithDifferentCasing_StillFindsAndUpdatesTheEntry"/>, one
    /// level up: <c>IMemoryStore</c>'s DEFAULT <c>TryUpdateEntry</c> matched sanitized names with an
    /// ordinal <c>==</c> long after FileMemoryStore's override was fixed. No production store
    /// reaches the default — both real stores override it — so this does not guard a live
    /// production bug. It guards the doubles built on the default disagreeing with both real
    /// stores, which is how a test comes to pass for behaviour production does not have.
    /// </summary>
    [Fact]
    public void InterfaceDefault_MutateWithDifferentCasing_StillFindsTheEntry()
    {
        // IMemoryStore-typed on purpose: TryUpdateEntry is a default interface method and is not
        // surfaced through the concrete type (a `var` here is a CS1061 build error, not a phantom).
        IMemoryStore store = new PersistingFakeStore();
        store.Upsert(new ArtifactEntry("MixedCase", "uri", 1, 1, DateTimeOffset.UtcNow), "local");

        bool mutateRan = false;
        bool changed = store.TryUpdateEntry("mixedcase", "local", e =>
        {
            mutateRan = true;
            return e with { ContentPreview = "touched" };
        });

        changed.Should().BeTrue(
            because: "the default must resolve a case-mismatched subject the way both real stores do");
        mutateRan.Should().BeTrue(
            because: "returning true without invoking the mutation would satisfy the assertion above " +
                     "while doing nothing");
        store.LoadIndex("local").Single(e => e.Name == "MixedCase")
            .ContentPreview.Should().Be("touched");
    }
}
