using System.Collections.Concurrent;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Mcp;

namespace Scrinia.Tests.Llm;

/// <summary>
/// End-to-end tests for <see cref="LlmConsolidator"/> against a temp workspace.
/// Uses <see cref="FakeBackgroundLlm"/> for deterministic completions and the
/// real <see cref="ScriniaArtifactStore"/> via AsyncLocal overrides so the static
/// state of the store stays isolated from other test classes.
/// </summary>
public class LlmConsolidatorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _scriniaDir;
    private readonly FakeBackgroundLlm _llm;

    public LlmConsolidatorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_llm_consolidator_{Guid.NewGuid():N}");
        _scriniaDir = Path.Combine(_tempDir, ".scrinia");
        Directory.CreateDirectory(Path.Combine(_scriniaDir, "store"));
        ScriniaArtifactStore.OverrideWorkspaceRoot(_tempDir);
        ScriniaArtifactStore.OverrideEphemeralStore(new ConcurrentDictionary<string, EphemeralEntry>());
        _llm = new FakeBackgroundLlm();
    }

    public void Dispose()
    {
        ScriniaArtifactStore.OverrideWorkspaceRoot(null);
        ScriniaArtifactStore.OverrideEphemeralStore(null);
        try { Directory.Delete(_tempDir, recursive: true); } catch { /* best effort */ }
    }

    /// <summary>Helper: write an artifact + sidecar with a specific description.</summary>
    private ArtifactEntry SeedEntry(
        string name, string content, string description,
        Dictionary<string, int>? termFrequencies = null, string scope = "local")
    {
        string artifact = Nmp2ChunkedEncoder.Encode(content);
        File.WriteAllText(ScriniaArtifactStore.ArtifactPath(name, scope), artifact);
        var entry = new ArtifactEntry(
            Name: name,
            Uri: ScriniaArtifactStore.ArtifactUri(name, scope),
            OriginalBytes: System.Text.Encoding.UTF8.GetByteCount(content),
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: description,
            TermFrequencies: termFrequencies);
        ScriniaArtifactStore.Upsert(entry, scope);
        return entry;
    }

    private static ArtifactEntry Reload(string name, string scope = "local") =>
        ScriniaArtifactStore.LoadIndex(scope).First(e => e.Name == name);

    [Fact]
    public async Task BackfillsDescription_WhenCurrentDescriptionIsAutoFallback()
    {
        string content = "The quick brown fox jumps over the lazy dog. " +
            "This is the body of a memory whose description was never explicitly set, " +
            "so Store assigned the first 200 characters of content as the description.";
        // Simulate auto-fallback: description is exact prefix of content (<= 200 chars)
        SeedEntry("note-1", content, description: content[..Math.Min(200, content.Length)]);

        var entries = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(
            _llm, entries, justCompacted: new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        result.DescriptionsBackfilled.Should().Be(1);
        result.SessionsSummarized.Should().Be(0);
        _llm.DescriptionCalls.Should().Be(1);
        _llm.SummaryCalls.Should().Be(0);
        Reload("note-1").Description.Should().Be("Auto-generated description.");
    }

    [Fact]
    public async Task LeavesRealDescriptionAlone()
    {
        SeedEntry("note-2", "any content here", description: "A real, agent-written description.");

        var entries = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(
            _llm, entries, justCompacted: new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        result.DescriptionsBackfilled.Should().Be(0);
        _llm.DescriptionCalls.Should().Be(0);
        Reload("note-2").Description.Should().Be("A real, agent-written description.");
    }

    [Fact]
    public async Task SummarizesEntry_WhenInJustCompactedSet()
    {
        SeedEntry("session-2026-05-14", "long session log content here", description: "anything");
        var justCompacted = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "session-2026-05-14" };

        var entries = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(
            _llm, entries, justCompacted, _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        result.SessionsSummarized.Should().Be(1);
        result.DescriptionsBackfilled.Should().Be(0);
        _llm.SummaryCalls.Should().Be(1);
        _llm.DescriptionCalls.Should().Be(0);
        Reload("session-2026-05-14").Description.Should().Be(
            "Auto-generated summary paragraph for a session log.");
    }

    [Fact]
    public async Task ExtractsFacts_AndSeedsTermFrequencies()
    {
        SeedEntry("note-3", "content for facts", description: "real desc");
        _llm.FactsResponse = ["OAuth uses PKCE.", "Tokens rotate every 24h."];

        var entries = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(
            _llm, entries, justCompacted: new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        result.FactsExtracted.Should().Be(1);
        var entry = Reload("note-3");
        entry.Facts.Should().BeEquivalentTo(["OAuth uses PKCE.", "Tokens rotate every 24h."]);
        // Tokens from facts should be in the TF dict (e.g. "oauth", "pkce", "tokens", "rotate")
        entry.TermFrequencies.Should().NotBeNull();
        entry.TermFrequencies!.Should().ContainKey("oauth");
        entry.TermFrequencies.Should().ContainKey("pkce");
        entry.TermFrequencies.Should().ContainKey("tokens");
    }

    [Fact]
    public async Task SeedsFacts_WhenExistingTermFrequenciesHaveCaseInsensitiveCollisions()
    {
        // Reproduces a real-world crash: sidecar JSON deserializes TermFrequencies with the
        // default case-sensitive comparer, so a dict can legitimately hold both "BM25" and
        // "bm25". The fact-seeding code must merge case-insensitively without throwing.
        var existingTf = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["BM25"] = 3,
            ["bm25"] = 1,
            ["other"] = 5,
        };
        SeedEntry("note-collide", "content with bm25 and other words", description: "real desc",
            termFrequencies: existingTf);
        _llm.FactsResponse = ["The BM25 scorer ranks documents."];

        var entries = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(
            _llm, entries, justCompacted: new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        result.FactsExtracted.Should().Be(1);
        var entry = Reload("note-collide");
        entry.TermFrequencies.Should().NotBeNull();

        // After the round-trip the dict is case-sensitive again (default JSON comparer),
        // so the surviving key is whichever case-variant the merge inserted first.
        // Either is acceptable — what we're verifying is "no crash + the merged count is
        // higher than any single source variant + fact tokens were also seeded".
        var ciTf = new Dictionary<string, int>(entry.TermFrequencies!, StringComparer.OrdinalIgnoreCase);
        ciTf.Should().ContainKey("bm25", "case-insensitive lookup finds either BM25 or bm25");
        ciTf["bm25"].Should().BeGreaterThan(3,
            "merged count for BM25+bm25 plus fact-seed +2 should exceed either variant alone");
        ciTf.Should().ContainKey("scorer", "the fact 'The BM25 scorer ranks documents.' seeds 'scorer'");
    }

    [Fact]
    public async Task SkipsAlreadyProcessedEntries_OnReRunWithSameContent()
    {
        SeedEntry("note-4", "stable content", description: "stable description provided");

        var entries = ScriniaArtifactStore.ListScoped(null);
        await LlmConsolidator.RunAsync(_llm, entries, new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);
        int factCallsAfterFirst = _llm.FactsCalls;

        // Re-run with same content — should skip via the progress file.
        var result2 = await LlmConsolidator.RunAsync(_llm, entries, new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        result2.Skipped.Should().Be(1);
        result2.FactsExtracted.Should().Be(0);
        _llm.FactsCalls.Should().Be(factCallsAfterFirst); // no extra calls
    }

    [Fact]
    public async Task ReprocessesEntry_WhenContentHashChanges()
    {
        SeedEntry("note-5", "original content", description: "real description");
        var entries = ScriniaArtifactStore.ListScoped(null);
        await LlmConsolidator.RunAsync(_llm, entries, new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);
        int factCallsAfterFirst = _llm.FactsCalls;

        // Rewrite artifact with different content (changes hash).
        File.WriteAllText(ScriniaArtifactStore.ArtifactPath("note-5", "local"),
            Nmp2ChunkedEncoder.Encode("completely different content now"));

        var entriesAfter = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(_llm, entriesAfter, new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        result.FactsExtracted.Should().Be(1);
        _llm.FactsCalls.Should().Be(factCallsAfterFirst + 1);
    }

    [Fact]
    public async Task ContinuesBatch_WhenLlmReturnsNullForOneMemory()
    {
        SeedEntry("note-6a", "content A", description: "real");
        SeedEntry("note-6b", "content B", description: "real");
        // Stage null facts: emulates timeout or garbage output.
        _llm.FactsResponse = null;

        var entries = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(_llm, entries, new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        // Both calls happen; both fail (no work done); batch continues.
        _llm.FactsCalls.Should().Be(2);
        result.Failed.Should().Be(2);
        result.FactsExtracted.Should().Be(0);
        Reload("note-6a").Facts.Should().BeNull();
        Reload("note-6b").Facts.Should().BeNull();
    }

    [Fact]
    public async Task DryRun_DoesNotCallLlmOrModifySidecar()
    {
        string content = "content used for description fallback";
        SeedEntry("note-7", content, description: content[..Math.Min(200, content.Length)]);

        var entries = ScriniaArtifactStore.ListScoped(null);
        var result = await LlmConsolidator.RunAsync(_llm, entries, new HashSet<string>(),
            _scriniaDir, dryRun: true, onWarning: null, CancellationToken.None);

        result.Processed.Should().Be(1);
        _llm.DescriptionCalls.Should().Be(0);
        _llm.FactsCalls.Should().Be(0);
        Reload("note-7").Description.Should().Be(content[..Math.Min(200, content.Length)]);
        Reload("note-7").Facts.Should().BeNull();
    }

    [Fact]
    public async Task ProgressFileIsWritten_UnderScriniaDir()
    {
        SeedEntry("note-8", "any content", description: "real");

        var entries = ScriniaArtifactStore.ListScoped(null);
        await LlmConsolidator.RunAsync(_llm, entries, new HashSet<string>(),
            _scriniaDir, dryRun: false, onWarning: null, CancellationToken.None);

        string progressFile = Path.Combine(_scriniaDir, ".consolidate-progress.json");
        File.Exists(progressFile).Should().BeTrue();
        string json = File.ReadAllText(progressFile);
        json.Should().Contain("\"note-8\"");
        json.Should().Contain("contentHash");
    }
}
