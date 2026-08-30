using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The store's write surface: raw input in, derived entry out.
///
/// <para>These pin what the STORE adds on top of <c>MemoryDerivation</c> — which
/// <c>MemoryDerivationTests</c> already covers in isolation — namely that it derives at all rather
/// than expecting a caller to, that it hands the pipeline the two answers only a store has
/// (the workspace's file hashes and whether the target is ephemeral), that it persists what it
/// derived, and that it resolves an existing memory by name case-insensitively and then carries
/// that memory's own casing forward into every path it builds.</para>
/// </summary>
public class FileMemoryStoreWriteSurfaceTests
{
    private const int MaxPersistentSubjectLength = 182;

    /// <summary>Mirrors EntityTypingEventHandlerTests' TempWorkspace: temp dir, a FileMemoryStore
    /// rooted there, recursive delete on dispose.</summary>
    private sealed class TempWorkspace : IDisposable
    {
        public TempWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "scrinia-writesurface-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Root);
            Store = new FileMemoryStore(Root);
        }

        public string Root { get; }
        public FileMemoryStore Store { get; }

        public void Dispose()
        {
            Store.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }
    }

    private static async Task<string> DecodeAsync(FileMemoryStore store, string subject, string scope = "local")
    {
        string artifact = await store.ReadArtifactAsync(MemoryAddress.Parse("/writesurface/item"));
        int count = Nmp2ChunkedEncoder.GetChunkCount(artifact);
        return string.Concat(Enumerable.Range(1, count).Select(i => Nmp2ChunkedEncoder.DecodeChunk(artifact, i)));
    }

    [Fact]
    public async Task StoreAndAppend_AllowA182CharacterSanitizedPersistentSubject()
    {
        using var tmp = new TempWorkspace();
        string subject = new('x', MaxPersistentSubjectLength);

        await tmp.Store.StoreAsync(new MemoryWrite(subject, "local", ["first"]));
        await tmp.Store.AppendAsync(new MemoryWrite(subject, "local", ["second"]));

        tmp.Store.LoadIndex("local").Should().ContainSingle(e => e.Name == subject);
        (await DecodeAsync(tmp.Store, subject)).Should().Be("firstsecond");
    }

    [Fact]
    public void PersistentMemoryWrite_At183SanitizedCharacters_ThrowsBeforeAStoreCanTouchDisk()
    {
        using var tmp = new TempWorkspace();
        string subject = new('x', MaxPersistentSubjectLength + 1);

        Action construct = () => _ = new MemoryWrite(subject, "local", ["body"]);

        var error = construct.Should().Throw<InvalidMemoryWriteException>().Which;
        error.MaxPersistentSubjectLength.Should().Be(MemoryWrite.MaxPersistentSubjectLength);
        error.SanitizedSubjectLength.Should().Be(MemoryWrite.MaxPersistentSubjectLength + 1);
        Directory.EnumerateFiles(tmp.Root, "*", SearchOption.AllDirectories).Should().BeEmpty(
            because: "construction rejects before a store can create artifact, sidecar, archive, or temporary files");
    }

    [Fact]
    public void PersistentMemoryWrite_UsesTheSanitizedLeafLength()
    {
        string raw = new string('x', 181) + "?";

        Action construct = () => _ = new MemoryWrite(raw, "local", ["body"]);

        construct.Should().NotThrow();
    }

    [Fact]
    public async Task LongEphemeralMemoryWrite_IsExemptAndCreatesNoPersistentFiles()
    {
        using var tmp = new TempWorkspace();
        string name = "~" + new string('x', 1_000);

        await tmp.Store.StoreAsync(new MemoryWrite(name, "ignored", ["body"]));

        tmp.Store.GetEphemeral(name[1..]).Should().NotBeNull();
        tmp.Store.LoadIndex("local").Should().BeEmpty();
    }

    [Fact]
    public void MemoryWrite_ExposesOnlyValidatedTypedRecordShape()
    {
        var write = MemoryWrite.TryCreate(MemoryAddress.Parse("/name"), ["body"],
            new Dictionary<string, IReadOnlyList<string>> { ["tag"] = ["one"] }, ["keyword"]).Write!;

        write.Address.Should().Be(MemoryAddress.Parse("/name"));
        write.Name.Should().Be("/name");
        write.Scope.Should().Be("local");
        write.Content.Should().ContainSingle();
        write.Description.Should().BeNull();
        write.Attributes.Should().ContainKey("tag");
        write.Keywords.Should().ContainSingle();
    }

    // ── Deriving ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task StoreAsync_DerivesMetadataTheCallerDidNotSupply()
    {
        using var tmp = new TempWorkspace();

        var entry = await tmp.Store.StoreAsync(new MemoryWrite(
            Name: "derived", Scope: "local",
            Content: ["the quick brown fox", "and a second chunk"],
            Keywords: ["vulpine"]));

        entry.Keywords.Should().Contain("vulpine");
        entry.TermFrequencies.Should().NotBeNullOrEmpty(
            because: "the store derives what the caller did not send — that is the point of the surface");
        entry.ChunkEntries.Should().HaveCount(2);
        entry.ContentPreview.Should().NotBeNullOrWhiteSpace();
        entry.ChunkCount.Should().Be(2);
    }

    /// <summary>
    /// The caller's keyword list is the AGENT set and nothing else is (D-01). A term the caller named
    /// seeds at +5; a term only the content produced seeds at the auto weight. This is the assertion
    /// that would fail if a caller's already-derived keyword list were ever handed back in as
    /// <see cref="MemoryWrite.Keywords"/>: every auto term would arrive at +5 too.
    /// </summary>
    [Fact]
    public async Task StoreAsync_TreatsTheCallersKeywordsAsAgentSuppliedAndContentTermsAsAuto()
    {
        using var tmp = new TempWorkspace();

        var entry = await tmp.Store.StoreAsync(new MemoryWrite(
            Name: "provenance", Scope: "local",
            Content: ["the quick brown fox"],
            Keywords: ["vulpine"]));

        entry.TermFrequencies!["vulpine"].Should().Be(5,
            because: "a keyword the caller named is agent-supplied");
        entry.TermFrequencies["quick"].Should().BeLessThan(5,
            because: "a term only the content produced is auto-extracted and must not reach the agent weight");
    }

    [Fact]
    public async Task StoreAsync_PersistsSoTheEntryIsReadableAfterwards()
    {
        using var tmp = new TempWorkspace();

        await tmp.Store.StoreAsync(new MemoryWrite("persisted", "local", ["body"]));

        tmp.Store.LoadIndex("local").Should().ContainSingle(e => e.Name == "persisted");
        (await DecodeAsync(tmp.Store, "persisted")).Should().Be("body");
    }

    // ── What only the store can answer ───────────────────────────────────────

    /// <summary>
    /// D-04. The drift baseline is the pipeline's one contact with the filesystem, and the store
    /// supplies it as a resolver. Before this surface, hashing happened only when MCP could see a
    /// local workspace, so a write through the remote store got no baseline at all; the pipeline now
    /// runs inside whichever store owns the workspace.
    /// </summary>
    [Fact]
    public async Task StoreAsync_HashesFileReferencesAgainstItsOwnWorkspace()
    {
        using var tmp = new TempWorkspace();
        string tracked = Path.Combine(tmp.Root, "tracked.cs");
        await File.WriteAllTextAsync(tracked, "the original body");

        var write = new MemoryWrite(
            Name: "watcher", Scope: "local", Content: ["a memory about a file"],
            Attributes: new(StringComparer.OrdinalIgnoreCase)
            {
                ["file"] = ["tracked.cs", "never-existed.cs"],
            });

        var first = await tmp.Store.StoreAsync(write);

        first.FileHashes.Should().ContainKey("tracked.cs",
            because: "the store resolves the path against its workspace root and hashes what it finds");
        first.FileHashes.Should().NotContainKey("never-existed.cs",
            because: "a reference that cannot be hashed is skipped, not a failed write");
        first.Attributes!["file"].Should().Contain("tracked.cs",
            because: "the searchable key must never fall behind the hashes");

        await File.WriteAllTextAsync(tracked, "the body, edited");
        var second = await tmp.Store.StoreAsync(write);

        second.FileHashes!["tracked.cs"].Should().NotBe(first.FileHashes!["tracked.cs"],
            because: "the baseline is the file's CONTENT — a hash that did not move could not detect drift");
    }

    /// <summary>
    /// D-05, and the reason the pipeline needs an <c>isEphemeral</c> flag it cannot work out for
    /// itself: count-shaped content auto-derives a review condition, and an ephemeral memory — which
    /// dies with the process — is exempt. Ephemerality is a store-owned question.
    /// </summary>
    [Fact]
    public async Task StoreAsync_AutoDerivesAReviewConditionForCountsButNotForAnEphemeral()
    {
        using var tmp = new TempWorkspace();
        string[] counting = ["this workspace ships 7 tools today"];

        var persistent = await tmp.Store.StoreAsync(new MemoryWrite("counted", "local", counting));
        var ephemeral = await tmp.Store.StoreAsync(new MemoryWrite("~counted", "local", counting));

        persistent.Attributes!["review"].Should().Contain(MemoryDerivation.CountReviewCondition);
        (ephemeral.Attributes?.ContainsKey("review") ?? false).Should().BeFalse(
            because: "an ephemeral memory does not outlive the process, so it has nothing to review");
    }

    [Fact]
    public async Task StoreAsync_WritesAnEphemeralNameToEphemeralStorageOnly()
    {
        using var tmp = new TempWorkspace();

        var entry = await tmp.Store.StoreAsync(new MemoryWrite("~scratch", "local", ["a passing thought"]));

        entry.Name.Should().Be("scratch", because: "the ~ is addressing, not part of the name");
        tmp.Store.GetEphemeral("scratch").Should().NotBeNull();
        tmp.Store.GetEphemeral("scratch")!.Keywords.Should().BeEquivalentTo(entry.Keywords);
        tmp.Store.LoadIndex("local").Should().BeEmpty(
            because: "an ephemeral write must not create a persistent entry");
        File.Exists(tmp.Store.ArtifactPath("~scratch", "local")).Should().BeFalse(
            because: "an ephemeral write must not create a file named after the ~ address");
    }

    // ── Name resolution ──────────────────────────────────────────────────────

    /// <summary>
    /// The half-fix that shipped twice: matching the existing entry case-insensitively but then
    /// letting the CALLER's casing travel forward into the artifact path, the sidecar and the entry's
    /// own Name. Both halves are asserted here — the resolution (one memory, CreatedAt kept, prior
    /// version archived) and the carry-forward (the memory keeps its own casing).
    /// </summary>
    [Fact]
    public async Task StoreAsync_ResolvesAMismatchedCasingAndKeepsTheExistingMemorysOwn()
    {
        using var tmp = new TempWorkspace();
        var first = await tmp.Store.StoreAsync(new MemoryWrite("MixedCase", "local", ["the original body"]));

        var second = await tmp.Store.StoreAsync(new MemoryWrite("MIXEDCASE", "local", ["the replacement body"]));

        second.Name.Should().Be("MixedCase",
            because: "the memory that already existed keeps its own casing, as every other resolved name does");
        second.CreatedAt.Should().Be(first.CreatedAt, because: "a re-store is not a new memory");
        second.UpdatedAt.Should().NotBeNull();
        tmp.Store.LoadIndex("local").Should().ContainSingle(
            because: "'MIXEDCASE' and 'MixedCase' name one memory, not two");
        tmp.Store.LoadIndex("local")[0].Name.Should().Be("MixedCase",
            because: "the persisted entry is written under the resolved casing too");

        string versions = Path.Combine(tmp.Store.GetStoreDirForScope("local"), "versions");
        Directory.GetFiles(versions, "MixedCase_*.nmp2").Should().ContainSingle(
            because: "a resolved re-store archives the version it is about to overwrite");
    }

    // ── Rollback ─────────────────────────────────────────────────────────────

    /// <summary>
    /// A re-store whose sidecar write fails must leave the memory's PREVIOUS content on disk.
    ///
    /// <para>Leaving the new bytes there is not a benign no-op: the artifact would hold the new
    /// content while the index entry still described the old, and the divergence propagates — the
    /// next <c>AppendAsync</c> reads the stale entry's <c>ChunkEntries</c> but numbers the appended
    /// one from the artifact's real chunk count, producing a non-contiguous set. Deleting instead
    /// (which is what <c>MemoryOrchestrator.StoreAsync</c> used to do, before it was repointed here)
    /// is worse still: nothing reads <c>versions/</c> back, so a transient sidecar failure would
    /// destroy a memory the caller only asked to overwrite, with no recovery path. The server-path
    /// counterpart is
    /// <c>Scrinia.Server.Tests.MemoryOrchestratorWriteSurfaceTests.StoreAsync_FailedReStore_LeavesThePreviousContentReadable</c>.</para>
    ///
    /// <para>The failure is forced by putting a DIRECTORY where the sidecar file belongs, so
    /// <c>WriteSidecar</c>'s atomic temp-then-move cannot land — a rename onto a directory fails on
    /// every filesystem this runs on. The artifact path is untouched, so the write that must be
    /// rolled back still succeeds first. The archive assertion is load-bearing as a PRECONDITION
    /// check: <c>ArchiveVersion</c> runs only when the existing entry actually resolved, so if it
    /// ever stopped resolving this test would fail there rather than passing vacuously.</para>
    /// </summary>
    [Fact]
    public async Task StoreAsync_RestoresThePreviousContentWhenPersistingTheEntryFails()
    {
        using var tmp = new TempWorkspace();
        await tmp.Store.StoreAsync(new MemoryWrite("target", "local", ["the original body"]));

        // Task 2 (write-path cost): a write against a previously-uncached scope now leaves the
        // index cache absent rather than eagerly rebuilding it, so the store above does not warm
        // "local"'s cache. Warm it explicitly so the re-store's own LoadIndex-based identity
        // resolution is a cache hit and finds the entry via the in-memory list rather than a cold
        // Directory.EnumerateFiles scan — which silently skips the sidecar path once it is replaced
        // by a directory below, misresolving this as a brand-new memory instead of an existing one.
        tmp.Store.LoadIndex("local").Should().ContainSingle();

        string storeDir = tmp.Store.GetStoreDirForScope("local");
        string sidecar = Path.Combine(storeDir, "target.meta.json");
        File.Delete(sidecar);
        Directory.CreateDirectory(sidecar);

        var restore = async () => await tmp.Store.StoreAsync(
            new MemoryWrite("target", "local", ["the replacement body"]));

        await restore.Should().ThrowAsync<Exception>(
            because: "a store that could not persist its entry must not report success");

        Directory.GetFiles(Path.Combine(storeDir, "versions"), "target_*.nmp2").Should().ContainSingle(
            because: "the existing entry resolved and its version was archived — without that this " +
                     "test would be asserting the brand-new-memory path instead");
        (await DecodeAsync(tmp.Store, "target")).Should().Be("the original body",
            because: "the failed re-store must leave the memory exactly as it was, not holding new " +
                     "content its index entry does not describe");

        Directory.Delete(sidecar);
    }

    /// <summary>
    /// The append counterpart, and the more destructive half: identity is resolved from the index
    /// BEFORE the artifact is read, so the read succeeds under the memory's own casing. Reading under
    /// the caller's casing threw, fell through to the create-new path, and replaced the memory with
    /// the appended chunk alone.
    /// </summary>
    [Fact]
    public async Task AppendAsync_ResolvesAMismatchedCasingRatherThanReplacingTheMemory()
    {
        using var tmp = new TempWorkspace();
        await tmp.Store.StoreAsync(new MemoryWrite("MixedCase", "local", ["the original body"]));

        var appended = await tmp.Store.AppendAsync(new MemoryWrite("mixedcase", "local", ["the appended body"]));

        appended.Name.Should().Be("MixedCase");
        appended.ChunkCount.Should().Be(2);
        (await DecodeAsync(tmp.Store, "MixedCase"))
            .Should().Be("the original bodythe appended body",
                because: "an append must add to the memory, not replace it");
        tmp.Store.LoadIndex("local").Should().ContainSingle();
    }

    // ── Appending ────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything an append carries over. Each of these was destroyed by at least one of the four
    /// hand-rolled implementations this surface replaces: the curated keywords (D-12), the drift
    /// baseline (D-21), the curated preview (D-20) and the earlier chunks' entries (D-25).
    /// </summary>
    [Fact]
    public async Task AppendAsync_KeepsWhatTheMemoryAlreadyCarried()
    {
        using var tmp = new TempWorkspace();
        await File.WriteAllTextAsync(Path.Combine(tmp.Root, "tracked.cs"), "the tracked body");

        await tmp.Store.StoreAsync(new MemoryWrite(
            Name: "carrier", Scope: "local",
            Content: ["the first chunk", "the second chunk"],
            Description: "a curated description",
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["file"] = ["tracked.cs"] },
            Keywords: ["vulpine"]));

        var appended = await tmp.Store.AppendAsync(new MemoryWrite("carrier", "local", ["the third chunk"]));

        appended.Keywords.Should().Contain("vulpine",
            because: "an append that dropped the memory's curated keywords would destroy them");
        appended.FileHashes.Should().ContainKey("tracked.cs",
            because: "an append must not destroy the memory's drift baseline");
        appended.ContentPreview.Should().Be("a curated description",
            because: "regenerating the preview from content drops a curated description on every append");
        // The appended chunk's entry is contiguous with the ones already there — 1-based, and the
        // earlier two survive rather than being replaced by the new one alone.
        appended.ChunkEntries!.Select(c => c.ChunkIndex).Should().Equal([1, 2, 3]);
        appended.TermFrequencies!["vulpine"].Should().Be(5,
            because: "a carried-forward curated keyword stays an agent term");
    }

    [Fact]
    public async Task AppendAsync_OnAMemoryThatDoesNotExistYetStoresIt()
    {
        using var tmp = new TempWorkspace();

        var entry = await tmp.Store.AppendAsync(new MemoryWrite("brand-new", "local", ["the only body"]));

        entry.ChunkCount.Should().Be(1);
        entry.UpdatedAt.Should().BeNull(because: "nothing was updated — this is a first write");
        tmp.Store.LoadIndex("local").Should().ContainSingle(e => e.Name == "brand-new");
    }

    [Fact]
    public async Task AppendAsync_RefusesMoreThanOneChunk()
    {
        using var tmp = new TempWorkspace();
        await tmp.Store.StoreAsync(new MemoryWrite("target", "local", ["the body"]));

        var refuse = async () => await tmp.Store.AppendAsync(
            new MemoryWrite("target", "local", ["one", "two"]));

        await refuse.Should().ThrowAsync<ArgumentException>(
            because: "an append adds exactly one chunk, and silently merging two would lose the second's entry");
    }

    [Fact]
    public async Task AppendAsync_AppendsToAnEphemeralMemoryInPlace()
    {
        using var tmp = new TempWorkspace();
        await tmp.Store.StoreAsync(new MemoryWrite("~scratch", "local", ["the first thought"]));

        var appended = await tmp.Store.AppendAsync(new MemoryWrite("~scratch", "local", ["the second thought"]));

        appended.ChunkCount.Should().Be(2);
        tmp.Store.GetEphemeral("scratch")!.ChunkCount.Should().Be(2);
        tmp.Store.LoadIndex("local").Should().BeEmpty();
    }

    /// <summary>
    /// An ephemeral memory reports the same keyword provenance whichever read reaches it. The write
    /// returns it, and the listing projection is a SECOND hand-written projection of the same
    /// record — one that already carried <c>Keywords</c> and <c>TermFrequencies</c> but not this,
    /// so the same memory answered "these were curated" through one read and "nobody knows"
    /// (null) through the other.
    /// <para>Nothing misbehaves on that today, but it is armed rather than harmless: the listing IS
    /// what <c>LoadIndex</c> serves for an ephemeral, and a compaction reads its metadata from
    /// exactly there — so the moment compact stops being a no-op on a <c>~name</c>, it would take
    /// the recovery heuristic for a memory whose provenance was recorded and sitting in the map it
    /// just read from.</para>
    /// </summary>
    [Fact]
    public async Task ListScoped_ProjectsAnEphemeralsKeywordProvenance()
    {
        using var tmp = new TempWorkspace();

        var written = await tmp.Store.StoreAsync(
            new MemoryWrite("~scratch", "local", ["a thought about boats"], Keywords: ["portcullis"]));
        written.AgentKeywords.Should().Equal(["portcullis"],
            because: "precondition: the write recorded it");

        var listed = tmp.Store.ListScoped(null).Single(s => s.Scope == "ephemeral").Entry;

        listed.Address.Should().Be(MemoryAddress.Parse("~scratch"));
        listed.ArtifactKey.Should().Be("artifacts/ephemeral/scratch.nmp2");
        listed.AgentKeywords.Should().Equal(written.AgentKeywords,
            because: "two projections of one record must not disagree about it");
        listed.Keywords.Should().Equal(written.Keywords,
            because: "the neighbouring field this one was omitted next to");
    }
}
