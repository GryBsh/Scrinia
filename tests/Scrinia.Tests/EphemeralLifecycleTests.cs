using FluentAssertions;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Resilience;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// <see cref="EphemeralEntry"/> had no <c>FileHashes</c> member, so a <c>file</c> reference on an
/// ephemeral memory was derived by the pipeline and then discarded — the entry looked clean while
/// carrying no drift baseline at all. These tests pin that the baseline now survives the write, is
/// consistent across every projection of the same memory, and — for a remote client — is hashed
/// against the caller's real workspace rather than a throwaway temp directory.
/// </summary>
public sealed class EphemeralLifecycleTests : IDisposable
{
    private readonly string _workspace;
    private readonly FileMemoryStore _store;

    public EphemeralLifecycleTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "scrinia-eph-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(_workspace, "src"));
        File.WriteAllText(Path.Combine(_workspace, "src", "thing.cs"), "// hello");
        _store = new FileMemoryStore(_workspace);
        MemoryStoreContext.Current = _store;
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_workspace, recursive: true); } catch { }
    }

    [Fact]
    public async Task EphemeralWrite_RecordsADriftBaselineForItsFileReferences()
    {
        // A BARE path, not `file:src/thing.cs`. Prose `file:` is NOT a supported way to produce a
        // `file` attribute, and worse than unsupported: FilePathPattern's (?<!["'`=:]) lookbehind
        // does not suppress the match, it shifts the start one character — so `file:src/thing.cs`
        // silently yields the corrupt path `rc/thing.cs`. The supported forms are a bare path in
        // content (this one), `file:path` in the `keywords` argument, an explicit `attributes` map,
        // or a line-start `file: path` WITH whitespace after the colon.
        var written = await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));

        written.FileHashes.Should().ContainKey("src/thing.cs");

        _store.GetEphemeral("scratch")!.FileHashes.Should().ContainKey("src/thing.cs",
            because: "the stored entry and the returned entry are two views of one memory");
    }

    [Fact]
    public async Task AppendToEphemeral_PreservesTheOriginalBaselineEvenIfTheFileChangesLater()
    {
        // Step 1a: the ExistingMetadata an append carries forward used to read only
        // `existingEntry?.FileHashes` (an ArtifactEntry field) with no `?? existingEph?.FileHashes`
        // fallback — correct only while EphemeralEntry had no FileHashes member of its own. The
        // moment it gained one, this line would make `skip` null for every ephemeral append, so
        // MemoryDerivation.ForAppend's HashFileRefs would treat "src/thing.cs" as never-baselined
        // and re-hash it against whatever the file currently contains — silently moving the
        // baseline to drifted content and destroying the exact signal D-21 exists to provide.
        var written = await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));
        string originalHash = written.FileHashes!["src/thing.cs"];

        // Mutate the file AFTER the baseline was recorded. A correct append must not notice.
        File.WriteAllText(Path.Combine(_workspace, "src", "thing.cs"), "// changed after baseline");

        var appended = await _store.AppendAsync(
            new MemoryWrite("~scratch", "local", ["no new file reference in this chunk"]));

        appended.FileHashes.Should().ContainKey("src/thing.cs").WhoseValue.Should().Be(originalHash,
            because: "an append must carry the ORIGINAL baseline forward, not silently re-hash the " +
                "file to its current (possibly drifted) content");
        _store.GetEphemeral("scratch")!.FileHashes.Should().ContainKey("src/thing.cs")
            .WhoseValue.Should().Be(originalHash);
    }

    [Fact]
    public async Task ListEphemeral_ProjectsFileHashesLikeTheWriteResponseDoes()
    {
        // Three projections of one ephemeral memory exist (PersistEphemeral's stored entry, its
        // returned entry, and ListEphemeral's). A field present in some and null in others means
        // the answer depends on which read you came through.
        await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));

        var listed = _store.ListEphemeral().Single().Entry;
        // ContainKey first: BeEquivalentTo alone passes over two nulls, so it would stay green
        // with the whole feature (or just this projection) absent.
        listed.FileHashes.Should().ContainKey("src/thing.cs");
        listed.FileHashes.Should().BeEquivalentTo(_store.GetEphemeral("scratch")!.FileHashes);
    }

    /// <summary>
    /// The projection pair those three sites now share is a genuine round trip:
    /// <c>e.WithMetadataFrom(e.ToArtifactEntry())</c> is <c>e</c>, including the artifact bytes
    /// <see cref="ArtifactEntry"/> cannot carry.
    ///
    /// <para>That last part is why <c>WithMetadataFrom</c> writes over the existing record instead
    /// of constructing one. A rebuild would have no <c>Artifact</c> to put back, so a metadata-only
    /// update to an ephemeral memory (which is what <c>link</c> on a <c>~name</c> is) would report
    /// success and leave the memory empty. Asserted on a memory with every optional field populated
    /// — a round trip over an all-defaults record is one that cannot fail.</para>
    /// </summary>
    [Fact]
    public async Task EphemeralEntry_ProjectionPair_RoundTrips()
    {
        await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"],
            Keywords: ["curated-term"], Description: "a curated preview"));
        // Appended so the record carries a second chunk (ChunkEntries) and an UpdatedAt as well.
        await _store.AppendAsync(new MemoryWrite("~scratch", "local", ["a second chunk"]));
        var original = _store.GetEphemeral("scratch")!;

        // Every field the pair has to carry is actually populated here, or the round trip is
        // vacuous for it.
        original.Artifact.Should().NotBeNullOrEmpty();
        original.Keywords.Should().NotBeNullOrEmpty();
        original.TermFrequencies.Should().NotBeNullOrEmpty();
        original.AgentKeywords.Should().NotBeNullOrEmpty();
        original.Attributes.Should().NotBeNullOrEmpty();
        original.FileHashes.Should().NotBeNullOrEmpty();
        original.ChunkEntries.Should().NotBeNullOrEmpty();

        original.WithMetadataFrom(original.ToArtifactEntry()).Should().BeEquivalentTo(original);
    }

    /// <summary>
    /// The compare-and-swap in <see cref="FileMemoryStore.TryUpdateEphemeral"/> re-applies its
    /// mutation to the NEWER entry when a concurrent writer lands between the read and the swap,
    /// instead of overwriting it.
    ///
    /// <para>Driven deterministically rather than with threads: the callback itself performs the
    /// interloping write on its first invocation, which is exactly the interleaving that matters and
    /// the only one a test can pin without a race. Under the unsynchronized
    /// <c>GetEphemeral</c> → mutate → <c>RememberEphemeral</c> shape this member replaced, the
    /// interloper's attribute is simply gone — and in the case that motivated this, the value lost is
    /// a <c>ref</c>, which makes a lost update manufacture the dangling one-sided ref that
    /// <c>link</c>'s precheck exists to prevent.</para>
    /// </summary>
    [Fact]
    public async Task TryUpdateEphemeral_ReAppliesToTheNewerEntry_WhenAConcurrentWriterWinsTheSwap()
    {
        await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["body text"]));
        // The encoded NMP/2 artifact, captured to compare against — a metadata swap must leave the
        // memory's content byte-identical.
        string originalArtifact = _store.GetEphemeral("scratch")!.Artifact;

        int attempts = 0;
        bool swapped = _store.TryUpdateEphemeral("scratch", current =>
        {
            attempts++;
            if (attempts == 1)
            {
                // Lands after this callback read `current`, before it can swap.
                var interloped = _store.GetEphemeral("scratch")! with
                {
                    Attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                    {
                        ["ref"] = ["~written-by-the-other-writer"],
                    },
                };
                _store.RememberEphemeral("scratch", interloped);
            }

            var merged = current.Attributes is null
                ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string[]>(current.Attributes, StringComparer.OrdinalIgnoreCase);
            merged["person"] = ["maria"];
            return current with { Attributes = merged };
        });

        swapped.Should().BeTrue();
        attempts.Should().Be(2,
            because: "the first swap must be REJECTED and the mutation re-run against what the " +
                     "other writer stored — one attempt would mean the CAS compared nothing");

        var final = _store.GetEphemeral("scratch")!;
        final.Attributes!.Should().ContainKey("person");
        final.Attributes.Should().ContainKey("ref",
            because: "the concurrent writer's value must survive — dropping it is the lost update " +
                     "this member exists to prevent");
        final.Artifact.Should().Be(originalArtifact, "the swap must not disturb the memory's content");
    }

    /// <summary>
    /// A swap invalidates the merged entity-fact cache, matching every other mutating write path on
    /// this store (<c>SaveIndex</c>, <c>Remove</c>, <c>TryUpdateEntry</c>). An ephemeral memory's
    /// attributes ARE part of that view — <c>GetEntityFactStore</c> builds over
    /// <c>ListScoped(null)</c>, which includes the ephemeral scope — so without the tail an
    /// already-built fact store stays blind to the write for the rest of the session.
    ///
    /// <para>The first <c>GetEntityFactStore()</c> call is what makes this test able to fail: it
    /// populates the cache, so a missing invalidation is observable rather than hidden behind a
    /// lazy first build.</para>
    /// </summary>
    [Fact]
    public async Task TryUpdateEphemeral_InvalidatesTheMergedFactStore()
    {
        await _store.StoreAsync(new MemoryWrite("~scratch", "local", ["body text"]));

        var maria = new Scrinia.Core.Search.EntityRef("person", "maria");
        _store.GetEntityFactStore().MentionCount(maria).Should().Be(0);

        _store.TryUpdateEphemeral("scratch", current => current with
        {
            Attributes = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["person"] = ["maria"],
            },
        });

        _store.GetEntityFactStore().MentionCount(maria).Should().Be(1,
            because: "a fact store built before the swap must not survive it");
    }

    [Fact]
    public async Task HttpMemoryStore_HashesEphemeralFileRefsAgainstTheSuppliedWorkspaceRoot()
    {
        // HttpMemoryStore derives its own ephemeral writes in-process via a FileMemoryStore helper
        // (WriteEphemeralInProcessAsync). Before Step 3, that helper was always rooted at a throwaway
        // temp directory, so a `file` reference resolved to nothing no matter what the caller passed
        // — the write would succeed with a baseline recording the file as unhashable. Passing the
        // real workspace root is what makes the hash correct rather than merely present.
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:5000") };
        using var remote = new HttpMemoryStore(http, "default",
            retryOptions: new RetryOptions(MaxRetries: 0), workspaceRoot: _workspace);

        var written = await remote.StoreAsync(new MemoryWrite("~scratch", "local", ["see src/thing.cs"]));

        string expectedHash = Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                await File.ReadAllBytesAsync(Path.Combine(_workspace, "src", "thing.cs"))));

        written.FileHashes.Should().ContainKey("src/thing.cs")
            .WhoseValue.Should().Be(expectedHash);
    }

    [Fact]
    public async Task DriftList_NamesAnEphemeralWithItsTildePrefix()
    {
        // BuildDriftList skips entries with no FileHashes, so before this branch gave ephemerals a
        // baseline its naming line was dead for them. Now they reach it — and it calls
        // FormatQualifiedName, which only special-cases "local:", so scope "ephemeral" came back as
        // the BARE name. A drift row naming "scratch" is unaddressable: recall('scratch') either
        // misses or returns an unrelated persistent memory of that name.
        var tools = new ScriniaMcpTools();

        await tools.Remember(["see src/thing.cs"], "~scratch");
        _store.GetEphemeral("scratch")!.FileHashes.Should().ContainKey("src/thing.cs",
            because: "precondition: without a baseline the drift scan skips this entry and the " +
                     "assertion below would be vacuous");

        // Drift the file so the entry produces a row at all — a clean reference prints nothing.
        await File.WriteAllTextAsync(Path.Combine(_workspace, "src", "thing.cs"), "// changed");

        string result = await tools.MemoryList(mode: "drift");

        result.Should().Contain("~scratch → src/thing.cs [DRIFT]",
            because: "a drift row must name the memory the way recall() takes it — the sibling " +
                     "renderer in BuildFullList already prefixes the tilde");
    }

    [Fact]
    public async Task Compact_OnAMultiChunkEphemeral_MergesItsChunks()
    {
        // Compact never checked IsEphemeral and went straight to ReadArtifactAsync, which is
        // disk-only — so it reported "Memory '~scratch' not found." for a memory list() will show,
        // and suggested running list() to find it.
        var tools = new ScriniaMcpTools();

        await tools.Remember(["the first chunk"], "~scratch");
        await tools.Append(" the second chunk", "~scratch");
        await tools.Append(" the third chunk", "~scratch");

        _store.GetEphemeral("scratch")!.ChunkCount.Should().Be(3,
            because: "precondition: three writes, three chunks");

        string result = await tools.MemoryCompact("~scratch");

        ResponseParser.Parse(result).Status.Should().Be("success");
        _store.GetEphemeral("scratch")!.ChunkCount.Should().Be(1);
    }

    [Fact]
    public async Task Compact_OnAnEphemeral_PreservesItsCuratedMetadata()
    {
        // Compaction rewrites content, not metadata. The persistent path proves this already;
        // the ephemeral path must not be a second implementation with different behaviour.
        //
        // "vulpine" is kept OUT of the chunk text on purpose: if it appeared in the content,
        // MemoryDerivation.CuratedKeywords' recovery heuristic would reconstruct it from the text
        // itself, and this test would pass even with the ephemeral metadata-carrying branch
        // removed entirely — a vacuity trap the task brief calls out explicitly.
        var tools = new ScriniaMcpTools();

        await tools.Remember(["a fox jumps"], "~scratch", description: "curated", keywords: ["vulpine"]);
        await tools.Append(" over a lazy dog", "~scratch");
        await tools.Append(" and keeps running", "~scratch");

        string result = await tools.MemoryCompact("~scratch");

        // Not just a metadata check: a failed compact() leaves the pre-existing entry (already
        // carrying "curated"/"vulpine" from the Remember above) untouched, which would satisfy the
        // assertions below vacuously. Pinning success and the chunk merge first is what makes this
        // test actually exercise the ephemeral compaction path rather than pass on a no-op.
        ResponseParser.Parse(result).Status.Should().Be("success");

        var compacted = _store.GetEphemeral("scratch")!;
        compacted.ChunkCount.Should().Be(1, because: "compaction must actually have run");
        compacted.ContentPreview.Should().Be("curated");
        compacted.Keywords.Should().Contain("vulpine");
        compacted.AgentKeywords.Should().Contain("vulpine",
            because: "only the agent's own keywords travel back into a rewrite — D-29");
    }
}
