using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="MaintenanceEventSink"/> — auto-linking and orphan detection.
/// </summary>
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
        await _tools.Remember(["Source content placeholder."], "/test/source");
        await _tools.Remember(["Target content placeholder."], "/test/target");

        // Act — fire OnStoredAsync with content that references /test/target
        await _sink.OnStoredAsync(
            "/test/source",
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
        await _tools.Remember(["Source content placeholder."], "/test/fencesource");
        await _tools.Remember(["Target content placeholder."], "/test/fencetarget");

        // Act — fire OnStoredAsync for the SOURCE, whose content mentions fencetarget only inside
        // a fenced block. Under the pre-fence-aware extractor this single call would both (a) write
        // ref=[fencesource] onto fencetarget, and (b) have its own orphan check (running right
        // after, against fencesource) see that fresh write and clear fencesource's orphan flag —
        // proven by reverting ReferenceExtractor and confirming this test fails (see task-6-report.md).
        await _sink.OnStoredAsync(
            "/test/fencesource",
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
        await _tools.Remember(["Isolated content with no references."], "/test/lonely");

        // Act — fire OnStoredAsync (content doesn't reference anything)
        await _sink.OnStoredAsync(
            "/test/lonely",
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
        await _tools.Remember(["Content of linked entry."], "/test/linked");

        // Manually add ref:/test/linked keyword on some other entry to simulate an inbound reference
        await _tools.Remember(["Some referencing content."], "/test/referrer", keywords: ["ref:/test/linked"]);

        // Act — fire OnStoredAsync for the linked entry
        await _sink.OnStoredAsync(
            "/test/linked",
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
        await _tools.Remember(["Alpha content."], "/test/alpha");
        await _tools.Remember(["Beta content."], "/test/beta");

        // Act — fire OnAppendedAsync with content mentioning /test/beta
        await _sink.OnAppendedAsync(
            "/test/alpha",
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
        await _tools.Remember(["Self-referencing content."], "/test/selfref");

        // Act — fire OnStoredAsync with content that references itself
        await _sink.OnStoredAsync(
            "/test/selfref",
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

    [Fact]
    public async Task AutoLink_Idempotent_DoesNotDuplicate()
    {
        // Arrange — create two entries
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Source content."], "/test/src");
        await _tools.Remember(["Destination content."], "/test/dst");

        string[] content = ["This references [[/test/dst]] in the analysis."];

        // Act — fire OnStoredAsync twice
        await _sink.OnStoredAsync("/test/src", content, store, CancellationToken.None);
        await _sink.OnStoredAsync("/test/src", content, store, CancellationToken.None);

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
        var act = async () => await _sink.OnForgottenAsync("/test/gone", true, store, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "OnForgottenAsync is a no-op and should never throw");
    }

    [Fact]
    public async Task OrphanRemoval_WhenInboundRefAdded()
    {
        // Arrange — create an orphan entry
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Orphan content."], "/test/wasorphan");

        // Fire sink to mark it as orphan
        await _sink.OnStoredAsync(
            "/test/wasorphan",
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
        await _tools.Remember(["References [[/test/wasorphan]] here."], "/test/referrer2", keywords: ["ref:/test/wasorphan"]);

        await _sink.OnStoredAsync(
            "/test/wasorphan",
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
        await _tools.Remember(["Source content placeholder."], "/test/fieldsource");
        await _tools.Remember(["Target content placeholder."], "/test/fieldtarget");

        // Act — fire OnStoredAsync with content that references /test/fieldtarget
        await _sink.OnStoredAsync(
            "/test/fieldsource",
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
        await _tools.Remember(["Content of field-linked entry."], "/test/fieldlinked");
        await _tools.Remember(["Some referencing content."], "/test/fieldreferrer");

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
            "/test/fieldlinked",
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
        await _tools.Remember(["Content of the once-orphaned entry."], "/test/legacyorphan");
        await _tools.Remember(["References [[/test/legacyorphan]] here."], "/test/legacyreferrer",
            keywords: ["ref:/test/legacyorphan"]);

        // Plant the pre-change shape: the marker as a bare keyword, with no orphan attribute.
        var (scope, subject) = store.ParseQualifiedName("/test/legacyorphan");
        store.TryUpdateEntry(subject, scope, e => e with
        {
            Keywords = [.. e.Keywords ?? [], "orphan"],
        }).Should().BeTrue();

        await _sink.OnStoredAsync(
            "/test/legacyorphan", ["Content of the once-orphaned entry."], store, CancellationToken.None);

        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().NotContain("orphan",
            "the entry has an inbound ref, so the stale legacy marker must go — leaving it lets a " +
            "demonstrably non-orphan entry outrank the real orphans on an \"orphan\" query");
        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "and it must not gain the attribute form of a marker it no longer qualifies for");
    }

    /// <summary>
    /// The local half of the two-write split. A dual-marked entry — the normal outcome of migration,
    /// since marking adds the attribute and leaves the keyword — must come out of a clear with
    /// BOTH carriers gone when the store can take both writes. The remote half, where the keyword
    /// write is refused and the attribute clear must land anyway, is pinned in
    /// <c>Scrinia.Server.Tests.RemoteLoadIndexTests.MaintenanceSink_ClearsTheOrphanAttributeRemotely_…</c>.
    /// </summary>
    [Fact]
    public async Task DualMarkedEntry_HasBothCarriersCleared_WhenTheStoreTakesBothWrites()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Content of the dual-marked entry."], "/test/dualmarked");
        await _tools.Remember(["References [[/test/dualmarked]] here."], "/test/dualreferrer",
            keywords: ["ref:/test/dualmarked"]);

        var (scope, subject) = store.ParseQualifiedName("/test/dualmarked");
        store.TryUpdateEntry(subject, scope, e => e with
        {
            Attributes = KnownAttributes.Merge(e.Attributes, new() { ["orphan"] = ["true"] }),
            Keywords = [.. e.Keywords ?? [], "orphan"],
        }).Should().BeTrue();

        await _sink.OnStoredAsync(
            "/test/dualmarked", ["Content of the dual-marked entry."], store, CancellationToken.None);

        var entry = store.LoadIndex(scope).First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        (entry.Attributes?.ContainsKey("orphan") ?? false).Should().BeFalse(
            "the attribute clear is the first write and must always land");
        entry.Keywords.Should().NotContain("orphan",
            "and the second write cleans up the legacy carrier — locally both succeed, so splitting " +
            "them costs nothing here while being the only thing that works remotely");
    }

    /// <summary>The other half, so the clearing above cannot be implemented as "always strip the
    /// keyword": a legacy entry that is STILL an orphan keeps its marker and gains the attribute,
    /// migrating onto the new carrier rather than being silently unmarked.</summary>
    [Fact]
    public async Task LegacyOrphanKeyword_MigratesToTheAttribute_WhenTheEntryIsStillAnOrphan()
    {
        var store = MemoryStoreContext.Current!;
        await _tools.Remember(["Isolated content, nothing points at it."], "/test/legacystillorphan");

        var (scope, subject) = store.ParseQualifiedName("/test/legacystillorphan");
        store.TryUpdateEntry(subject, scope, e => e with
        {
            Keywords = [.. e.Keywords ?? [], "orphan"],
        }).Should().BeTrue();

        await _sink.OnStoredAsync(
            "/test/legacystillorphan", ["Isolated content, nothing points at it."], store, CancellationToken.None);

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
        await _tools.Remember(["Isolated content at the attribute cap."], "/test/cappedorphan");

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
                "/test/cappedorphan", ["Isolated content at the attribute cap."], store, CancellationToken.None);
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
}
