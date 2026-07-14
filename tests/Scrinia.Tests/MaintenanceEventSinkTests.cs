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
        await _tools.Store(["Source content placeholder."], "test:source");
        await _tools.Store(["Target content placeholder."], "test:target");

        // Act — fire OnStoredAsync with content that references test:target
        await _sink.OnStoredAsync(
            "test:source",
            ["This analysis references test:target in its findings."],
            store,
            CancellationToken.None);

        // Assert — target entry should now have Fields["ref"] containing test:source
        var (targetScope, targetSubject) = store.ParseQualifiedName("test:target");
        var targetEntries = store.LoadIndex(targetScope);
        var targetEntry = targetEntries.First(e =>
            e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

        targetEntry.Attributes.Should().NotBeNull();
        targetEntry.Attributes!["ref"].Should().Contain("test:source",
            "auto-linking should add the source to the ref FIELD on the referenced target entry");
    }

    [Fact]
    public async Task OrphanDetection_AddsOrphanKeyword()
    {
        // Arrange — create an entry with no inbound refs from any other entry
        var store = MemoryStoreContext.Current!;
        await _tools.Store(["Isolated content with no references."], "test:lonely");

        // Act — fire OnStoredAsync (content doesn't reference anything)
        await _sink.OnStoredAsync(
            "test:lonely",
            ["Isolated content with no references."],
            store,
            CancellationToken.None);

        // Assert — the entry should be flagged as orphan
        var (scope, subject) = store.ParseQualifiedName("test:lonely");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().Contain("orphan",
            "an entry with no inbound references should be flagged as orphan");
    }

    [Fact]
    public async Task OrphanDetection_NoOrphanWhenHasInboundRefs()
    {
        // Arrange — create entry A that has a ref:test:linked keyword (simulating an inbound ref)
        var store = MemoryStoreContext.Current!;
        await _tools.Store(["Content of linked entry."], "test:linked");

        // Manually add ref:test:linked keyword on some other entry to simulate an inbound reference
        await _tools.Store(["Some referencing content."], "test:referrer", keywords: ["ref:/test/linked"]);

        // Act — fire OnStoredAsync for the linked entry
        await _sink.OnStoredAsync(
            "test:linked",
            ["Content of linked entry."],
            store,
            CancellationToken.None);

        // Assert — entry should NOT have orphan keyword
        var (scope, subject) = store.ParseQualifiedName("test:linked");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().NotContain("orphan",
            "an entry with inbound references should not be flagged as orphan");
    }

    [Fact]
    public async Task AppendNewRefs_CreatesLinks()
    {
        // Arrange — create two entries
        var store = MemoryStoreContext.Current!;
        await _tools.Store(["Alpha content."], "test:alpha");
        await _tools.Store(["Beta content."], "test:beta");

        // Act — fire OnAppendedAsync with content mentioning test:beta
        await _sink.OnAppendedAsync(
            "test:alpha",
            "Appended note: see test:beta for related work.",
            store,
            CancellationToken.None);

        // Assert — beta should have Fields["ref"] containing test:alpha
        var (betaScope, betaSubject) = store.ParseQualifiedName("test:beta");
        var betaEntries = store.LoadIndex(betaScope);
        var betaEntry = betaEntries.First(e =>
            e.Name.Equals(betaSubject, StringComparison.OrdinalIgnoreCase));

        betaEntry.Attributes.Should().NotBeNull();
        betaEntry.Attributes!["ref"].Should().Contain("test:alpha",
            "OnAppendedAsync should create ref FIELD links for newly referenced memories");
    }

    [Fact]
    public async Task AutoLink_SkipsSelfReferences()
    {
        // Arrange — create an entry
        var store = MemoryStoreContext.Current!;
        await _tools.Store(["Self-referencing content."], "test:selfref");

        // Act — fire OnStoredAsync with content that references itself
        await _sink.OnStoredAsync(
            "test:selfref",
            ["This entry references test:selfref in its own content."],
            store,
            CancellationToken.None);

        // Assert — entry should NOT have a ref:test:selfref keyword or a self ref field value
        var (scope, subject) = store.ParseQualifiedName("test:selfref");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        var refKeywords = entry.Keywords?
            .Where(k => k.StartsWith("ref:test:selfref", StringComparison.OrdinalIgnoreCase))
            .ToArray() ?? [];

        refKeywords.Should().BeEmpty(
            "self-references should be skipped — an entry should not link to itself");

        var selfRefFieldValues = entry.Attributes is not null && entry.Attributes.TryGetValue("ref", out var refs)
            ? refs.Where(v => v.Equals("test:selfref", StringComparison.OrdinalIgnoreCase)).ToArray()
            : [];

        selfRefFieldValues.Should().BeEmpty(
            "self-references should be skipped — an entry should not add itself to its own ref field");
    }

    [Fact]
    public async Task AutoLink_Idempotent_DoesNotDuplicate()
    {
        // Arrange — create two entries
        var store = MemoryStoreContext.Current!;
        await _tools.Store(["Source content."], "test:src");
        await _tools.Store(["Destination content."], "test:dst");

        string[] content = ["This references test:dst in the analysis."];

        // Act — fire OnStoredAsync twice
        await _sink.OnStoredAsync("test:src", content, store, CancellationToken.None);
        await _sink.OnStoredAsync("test:src", content, store, CancellationToken.None);

        // Assert — target should have "test:src" in Fields["ref"] exactly once
        var (dstScope, dstSubject) = store.ParseQualifiedName("test:dst");
        var dstEntries = store.LoadIndex(dstScope);
        var dstEntry = dstEntries.First(e =>
            e.Name.Equals(dstSubject, StringComparison.OrdinalIgnoreCase));

        var refCount = dstEntry.Attributes is not null && dstEntry.Attributes.TryGetValue("ref", out var refs)
            ? refs.Count(v => v.Equals("test:src", StringComparison.OrdinalIgnoreCase))
            : 0;

        refCount.Should().Be(1,
            "auto-linking should be idempotent — running twice should not duplicate ref field values");
    }

    [Fact]
    public async Task OnForgotten_DoesNotThrow()
    {
        // Act & Assert — OnForgottenAsync should complete without error
        var store = MemoryStoreContext.Current!;
        var act = async () => await _sink.OnForgottenAsync("test:gone", true, store, CancellationToken.None);

        await act.Should().NotThrowAsync(
            "OnForgottenAsync is a no-op and should never throw");
    }

    [Fact]
    public async Task OrphanRemoval_WhenInboundRefAdded()
    {
        // Arrange — create an orphan entry
        var store = MemoryStoreContext.Current!;
        await _tools.Store(["Orphan content."], "test:wasorphan");

        // Fire sink to mark it as orphan
        await _sink.OnStoredAsync(
            "test:wasorphan",
            ["Orphan content."],
            store,
            CancellationToken.None);

        // Verify it's orphan
        var (scope, subject) = store.ParseQualifiedName("test:wasorphan");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
        entry.Keywords.Should().Contain("orphan", "entry should initially be flagged as orphan");

        // Act — create a referrer and fire sink for the orphan again
        await _tools.Store(["References test:wasorphan here."], "test:referrer2", keywords: ["ref:/test/wasorphan"]);

        await _sink.OnStoredAsync(
            "test:wasorphan",
            ["Orphan content."],
            store,
            CancellationToken.None);

        // Assert — orphan keyword should be removed
        entries = store.LoadIndex(scope);
        entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().NotContain("orphan",
            "orphan keyword should be removed when inbound references exist");
    }

    [Fact]
    public async Task AutoLink_WritesRefField_NotKeyword()
    {
        // Arrange — create two topic-scoped entries
        var store = MemoryStoreContext.Current!;
        await _tools.Store(["Source content placeholder."], "test:fieldsource");
        await _tools.Store(["Target content placeholder."], "test:fieldtarget");

        // Act — fire OnStoredAsync with content that references test:fieldtarget
        await _sink.OnStoredAsync(
            "test:fieldsource",
            ["This analysis references test:fieldtarget in its findings."],
            store,
            CancellationToken.None);

        // Assert — target entry should have Fields["ref"] containing the source,
        // and should NOT have a legacy ref: keyword.
        var (targetScope, targetSubject) = store.ParseQualifiedName("test:fieldtarget");
        var targetEntries = store.LoadIndex(targetScope);
        var targetEntry = targetEntries.First(e =>
            e.Name.Equals(targetSubject, StringComparison.OrdinalIgnoreCase));

        targetEntry.Attributes.Should().NotBeNull();
        targetEntry.Attributes!["ref"].Should().Contain(v => v.Contains("test:fieldsource"),
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
        await _tools.Store(["Content of field-linked entry."], "test:fieldlinked");
        await _tools.Store(["Some referencing content."], "test:fieldreferrer");

        var (referrerScope, referrerSubject) = store.ParseQualifiedName("test:fieldreferrer");
        var referrerEntry = store.LoadIndex(referrerScope)
            .First(e => e.Name.Equals(referrerSubject, StringComparison.OrdinalIgnoreCase));

        // The sink compares Fields["ref"] values against the source's FormatQualifiedName
        // output, which renders v1 "topic:subject" names in path form ("/topic/subject").
        var (linkedScope, linkedSubject) = store.ParseQualifiedName("test:fieldlinked");
        string linkedQualified = store.FormatQualifiedName(linkedScope, linkedSubject);

        var referrerFields = KnownFields.Merge(referrerEntry.Attributes, new() { ["ref"] = [linkedQualified] });
        store.Upsert(referrerEntry with { Attributes = referrerFields }, referrerScope);

        // Act — fire OnStoredAsync for the linked entry
        await _sink.OnStoredAsync(
            "test:fieldlinked",
            ["Content of field-linked entry."],
            store,
            CancellationToken.None);

        // Assert — entry should NOT have orphan keyword; the inbound ref lives only in Fields.
        var (scope, subject) = store.ParseQualifiedName("test:fieldlinked");
        var entries = store.LoadIndex(scope);
        var entry = entries.First(e =>
            e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));

        entry.Keywords.Should().NotContain("orphan",
            "an entry with an inbound ref FIELD (no legacy keyword) should not be flagged as orphan");
    }
}
