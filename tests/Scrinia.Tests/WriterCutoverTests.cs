using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// P6.3 writer cutover: proves <c>AttributeMigration.Forward</c> runs inside the persistent
/// MCP write sites (Store/UpdateMeta/Append in MemoryTools.Core.cs) AT WRITE TIME, not merely
/// picked up later by <c>FileMemoryStore</c>'s read-time forward-fill
/// (<c>FileMemoryStore.LoadIndexUnsafe</c>, which calls <c>AttributeMigration.MigratesTo</c>
/// on every cold load and writes the upgrade back).
///
/// Write/read-time isolation: these tests read the persisted entry back via
/// <see cref="ScriniaArtifactStore.LoadIndex"/> (src/Scrinia/Mcp/ScriniaArtifactStore.cs) — a
/// plain "*.meta.json" sidecar deserialize with NO call to AttributeMigration anywhere in its
/// path, and no in-memory cache. This is a completely separate code path from
/// <c>FileMemoryStore.LoadIndex</c> (which IS where the read-time forward-fill lives) even
/// though both ultimately point at the same directory on disk during these tests
/// (TestHelpers.StoreScope overrides both). The tools under test persist through
/// <c>store.Upsert(...)</c> — <c>FileMemoryStore.Upsert</c> — which just serializes the given
/// entry straight to its sidecar; it does not call Forward either. So the only way
/// <c>Fields</c> can already contain "tag"/"review"/"anchor"/"file" keys when read back
/// through <c>ScriniaArtifactStore.LoadIndex()</c> is if the tool itself (Store/UpdateMeta/
/// Append) called Forward on the entry before persisting it — which is exactly what this
/// task adds. Before that change, these assertions fail because Fields lacks those
/// param-derived keys (only keyword/ref/attribute-derived keys are present at write time).
/// </summary>
public sealed class WriterCutoverTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task Store_Persistent_FoldsToAttributesAtWriteTime()
    {
        using var scope = new TestHelpers.StoreScope();
        string relPath = "src/Probe.cs";
        string fullPath = Path.Combine(scope.WorkspaceDir, relPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(fullPath, "class Probe {}");

        await Tools().Store(
            ["Widget subsystem depends on the probe."],
            "writer-cutover-store",
            tags: ["widgets"],
            reviewAfter: "2026-08-01",
            anchors: ["how does the widget subsystem work"],
            codeRefs: [relPath]);

        // Raw sidecar read — bypasses FileMemoryStore's read-time Forward entirely (see class doc).
        var persisted = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "writer-cutover-store");

        // Fields populated AT WRITE.
        persisted.Attributes.Should().NotBeNull();
        persisted.Attributes!["tag"].Should().Contain("widgets");
        persisted.Attributes!["review"].Should().NotBeEmpty();
        persisted.Attributes!["anchor"].Should().Contain("how does the widget subsystem work");
        persisted.Attributes!["file"].Should().Contain(relPath);

        AttributeReaders.TryReadReview(persisted.Attributes, out var reviewDate, out _).Should().BeTrue();
        reviewDate.Should().NotBeNull();
        persisted.FileHashes.Should().ContainKey(relPath);
    }

    [Fact]
    public async Task UpdateMeta_Persistent_FoldsToAttributesAtWriteTime()
    {
        using var scope = new TestHelpers.StoreScope();
        // Base entry deliberately carries no review data — anything under "review" below
        // can only have come from THIS UpdateMeta call's own Forward pass, not carried
        // over from Store's.
        await Tools().Store(["base content"], "writer-cutover-update");

        await Tools().UpdateMeta(
            "writer-cutover-update",
            reviewAfter: "2026-09-01",
            reviewWhen: "when the widget API changes");

        var persisted = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "writer-cutover-update");

        persisted.Attributes.Should().NotBeNull();
        persisted.Attributes!["review"].Should().NotBeEmpty();

        AttributeReaders.TryReadReview(persisted.Attributes, out var reviewDate, out var condition).Should().BeTrue();
        reviewDate.Should().NotBeNull();
        condition.Should().Be("when the widget API changes");
    }

    [Fact]
    public async Task Append_Persistent_FoldsToAttributesAtWriteTime()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(
            ["Initial content about the auth flow."],
            "writer-cutover-append",
            tags: ["auth"],
            anchors: ["how does login work"]);

        await Tools().Append(
            "Additional detail about token refresh.",
            "writer-cutover-append",
            tags: ["auth", "tokens"],
            anchors: ["how does login work", "what triggers a token refresh"]);

        var persisted = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "writer-cutover-append");

        persisted.Attributes.Should().NotBeNull();
        persisted.Attributes!["tag"].Should().Contain("auth").And.Contain("tokens");
        persisted.Attributes!["anchor"].Should().Contain("how does login work")
            .And.Contain("what triggers a token refresh");
    }

    [Fact]
    public async Task Store_Ephemeral_FoldsToAttributesAtWriteTime()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(
            ["Ephemeral scratch note about the widget subsystem."],
            "~writer-cutover-eph-store",
            tags: ["widgets"],
            anchors: ["how does the widget subsystem work"]);

        // EphemeralEntry is in-memory-only and never forward-filled at read time — assert
        // directly against the store, not through a ListEphemeral projection (that projection
        // still drops Fields and is fixed in Task 4).
        var entry = MemoryStoreContext.Current!.GetEphemeral("writer-cutover-eph-store");
        entry.Should().NotBeNull();

        entry!.Attributes.Should().NotBeNull();
        entry.Attributes!["tag"].Should().Contain("widgets");
        entry.Attributes!["anchor"].Should().Contain("how does the widget subsystem work");
    }

    [Fact]
    public async Task Append_Ephemeral_FoldsToAttributesAtWriteTime()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(
            ["Initial ephemeral note about the auth flow."],
            "~writer-cutover-eph-append",
            tags: ["auth"],
            anchors: ["how does login work"]);

        await Tools().Append(
            "Additional detail about token refresh.",
            "~writer-cutover-eph-append",
            tags: ["auth", "tokens"],
            anchors: ["how does login work", "what triggers a token refresh"]);

        var entry = MemoryStoreContext.Current!.GetEphemeral("writer-cutover-eph-append");
        entry.Should().NotBeNull();

        entry!.Attributes.Should().NotBeNull();
        entry.Attributes!["tag"].Should().Contain("auth").And.Contain("tokens");
        entry.Attributes!["anchor"].Should().Contain("how does login work")
            .And.Contain("what triggers a token refresh");
    }

    /// <summary>
    /// Task 4 (P6.3 follow-up): <c>EphemeralEntry</c> never passes through the read-time
    /// forward-fill (it's in-memory only), so the <c>Fields</c> populated at write time by
    /// Task 2 are only observable through search/display if the <c>ListEphemeral</c>
    /// projection (<c>EphemeralEntry</c> → <c>ArtifactEntry</c>) actually carries them.
    /// Pre-Task-4 this projection drops <c>Fields</c> entirely — this test exercises the real
    /// production projection (<see cref="FileMemoryStore.ListEphemeral"/>, reached here via
    /// <see cref="FileMemoryStore.ListScoped(string?)"/>, the same path the MCP list/search/
    /// summary tools use) and fails until the projection carries <c>Fields</c> through.
    /// </summary>
    [Fact]
    public async Task ListEphemeral_Projection_CarriesFieldsFromEphemeralEntry()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(
            ["Ephemeral scratch note about the widget subsystem."],
            "~writer-cutover-eph-list",
            tags: ["widgets"],
            anchors: ["how does the widget subsystem work"]);

        var projected = MemoryStoreContext.Current!.ListScoped(null)
            .Single(sa => sa.Scope == "ephemeral" && sa.Entry.Name == "writer-cutover-eph-list")
            .Entry;

        projected.Attributes.Should().NotBeNull();
        projected.Attributes!["tag"].Should().Contain("widgets");
        projected.Attributes!["anchor"].Should().Contain("how does the widget subsystem work");
    }

    /// <summary>
    /// Task 4: <c>CopyMemory</c>'s explicit-ctor ephemeral→persistent promotion path
    /// (<see cref="FileMemoryStore.CopyMemory"/>) must also carry <c>Fields</c> from the
    /// source <c>EphemeralEntry</c> onto the newly-constructed persistent <c>ArtifactEntry</c>
    /// — otherwise attributes written at Task 2 would be silently lost on promotion.
    /// </summary>
    [Fact]
    public async Task CopyMemory_EphemeralToPersistent_CarriesFields()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(
            ["Ephemeral scratch note about the widget subsystem."],
            "~writer-cutover-eph-copy",
            tags: ["widgets"],
            anchors: ["how does the widget subsystem work"]);

        await Tools().Copy("~writer-cutover-eph-copy", "writer-cutover-copy-dest");

        var persisted = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "writer-cutover-copy-dest");

        persisted.Attributes.Should().NotBeNull();
        persisted.Attributes!["tag"].Should().Contain("widgets");
        persisted.Attributes!["anchor"].Should().Contain("how does the widget subsystem work");
    }
}
