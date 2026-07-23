using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// End-to-end smoke for the harness's primary retriever: ingest a small haystack,
/// run a query, verify the most relevant doc comes back top-1. If this breaks, the
/// whole benchmark harness is broken — BM25 plumbing is wired wrong.
/// </summary>
public class ScriniaRetrieverTests : IAsyncLifetime
{
    private string _tempDir = null!;
    private ScriniaRetriever _retriever = null!;

    public Task InitializeAsync()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "scrinia-retbench-test-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(_tempDir);
        _retriever = new ScriniaRetriever(_tempDir);
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        await _retriever.DisposeAsync();
    }

    [Fact]
    public async Task Ingest_and_retrieve_returns_top_match()
    {
        await _retriever.IngestAsync(new HaystackDoc("d1", "I love hiking in the mountains every summer", null));
        await _retriever.IngestAsync(new HaystackDoc("d2", "PostgreSQL is my preferred database backend", null));
        await _retriever.IngestAsync(new HaystackDoc("d3", "The new espresso machine arrived today", null));

        var hits = await _retriever.RetrieveAsync("database backend preference", topK: 3);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("d2");
    }

    [Fact]
    public async Task Reset_clears_previous_ingest()
    {
        await _retriever.IngestAsync(new HaystackDoc("d1", "kubernetes deployment manifest", null));
        var before = await _retriever.RetrieveAsync("kubernetes", topK: 3);
        before.Should().NotBeEmpty();

        await _retriever.ResetAsync();
        var after = await _retriever.RetrieveAsync("kubernetes", topK: 3);
        after.Should().BeEmpty();
    }

    [Fact]
    public async Task Doc_id_is_preserved_through_search_round_trip()
    {
        // IDs with characters that would be sanitized in the filename — the retriever
        // must still return the *original* ID so the scorer can match ground truth.
        const string original = "convomem_user_5_msg_12";
        await _retriever.IngestAsync(new HaystackDoc(original, "my favorite color is teal", null));
        var hits = await _retriever.RetrieveAsync("favorite color", topK: 1);

        hits.Should().HaveCount(1);
        hits[0].Id.Should().Be(original);
    }

    [Fact]
    public async Task Attribute_value_is_retrievable_even_when_absent_from_content()
    {
        // Content never contains "zorptree"; only the agent attribute value carries it.
        await _retriever.IngestAsync(
            new HaystackDoc("d1", "the subsystem orchestrates lifecycle hooks", null),
            new Dictionary<string, string[]> { ["topic"] = new[] { "zorptree" } },
            null);
        await _retriever.IngestAsync(new HaystackDoc("d2", "completely unrelated gardening notes", null));

        var hits = await _retriever.RetrieveAsync("zorptree", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("d1",
            because: "the attribute's value must be seeded into BM25 even when absent from content");
    }

    [Fact]
    public async Task EntityGraph_LiftsAssociatedMemory_SharingEntity_ButNotQueryTerms()
    {
        // Converged onto the production EntityGraphScoreContributor: the graph is built per query
        // from the candidates' own attributes. d1 is tagged {alice, zeta}, d2 {zeta, bob};
        // both share identical content that never contains "alice". Query "alice" seeds the alice
        // node → d1, and PPR diffuses alice→zeta→d2, lifting d2 though it shares no query term.
        var root = Path.Combine(Path.GetTempPath(), "scrinia-eg-on-" + Guid.NewGuid().ToString("N")[..8]);
        await using var eg = new ScriniaRetriever(root, entityGraphEnabled: true);

        await eg.IngestAsync(new HaystackDoc("d1", "kickoff sync recap", null),
            new Dictionary<string, string[]> { ["person"] = new[] { "alice" }, ["topic"] = new[] { "zeta" } }, null);
        await eg.IngestAsync(new HaystackDoc("d2", "kickoff sync recap", null),
            new Dictionary<string, string[]> { ["topic"] = new[] { "zeta" }, ["person"] = new[] { "bob" } }, null);

        var hits = await eg.RetrieveAsync("alice", topK: 5);

        hits.Select(h => h.Id).Should().Contain("d2",
            because: "the entity graph diffuses alice→zeta→the bob memory, lifting d2 though it never mentions alice");
    }

    [Fact]
    public async Task EntityGraph_Off_LeavesAssociatedMemoryBuried()
    {
        // Same setup on the default (entity-graph-off) retriever: "alice" lexically hits only d1,
        // so BM25 alone never surfaces d2. Pins the sibling test's lift to the entity graph.
        await _retriever.IngestAsync(new HaystackDoc("d1", "kickoff sync recap", null),
            new Dictionary<string, string[]> { ["person"] = new[] { "alice" }, ["topic"] = new[] { "zeta" } }, null);
        await _retriever.IngestAsync(new HaystackDoc("d2", "kickoff sync recap", null),
            new Dictionary<string, string[]> { ["topic"] = new[] { "zeta" }, ["person"] = new[] { "bob" } }, null);

        var hits = await _retriever.RetrieveAsync("alice", topK: 5);

        hits.Select(h => h.Id).Should().NotContain("d2",
            because: "without the entity graph, d2 shares no query term with 'alice' and stays buried");
    }
}
