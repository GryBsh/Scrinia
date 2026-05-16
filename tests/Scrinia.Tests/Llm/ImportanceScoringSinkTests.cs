using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Llm;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Llm;

/// <summary>
/// Tests for <see cref="ImportanceScoringSink"/>. Covers the four scenarios the sink
/// must handle gracefully: (1) LLM unavailable → no-op, (2) LLM returns a score →
/// sidecar updated, (3) LLM returns null → sidecar untouched, (4) backfill pass walks
/// only previously-unscored entries.
/// </summary>
public sealed class ImportanceScoringSinkTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public ImportanceScoringSinkTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_imp_sink_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_root, ".scrinia", "store", "local"));
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private ArtifactEntry AddMemory(string name, string content, int? initialImportance = null)
    {
        var entry = new ArtifactEntry(
            Name: name,
            Uri: "",
            OriginalBytes: content.Length,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: $"desc for {name}",
            Importance: initialImportance);
        _store.Upsert(entry, "local");
        string path = _store.ArtifactPath(name, "local");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
        return entry;
    }

    // ── OnStoredAsync ────────────────────────────────────────────────────────

    [Fact]
    public async Task OnStoredAsync_NoLlmConfigured_NoOp()
    {
        // Real production case: user runs scri without configuring a Tier 2 LLM. The
        // sink must not blow up, must not log a scary error, must not modify the entry.
        AddMemory("oauth-flow", "some content");
        var sink = new ImportanceScoringSink(() => null);

        await sink.OnStoredAsync("oauth-flow", ["some content"], _store, CancellationToken.None);

        var entry = _store.LoadIndex("local").Single(e => e.Name == "oauth-flow");
        entry.Importance.Should().BeNull("no LLM configured means importance stays unscored");
    }

    [Fact]
    public async Task OnStoredAsync_LlmReturnsScore_PersistsImportanceToSidecar()
    {
        AddMemory("oauth-flow", "some content");
        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 7 };
        var sink = new ImportanceScoringSink(() => fakeLlm);

        await sink.OnStoredAsync("oauth-flow", ["some content"], _store, CancellationToken.None);

        var entry = _store.LoadIndex("local").Single(e => e.Name == "oauth-flow");
        entry.Importance.Should().Be(7);
        fakeLlm.ImportanceCalls.Should().Be(1);
    }

    [Fact]
    public async Task OnStoredAsync_LlmReturnsNull_LeavesSidecarUntouched()
    {
        // The LLM may fail / timeout / produce garbage; the sink must treat null as
        // "skip and continue" so the entry stays unscored (ranker handles via neutral fallback).
        AddMemory("oauth-flow", "some content");
        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = null };
        var sink = new ImportanceScoringSink(() => fakeLlm);

        await sink.OnStoredAsync("oauth-flow", ["some content"], _store, CancellationToken.None);

        var entry = _store.LoadIndex("local").Single(e => e.Name == "oauth-flow");
        entry.Importance.Should().BeNull("LLM failure must leave the existing value alone");
    }

    [Fact]
    public async Task OnStoredAsync_EmptyContent_NoLlmCall()
    {
        // Empty / whitespace-only content has no signal to score — skip early.
        AddMemory("blank", "");
        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 8 };
        var sink = new ImportanceScoringSink(() => fakeLlm);

        await sink.OnStoredAsync("blank", ["   "], _store, CancellationToken.None);

        fakeLlm.ImportanceCalls.Should().Be(0, "empty content should not trigger an LLM call");
    }

    [Fact]
    public async Task OnStoredAsync_MemoryDeletedBeforeSinkRuns_HandledGracefully()
    {
        // Sink runs on a background Task, so by the time it executes the memory may
        // have been forgotten. Sink should not throw.
        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 5 };
        var sink = new ImportanceScoringSink(() => fakeLlm);

        // Fire sink for a memory that was never persisted — simulates the race.
        var act = async () => await sink.OnStoredAsync(
            "never-existed", ["content"], _store, CancellationToken.None);

        await act.Should().NotThrowAsync("missing memory after sink fires is a tolerable race");
    }

    [Fact]
    public async Task OnStoredAsync_OverwritesPriorImportance()
    {
        // Re-storing a memory should rescore it — the content may have changed
        // materially so the old score is no longer trustworthy.
        AddMemory("oauth-flow", "some content", initialImportance: 3);
        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 9 };
        var sink = new ImportanceScoringSink(() => fakeLlm);

        await sink.OnStoredAsync("oauth-flow", ["some content"], _store, CancellationToken.None);

        var entry = _store.LoadIndex("local").Single(e => e.Name == "oauth-flow");
        entry.Importance.Should().Be(9, "re-store should produce a fresh score");
    }

    // ── OnAppendedAsync ──────────────────────────────────────────────────────

    [Fact]
    public async Task OnAppendedAsync_ScoresFullMemory_NotJustAppendedChunk()
    {
        // Regression for ultrareview finding: previously OnAppendedAsync scored only
        // the appendage, so a "Yes" append to an "important architectural decision"
        // memory would clobber Importance=9 with Importance≈1. Sink must re-read the
        // full memory after the append has been persisted and score against that.
        const string substantialBody = "Important architectural decision about authentication flow with OAuth and token rotation strategy.";
        AddMemory("oauth-decision", substantialBody, initialImportance: 9);

        // Simulate an append by extending the on-disk artifact (the MCP append path
        // already writes before the sink fires, so this mirrors production state).
        string path = _store.ArtifactPath("oauth-decision", "local");
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(substantialBody + "\nYes"));

        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 8 };
        var sink = new ImportanceScoringSink(() => fakeLlm);

        await sink.OnAppendedAsync("oauth-decision", "Yes", _store, CancellationToken.None);

        fakeLlm.ImportanceCalls.Should().Be(1);
        fakeLlm.LastImportanceContent.Should().Contain("architectural decision",
            "the sink must score against the full memory content, not the appended snippet alone");
        fakeLlm.LastImportanceContent.Should().Contain("Yes",
            "appended content should be part of what's scored");
    }

    [Fact]
    public async Task OnAppendedAsync_NoLlm_NoOp()
    {
        AddMemory("oauth-decision", "body", initialImportance: 9);
        var sink = new ImportanceScoringSink(() => null);

        await sink.OnAppendedAsync("oauth-decision", "extra", _store, CancellationToken.None);

        var entry = _store.LoadIndex("local").Single(e => e.Name == "oauth-decision");
        entry.Importance.Should().Be(9, "no-LLM path must leave the prior score alone");
    }

    // ── Backfill ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task BackfillAsync_OnlyScoresUnscoredMemories()
    {
        // Mixed workspace: one already-scored, two unscored. Backfill should touch
        // only the two — preserves prior manual / external scores.
        AddMemory("already-scored", "alpha content", initialImportance: 8);
        AddMemory("unscored-one", "beta content");
        AddMemory("unscored-two", "gamma content");

        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 5 };
        var result = await ImportanceScoringSink.BackfillAsync(_store, fakeLlm, onProgress: null, CancellationToken.None);

        result.Total.Should().Be(3);
        result.Scored.Should().Be(2);
        result.Skipped.Should().Be(1, "the already-scored memory should be skipped");
        result.Failed.Should().Be(0);
        fakeLlm.ImportanceCalls.Should().Be(2);

        var entries = _store.LoadIndex("local").ToDictionary(e => e.Name);
        entries["already-scored"].Importance.Should().Be(8, "prior score is preserved");
        entries["unscored-one"].Importance.Should().Be(5);
        entries["unscored-two"].Importance.Should().Be(5);
    }

    [Fact]
    public async Task BackfillAsync_LlmFailureCountsAsFailed_NotScored()
    {
        AddMemory("m1", "content one");
        AddMemory("m2", "content two");

        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = null };
        var result = await ImportanceScoringSink.BackfillAsync(_store, fakeLlm, onProgress: null, CancellationToken.None);

        result.Failed.Should().Be(2);
        result.Scored.Should().Be(0);

        _store.LoadIndex("local").Should().AllSatisfy(e =>
            e.Importance.Should().BeNull("LLM failure leaves Importance null"));
    }

    [Fact]
    public async Task BackfillAsync_ReportsProgress()
    {
        AddMemory("a", "x");
        AddMemory("b", "y");
        AddMemory("c", "z");

        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 5 };
        var progress = new List<(int done, int total)>();

        await ImportanceScoringSink.BackfillAsync(_store, fakeLlm,
            onProgress: (done, total) => progress.Add((done, total)),
            CancellationToken.None);

        progress.Should().NotBeEmpty();
        progress.Last().Should().Be((3, 3), "final progress tick should hit (total, total)");
        progress.Should().AllSatisfy(p => p.total.Should().Be(3));
    }

    [Fact]
    public async Task BackfillAsync_RespectsCancellation()
    {
        AddMemory("a", "x");
        AddMemory("b", "y");

        var fakeLlm = new FakeBackgroundLlm { ImportanceResponse = 5 };
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var act = async () => await ImportanceScoringSink.BackfillAsync(_store, fakeLlm, null, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
