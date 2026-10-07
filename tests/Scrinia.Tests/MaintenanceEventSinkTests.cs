using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Scrinia.TestDoubles;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="MaintenanceEventSink"/> — auto-linking and orphan detection.
/// </summary>
[Collection(GlobalWorkspaceConfigCollection.Name)]
public sealed class MaintenanceEventSinkTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaMcpTools _tools;
    private readonly MaintenanceEventSink _sink;

    public MaintenanceEventSinkTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaMcpTools();
        _sink = new MaintenanceEventSink();
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task AutoLink_AddsRefFieldOnTarget()
    {
        // Arrange — create two topic-scoped entries
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Source content placeholder."], MemoryAddress.Parse("/test/source"));
        await _tools.Remember(["Target content placeholder."], MemoryAddress.Parse("/test/target"));

        // Act — fire OnStoredAsync with content that references /test/target
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/source"),
            ["This analysis references [[/test/target]] in its findings."],
            store,
            CancellationToken.None);

        // Assert — target entry should now have Fields["ref"] containing /test/source
        var (targetScope, targetSubject) = store.ParseQualifiedName("/test/target");
        var targetEntries = store.LoadIndex(targetScope);
        var targetEntry = targetEntries.First(e =>
            e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

        targetEntry.Attributes.Should().NotBeNull();
        targetEntry.Attributes!["ref"].Should().Contain("/test/source",
            "auto-linking should add the source to the ref FIELD on the referenced target entry");

        // Closes a pre-existing LOCAL inconsistency, not just the remote one: whether an attribute
        // change reached the BM25 index used to depend on which caller made it. UpdateMeta computed
        // a boost for itself; this sink and EntityTypingEventHandler changed attributes and computed
        // nothing, so a ref-link was invisible to search. The rule lives in
        // MemoryDerivation.ForMetadataUpdate now and TryUpdateEntry applies it for every route.
        targetEntry.TermFrequencies.Should().NotBeNull()
            .And.ContainKey("source",
                "the ref attribute's value tokens must reach the index — the store re-derives them " +
                "on any mutation that changes attributes, whichever caller made it");
    }

    /// <summary>Task 6 write-path consequence: <see cref="ReferenceExtractor.ExtractMemoryRefs"/>
    /// now skips fenced code blocks, and this sink calls it on every store/append (see
    /// <see cref="MaintenanceEventSink"/>). A <c>[[wiki-link]]</c> pasted inside a fenced block —
    /// e.g. a code sample that happens to mention a memory name — no longer auto-links its target.
    /// <para>The auto-link write (<c>AutoLinkAndDetectOrphans</c> step 2) lands on the MENTIONED
    /// target's own <c>ref</c> attribute, not on the mentioner's — and orphan detection (step 3)
    /// runs, in the same call, against whichever entry is currently being stored/appended, checking
    /// whether ITS name shows up in anyone else's <c>ref</c> list. So the entry whose orphan status
    /// this write can flip is the one whose content did the mentioning (fencesource here), not the
    /// one that got mentioned (fencetarget) — a single <c>OnStoredAsync</c> call for fencesource
    /// exercises both the auto-link write and its own orphan check together.</para></summary>
    [Fact]
    public async Task AutoLink_IgnoresLinksInsideAFencedBlock()
    {
        // Arrange — create two topic-scoped entries
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Source content placeholder."], MemoryAddress.Parse("/test/fencesource"));
        await _tools.Remember(["Target content placeholder."], MemoryAddress.Parse("/test/fencetarget"));

        // Act — fire OnStoredAsync for the SOURCE, whose content mentions fencetarget only inside
        // a fenced block. Under the pre-fence-aware extractor this single call would both (a) write
        // ref=[fencesource] onto fencetarget, and (b) have its own orphan check (running right
        // after, against fencesource) see that fresh write and clear fencesource's orphan flag —
        // proven by reverting ReferenceExtractor and confirming this test fails (see task-6-report.md).
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/fencesource"),
            ["Prose has no link here.\n```\nSee [[/test/fencetarget]] in this pasted snippet.\n```\n"],
            store,
            CancellationToken.None);

        // Assert — fencetarget gets no ref field: the fenced link never reached the extractor, so
        // the auto-link write never happened.
        var (targetScope, targetSubject) = store.ParseQualifiedName("/test/fencetarget");
        var targetEntry = store.LoadIndex(targetScope).First(e =>
            e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

        (targetEntry.Attributes?.ContainsKey("ref") ?? false).Should().BeFalse(
            "a wiki-link inside a fenced block must not auto-link its target — that is the fix, " +
            "not a regression");

        // Assert — fencesource, whose only mention of another memory lived inside the fence, stays
        // orphan: with the write suppressed, its own orphan check finds nothing pointing back at it.
        var (sourceScope, sourceSubject) = store.ParseQualifiedName("/test/fencesource");
        var sourceEntry = store.LoadIndex(sourceScope).First(e =>
            e.Name.Equals(sourceSubject, StringComparison.OrdinalIgnoreCase));

        sourceEntry.Attributes.Should().NotBeNull().And.ContainKey("orphan",
            "fencesource's mention of fencetarget lived only inside a fence and was never " +
            "extracted, so the auto-link write that would otherwise clear its own orphan flag " +
            "never happened — it stays orphan");
    }

    /// <summary>The orphan marker is an <c>orphan</c> ATTRIBUTE, not a keyword — see
    /// <see cref="MaintenanceEventSink"/> for why the keyword form made this sink a no-op against
    /// a remote store while succeeding locally.</summary>
    [Fact]
    public async Task OrphanDetection_AddsOrphanAttribute()
    {
        // Arrange — create an entry with no inbound refs from any other entry
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Isolated content with no references."], MemoryAddress.Parse("/test/lonely"));

        // Act — fire OnStoredAsync (content doesn't reference anything)
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/lonely"),
            ["Isolated content with no references."],
            store,
            CancellationToken.None);

        // Assert — the entry should be flagged as orphan
        var (scope, subject) = store.ParseQualifiedName("/test/lonely");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Attributes.Should().NotBeNull()
            .And.ContainKey("orphan",
                "an entry with no inbound references should be flagged as orphan");
        entry.Keywords.Should().NotContain("orphan",
            "the marker moved off the keyword list entirely — writing it there is what PATCH " +
            "refuses, so a sink that still did it would work locally and silently do nothing " +
            "against a remote store");
    }

    [Fact]
    public async Task OrphanDetection_NoOrphanWhenHasInboundRefs()
    {
        // Arrange — create entry A that has a ref:/test/linked keyword (simulating an inbound ref)
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Content of linked entry."], MemoryAddress.Parse("/test/linked"));

        // Manually add ref:/test/linked keyword on some other entry to simulate an inbound reference
        await _tools.Remember(["Some referencing content."], MemoryAddress.Parse("/test/referrer"), keywords: ["ref:/test/linked"]);

        // Act — fire OnStoredAsync for the linked entry
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/linked"),
            ["Content of linked entry."],
            store,
            CancellationToken.None);

        // Assert — entry should NOT have orphan keyword
        var (scope, subject) = store.ParseQualifiedName("/test/linked");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "an entry with inbound references should not be flagged as orphan");
    }

    [Fact]
    public async Task AppendNewRefs_CreatesLinks()
    {
        // Arrange — create two entries
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Alpha content."], MemoryAddress.Parse("/test/alpha"));
        await _tools.Remember(["Beta content."], MemoryAddress.Parse("/test/beta"));

        // Act — fire OnAppendedAsync with content mentioning /test/beta
        await _sink.OnAppendedAsync(
            MemoryAddress.Parse("/test/alpha"),
            "Appended note: see [[/test/beta]] for related work.",
            store,
            CancellationToken.None);

        // Assert — beta should have Fields["ref"] containing /test/alpha
        var (betaScope, betaSubject) = store.ParseQualifiedName("/test/beta");
        var betaEntries = store.LoadIndex(betaScope);
        var betaEntry = betaEntries.First(e =>
            e.Name.Equals(betaSubject, StringComparison.OrdinalIgnoreCase));

        betaEntry.Attributes.Should().NotBeNull();
        betaEntry.Attributes!["ref"].Should().Contain("/test/alpha",
            "OnAppendedAsync should create ref FIELD links for newly referenced memories");
    }

    [Fact]
    public async Task AutoLink_SkipsSelfReferences()
    {
        // Arrange — create an entry
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Self-referencing content."], MemoryAddress.Parse("/test/selfref"));

        // Act — fire OnStoredAsync with content that references itself
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/selfref"),
            ["This entry references [[/test/selfref]] in its own content."],
            store,
            CancellationToken.None);

        // Assert — entry should NOT have a ref:/test/selfref keyword or a self ref field value
        var (scope, subject) = store.ParseQualifiedName("/test/selfref");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        var refKeywords = entry.Keywords?
            .Where(k => k.StartsWith("ref:/test/selfref", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];

        refKeywords.Should().BeEmpty(
            "self-references should be skipped — an entry should not link to itself");

        var selfRefFieldValues = entry.Attributes is not null && entry.Attributes.TryGetValue("ref", out var refs)
            ? refs.Where(v => v.Equals("/test/selfref", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

        selfRefFieldValues.Should().BeEmpty(
            "self-references should be skipped — an entry should not add itself to its own ref field");
    }

    [Theory]
    [InlineData("/temp/probe", "~probe")]
    [InlineData("~probe", "/temp/probe")]
    public async Task AutoLink_SkipsCanonicalEphemeralSelfReferences(string sourceName, string referenceName)
    {
        var store = MemoryStoreContext.Current!;
        var address = MemoryAddress.Parse(sourceName);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse(address.ToString().StartsWith('/') || address.ToString().StartsWith('~') ? address.ToString() : "/" + address.ToString()), "local", ["placeholder"], null, new(), []));
        await _sink.OnStoredAsync(
            address,
            [$"This entry references [[{referenceName}]] in its own content."],
            store,
            CancellationToken.None);

        var entry = store.GetEphemeral(address.Value)?.ToArtifactEntry();
        entry.Should().NotBeNull();
        entry!.Attributes?.TryGetValue("ref", out var refs).Should().NotBe(true);
        entry.Keywords?.Any(keyword =>
            keyword.Equals($"ref:{address}", StringComparison.OrdinalIgnoreCase)).Should().NotBe(true);
    }

    [Fact]
    public async Task AutoLink_Idempotent_DoesNotDuplicate()
    {
        // Arrange — create two entries
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Source content."], MemoryAddress.Parse("/test/src"));
        await _tools.Remember(["Destination content."], MemoryAddress.Parse("/test/dst"));

        string[] content = ["This references [[/test/dst]] in the analysis."];

        // Act — fire OnStoredAsync twice
        await _sink.OnStoredAsync(MemoryAddress.Parse("/test/src"), content, store, CancellationToken.None);
        await _sink.OnStoredAsync(MemoryAddress.Parse("/test/src"), content, store, CancellationToken.None);

        // Assert — target should have "/test/src" in Fields["ref"] exactly once
        var (dstScope, dstSubject) = store.ParseQualifiedName("/test/dst");
        var dstEntries = store.LoadIndex(dstScope);
        var dstEntry = dstEntries.First(e =>
            e.Name.Equals(dstSubject, StringComparison.OrdinalIgnoreCase));

        var refCount = dstEntry.Attributes is not null && dstEntry.Attributes.TryGetValue("ref", out var refs)
            ? refs.Count(v => v.Equals("/test/src", StringComparison.OrdinalIgnoreCase))
            : 0;

        refCount.Should().Be(1,
            "auto-linking should be idempotent — running twice should not duplicate ref field values");
    }

    [Fact]
    public async Task OnForgotten_DoesNotThrow()
    {
        // Act & Assert — OnForgottenAsync should complete without error
        var store = MemoryStoreContext.Current!;
        var act = async () => await _sink.OnForgottenAsync(MemoryAddress.Parse("/test/gone"), true, store, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "OnForgottenAsync is a no-op and should never throw");
    }

    [Fact]
    public async Task OrphanRemoval_WhenInboundRefAdded()
    {
        // Arrange — create an orphan entry
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Orphan content."], MemoryAddress.Parse("/test/wasorphan"));

        // Fire sink to mark it as orphan
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/wasorphan"),
            ["Orphan content."],
            store,
            CancellationToken.None);

        // Verify it's orphan
        var (scope, subject) = store.ParseQualifiedName("/test/wasorphan");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        entry.Attributes.Should().NotBeNull().And.ContainKey("orphan",
            "entry should initially be flagged as orphan");

        // Act — create a referrer and fire sink for the orphan again
        await _tools.Remember(["References [[/test/wasorphan]] here."], MemoryAddress.Parse("/test/referrer2"), keywords: ["ref:/test/wasorphan"]);

        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/wasorphan"),
            ["Orphan content."],
            store,
            CancellationToken.None);

        // Assert — orphan keyword should be removed
        entries = store.LoadIndex(scope);
        entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "the orphan attribute should be removed when inbound references exist");
    }

    [Fact]
    public async Task AutoLink_WritesRefField_NotKeyword()
    {
        // Arrange — create two topic-scoped entries
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Source content placeholder."], MemoryAddress.Parse("/test/fieldsource"));
        await _tools.Remember(["Target content placeholder."], MemoryAddress.Parse("/test/fieldtarget"));

        // Act — fire OnStoredAsync with content that references /test/fieldtarget
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/fieldsource"),
            ["This analysis references [[/test/fieldtarget]] in its findings."],
            store,
            CancellationToken.None);

        // Assert — target entry should have Fields["ref"] containing the source,
        // and should NOT have a legacy ref: keyword.
        var (targetScope, targetSubject) = store.ParseQualifiedName("/test/fieldtarget");
        var targetEntries = store.LoadIndex(targetScope);
        var targetEntry = targetEntries.First(e =>
            e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

        targetEntry.Attributes.Should().NotBeNull();
        targetEntry.Attributes!["ref"].Should().Contain(v => v.Contains("/test/fieldsource"),
            "auto-linking should write the ref FIELD now");
        (targetEntry.Keywords ?? []).Should().NotContain(k => k.StartsWith("ref:"),
            "prefixed ref: keywords are no longer written");
    }

    [Fact]
    public async Task OrphanDetection_NoOrphanWhenInboundRefIsFieldOnly()
    {
        // Arrange — create an entry that will be referenced, and a referrer whose
        // Fields["ref"] (not a legacy ref: keyword) points at it, simulating the new write shape.
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Content of field-linked entry."], MemoryAddress.Parse("/test/fieldlinked"));
        await _tools.Remember(["Some referencing content."], MemoryAddress.Parse("/test/fieldreferrer"));

        var (referrerScope, referrerSubject) = store.ParseQualifiedName("/test/fieldreferrer");
        var referrerEntry = store.LoadIndex(referrerScope)
            .First(e => e.Name.Equals(referrerSubject, StringComparison.OrdinalIgnoreCase));

        // The sink compares Fields["ref"] values against the source's FormatQualifiedName
        // output, which renders v1 "topic:subject" names in path form ("/topic/subject").
        var (linkedScope, linkedSubject) = store.ParseQualifiedName("/test/fieldlinked");
        string linkedQualified = store.FormatQualifiedName(linkedScope, linkedSubject);

        var referrerFields = KnownAttributes.Merge(referrerEntry.Attributes, new() { ["ref"] = [linkedQualified] });
        store.Upsert(referrerEntry with { Attributes = referrerFields }, referrerScope);

        // Act — fire OnStoredAsync for the linked entry
        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/fieldlinked"),
            ["Content of field-linked entry."],
            store,
            CancellationToken.None);

        // Assert — entry should NOT have orphan keyword; the inbound ref lives only in Fields.
        var (scope, subject) = store.ParseQualifiedName("/test/fieldlinked");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "an entry with an inbound ref FIELD (no legacy keyword) should not be flagged as orphan");
    }

    /// <summary>
    /// Task 5: orphan detection now answers "does anything reference me?" via
    /// <see cref="IMemoryStore.AnyScoped"/> instead of materializing <c>ListScoped(null)</c> and
    /// calling <c>.Any</c> on the result. <c>ListScoped(null)</c> is a CROSS-SCOPE listing, and
    /// every other test in this file references the target from within the very same topic scope
    /// ("/test/..."), so a bug that narrowed <c>AnyScoped</c>'s scan to a single scope (e.g. only
    /// the referrer's own scope, or only "local") would still pass them. Referrer and target live
    /// in different topic scopes here to make that failure mode observable.
    /// </summary>
    [Fact]
    public async Task OrphanDetection_FindsInboundRefFromADifferentScope()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Content in one topic scope."], MemoryAddress.Parse("/crossscopetarget/entry"));
        await _tools.Remember(
            ["References [[/crossscopetarget/entry]] from a different topic."],
            MemoryAddress.Parse("/crossscopereferrer/entry"),
            keywords: ["ref:/crossscopetarget/entry"]);

        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/crossscopetarget/entry"),
            ["Content in one topic scope."],
            store,
            CancellationToken.None);

        var (scope, subject) = store.ParseQualifiedName("/crossscopetarget/entry");
        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "the inbound ref lives in a different topic scope than the target — AnyScoped(null, ...) " +
            "must still find it, the same way the materialized ListScoped(null).Any(...) it replaced did");
    }

    /// <summary>The other direction of the same scope-spanning check: with the cross-scope referrer
    /// removed, an entry with genuinely no inbound refs anywhere in the store is still marked orphan
    /// — proving the lazy scan does not stop early and under-report.</summary>
    [Fact]
    public async Task OrphanDetection_MarksOrphan_WhenNoOtherScopeReferencesIt()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Nothing points at this, anywhere."], MemoryAddress.Parse("/crossscopelonely/entry"));
        await _tools.Remember(["Unrelated content, no reference."], MemoryAddress.Parse("/othertopic/unrelated"));

        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/crossscopelonely/entry"),
            ["Nothing points at this, anywhere."],
            store,
            CancellationToken.None);

        var (scope, subject) = store.ParseQualifiedName("/crossscopelonely/entry");
        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Attributes.Should().NotBeNull().And.ContainKey("orphan",
            "no entry in any scope references this one, so it must still be marked orphan");
    }

    /// <summary>
    /// Pins that <c>OnStoredAsync</c> actually widens through <see cref="CoreSinkStore.Require"/>
    /// rather than a bare <c>(IMemoryStore)store</c> cast that would also compile and also throw.
    /// <c>OnStoredAsync</c> swallows the exception in its own <c>try</c>/<c>catch</c> and logs it to
    /// <see cref="Console.Error"/> — see <see cref="MaintenanceEventSink.OnStoredAsync"/> — so the
    /// throw itself is not observable here; the failure is observed through the logged message
    /// instead. That message must carry <c>Require</c>'s own text naming the handler
    /// ("MaintenanceEventSink is a Core-internal event handler..."), not just the catch block's own
    /// hardcoded "MaintenanceEventSink error:" prefix, which a raw-cast mutation would still print
    /// (with an <see cref="InvalidCastException"/> in place of Require's message) and would
    /// therefore NOT distinguish the mutation.
    /// </summary>
    [Fact]
    public async Task OnStoredAsync_LogsNamingTheHandler_WhenStoreIsNotAFullMemoryStore()
    {
        var sink = new MaintenanceEventSink();
        var narrowStore = new NarrowPluginStore();

        var captured = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(captured);
            await sink.OnStoredAsync(MemoryAddress.Parse("/test/narrow"), ["content"], narrowStore, CancellationToken.None);
        }
        finally
        {
            Console.SetError(previous);
        }

        captured.ToString().Should().Contain(
            "MaintenanceEventSink is a Core-internal event handler",
            "this is CoreSinkStore.Require's own message text, naming the handler -- a raw-cast " +
            "mutation would still print the catch block's generic 'MaintenanceEventSink error:' " +
            "prefix but never this phrase, since an InvalidCastException carries no such text");
    }

    // ── The legacy keyword carrier ─────────────────────────────────────────────

    /// <summary>
    /// Entries written before the marker moved to an attribute carry the bare <c>orphan</c>
    /// KEYWORD. Clearing it was removed when the marker moved, on the reasoning that it was inert
    /// stale noise. It is not inert: <c>WeightedAttributeScorer.ScoreEntryTerm</c> matches attribute
    /// VALUES and never keys, so a bare "orphan" query scores the keyword form (40.00 measured)
    /// ABOVE a genuine attribute-era orphan (27.27) — an entry with demonstrable inbound refs
    /// outranking the real orphans. The clearing is restored: marking still keys off the attribute
    /// alone, so a legacy orphan migrates onto the new carrier; clearing keys off either.
    /// </summary>
    [Fact]
    public async Task LegacyOrphanKeyword_IsCleared_WhenTheEntryGainsAnInboundRef()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Content of the once-orphaned entry."], MemoryAddress.Parse("/test/legacyorphan"));
        await _tools.Remember(["References [[/test/legacyorphan]] here."], MemoryAddress.Parse("/test/legacyreferrer"),
            keywords: ["ref:/test/legacyorphan"]);

        // Plant the pre-change shape: the marker as a bare keyword, with no orphan attribute.
        var (scope, subject) = store.ParseQualifiedName("/test/legacyorphan");
        store.TryUpdateEntry(subject, scope, e => e with
        {
            Keywords = [.. e.Keywords ?? [], "orphan"],
        }).Should().BeTrue();

        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/legacyorphan"), ["Content of the once-orphaned entry."], store, CancellationToken.None);

        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().NotContain("orphan",
            "the entry has an inbound ref, so the stale legacy marker must go — leaving it lets a " +
            "demonstrably non-orphan entry outrank the real orphans on an \"orphan\" query");
        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "and it must not gain the attribute form of a marker it no longer qualifies for");
    }

    /// <summary>
    /// A dual-marked entry — the normal outcome of migration, since marking adds the attribute and
    /// leaves the keyword — must come out of a clear with BOTH carriers gone. Driven end to end
    /// through a real store here; that it happens in a SINGLE mutation is pinned separately by
    /// <see cref="ClearingBothCarriers_IsASinglePersistedWrite"/>, which counts the writes.
    /// <para>This used to be named for taking "both writes", and paired with a deleted server test
    /// covering the remote case where the keyword write was refused and the attribute clear had to
    /// land anyway. There is one write and one store now.</para>
    /// </summary>
    [Fact]
    public async Task DualMarkedEntry_HasBothCarriersCleared()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Content of the dual-marked entry."], MemoryAddress.Parse("/test/dualmarked"));
        await _tools.Remember(["References [[/test/dualmarked]] here."], MemoryAddress.Parse("/test/dualreferrer"),
            keywords: ["ref:/test/dualmarked"]);

        var (scope, subject) = store.ParseQualifiedName("/test/dualmarked");
        store.TryUpdateEntry(subject, scope, e => e with
        {
            Attributes = KnownAttributes.Merge(e.Attributes, new() { ["orphan"] = ["true"] }),
            Keywords = [.. e.Keywords ?? [], "orphan"],
        }).Should().BeTrue();

        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/dualmarked"), ["Content of the dual-marked entry."], store, CancellationToken.None);

        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "the entry has an inbound ref, so the attribute marker must go");
        entry.Keywords.Should().NotContain("orphan",
            "and the legacy carrier goes with it, in the same mutation");
    }

    /// <summary>The other half, so the clearing above cannot be implemented as "always strip the
    /// keyword": a legacy entry that is STILL an orphan keeps its marker and gains the attribute,
    /// migrating onto the new carrier rather than being silently unmarked.</summary>
    [Fact]
    public async Task LegacyOrphanKeyword_MigratesToTheAttribute_WhenTheEntryIsStillAnOrphan()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Isolated content, nothing points at it."], MemoryAddress.Parse("/test/legacystillorphan"));

        var (scope, subject) = store.ParseQualifiedName("/test/legacystillorphan");
        store.TryUpdateEntry(subject, scope, e => e with
        {
            Keywords = [.. e.Keywords ?? [], "orphan"],
        }).Should().BeTrue();

        await _sink.OnStoredAsync(
            MemoryAddress.Parse("/test/legacystillorphan"), ["Isolated content, nothing points at it."], store, CancellationToken.None);

        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Attributes.Should().NotBeNull().And.ContainKey("orphan",
            "marking keys off the ATTRIBUTE, so a legacy entry that is genuinely still an orphan " +
            "migrates onto the carrier both backends can write");
    }

    /// <summary>
    /// <c>KnownAttributes.Merge</c> caps at <see cref="KnownAttributes.MaxAttributes"/> and
    /// truncates past it with no signal, so an entry already at the cap lost the orphan marker
    /// while <c>TryUpdateEntry</c> still returned <c>true</c> — the sink's own "did it persist?"
    /// check read success. It declines the write and says so instead.
    /// </summary>
    [Fact]
    public async Task OrphanMarker_AtTheAttributeKeyCap_IsReportedRatherThanSilentlyDropped()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Isolated content at the attribute cap."], MemoryAddress.Parse("/test/cappedorphan"));

        var (scope, subject) = store.ParseQualifiedName("/test/cappedorphan");
        var full = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < KnownAttributes.MaxAttributes; i++) full[$"k{i}"] = ["v"];
        store.TryUpdateEntry(subject, scope, e => e with { Attributes = full }).Should().BeTrue();

        var captured = new StringWriter();
        var previous = Console.Error;
        try
        {
            Console.SetError(captured);
            await _sink.OnStoredAsync(
                MemoryAddress.Parse("/test/cappedorphan"), ["Isolated content at the attribute cap."], store, CancellationToken.None);
        }
        finally
        {
            Console.SetError(previous);
        }

        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        entry.Attributes.Should().NotBeNull();
        entry.Attributes!.ContainsKey("orphan").Should().BeFalse(
            "the cap genuinely prevents it — this test proves nothing if the marker fits");

        captured.ToString().Should().Contain("orphan",
            "the marker could not be applied, and the whole point of checking the result is that " +
            "such a case is reported rather than counted as a success");
        captured.ToString().Should().Contain(KnownAttributes.MaxAttributes.ToString(
            System.Globalization.CultureInfo.InvariantCulture),
            "and the report must name the cap, or it sends the reader looking for a store fault");
    }

    /// <summary>
    /// Clearing an orphan marker is ONE persisted mutation, not two.
    ///
    /// <para>It used to be two, and the reason was entirely remote: the metadata endpoint refused a
    /// body that changed <c>Keywords</c>, so bundling the keyword removal into the attribute's
    /// mutation made the server refuse BOTH and leave a non-orphan marked <c>orphan:true</c>
    /// forever. Splitting them guaranteed the attribute cleared even when the keyword could not.
    /// With one store there is one sidecar, and the entry is already in hand inside the callback,
    /// so both carriers come off together.</para>
    ///
    /// <para>This is the common path, not a corner: marking deliberately leaves a legacy entry's
    /// keyword alone, so dual-marked entries are the normal outcome of migration.</para>
    ///
    /// <para>Counted through <c>Upsert</c> rather than <c>TryUpdateEntry</c> on purpose.
    /// <c>TryUpdateEntry</c>'s interface default is what a <see cref="FakeMemoryStore"/> subclass
    /// inherits, and the interface mapping is fixed at <see cref="FakeMemoryStore"/> — a derived
    /// re-declaration would not intercept a call made through <c>IMemoryStore</c>, so a counter
    /// there would read zero and pass for the wrong reason. <c>Upsert</c> is virtual, is what the
    /// default calls once per mutation that returns a value, and is therefore an honest count of
    /// the writes that actually landed.</para>
    /// </summary>
    [Fact]
    public async Task ClearingBothCarriers_IsASinglePersistedWrite()
    {
        var store = new CountingStore();
        store.Upsert(new ArtifactEntry("target", "uri", 1, 1, DateTimeOffset.UtcNow,
            Keywords: ["orphan", "notes"],
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["orphan"] = ["true"],
            }));
        store.Upsert(new ArtifactEntry("referrer", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["ref"] = ["local:target"],
            }));
        store.Writes.Clear();

        await _sink.OnStoredAsync(MemoryAddress.Parse("/target"), ["Content with no outbound references."], store,
            CancellationToken.None);

        var entry = store.LoadIndex().Single(e => e.Name == "target");

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            because: "the entry has an inbound ref, so the attribute marker must go");
        entry.Keywords.Should().NotContain("orphan",
            because: "and the legacy carrier must go with it — leaving it lets a demonstrably " +
                     "non-orphan entry outrank the real orphans on a bare \"orphan\" query");
        entry.Keywords.Should().Contain("notes",
            because: "only the marker is removed; the entry's own keywords are not collateral");

        store.Writes.GetValueOrDefault("target").Should().Be(1,
            because: "both carriers live in one sidecar, so clearing them is one mutation. The " +
                     "second write existed because a remote PATCH refused Keywords changes, and " +
                     "that store is gone");
    }

    /// <summary>
    /// The counterpart, so the single write above cannot be implemented as "always strip both":
    /// an entry that is STILL an orphan gains the attribute and KEEPS its legacy keyword, which is
    /// what makes the dual-marked state the normal migration outcome in the first place.
    /// </summary>
    [Fact]
    public async Task MarkingAnOrphan_LeavesTheLegacyKeyword_AndIsAlsoOneWrite()
    {
        var store = new CountingStore();
        store.Upsert(new ArtifactEntry("lonely", "uri", 1, 1, DateTimeOffset.UtcNow,
            Keywords: ["orphan"]));
        store.Writes.Clear();

        await _sink.OnStoredAsync(MemoryAddress.Parse("/lonely"), ["Nothing points at this."], store,
            CancellationToken.None);

        var entry = store.LoadIndex().Single(e => e.Name == "lonely");

        entry.Attributes.Should().ContainKey("orphan",
            because: "nothing references it, so it migrates onto the attribute carrier");
        entry.Keywords.Should().Contain("orphan",
            because: "marking does not strip the legacy keyword — the entry still qualifies for " +
                     "the marker, and dropping one carrier while adding another would make it " +
                     "briefly unfindable by the spelling every pre-migration entry uses");
        store.Writes.GetValueOrDefault("lonely").Should().Be(1);
    }

    /// <summary>
    /// A <see cref="FakeMemoryStore"/> that persists and counts what it persisted. Overrides the
    /// three members the orphan path reaches — <c>LoadIndex</c> for the entry, <c>ListScoped</c>
    /// for the whole-store inbound-ref question that <c>AnyScoped</c>'s default reads through, and
    /// <c>Upsert</c> for the write itself — and leaves the rest of the shared double alone.
    /// </summary>
    private sealed class CountingStore : FakeMemoryStore
    {
        private readonly List<ArtifactEntry> _entries = [];

        public Dictionary<string, int> Writes { get; } = new(StringComparer.OrdinalIgnoreCase);

        public override IReadOnlyList<ArtifactEntry> LoadIndex(string scope = "local") => _entries;

        public override List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null) =>
            [.. _entries.Select(e => new ScopedArtifact("local", e))];

        public override void Upsert(ArtifactEntry entry, string scope = "local")
        {
            Writes[entry.Name] = Writes.GetValueOrDefault(entry.Name) + 1;
            _entries.RemoveAll(e => e.Name.Equals(entry.Name, StringComparison.OrdinalIgnoreCase));
            _entries.Add(entry);
        }
    }
}
