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
    public async Task Typed_keyword_value_is_retrievable_even_when_absent_from_content()
    {
        // Content never contains "zorptree"; only the typed keyword carries it.
        // Positional args: (doc, agentAnchors, agentKeywords, ct).
        await _retriever.IngestAsync(
            new HaystackDoc("d1", "the subsystem orchestrates lifecycle hooks", null),
            null,
            new[] { "topic:zorptree" });
        await _retriever.IngestAsync(new HaystackDoc("d2", "completely unrelated gardening notes", null));

        var hits = await _retriever.RetrieveAsync("zorptree", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("d1",
            because: "the typed keyword's value must be seeded into BM25 even when absent from content");
    }
}
