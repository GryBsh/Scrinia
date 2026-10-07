using BenchmarkDotNet.Attributes;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Benchmarks;

/// <summary>
/// The read path <c>scri hint</c> runs on every user prompt, in process, with the JIT and host
/// startup already paid.
///
/// <para><b>Why this exists.</b> The whole-process cost of <c>hint</c> is measured by
/// <c>./measure-startup.ps1</c>, which drives the published binary. What that number cannot say is
/// how much of it is Scrinia and how much is .NET starting up, because the script has no way to
/// subtract one from the other — and it is an unreliable number in its own right, swinging between
/// roughly 120ms and 400ms on this machine for reasons that were measured but not explained. This
/// benchmark measures the Scrinia half alone, in process, where BenchmarkDotNet controls the
/// conditions. Whatever it reports is the part that scales with the corpus; everything else in the
/// wall-clock figure is fixed cost that no amount of search work will change.</para>
///
/// <para><b>What it reported, and what that settles.</b> Default job, this machine:</para>
/// <code>
///   entries    matches      misses
///        50    50.4 us     38.4 us
///       250     336 us      311 us
///     1,000   1,152 us      945 us
/// </code>
/// <para>Microseconds. At this repository's 69 memories the search costs roughly <b>0.07ms</b>,
/// against a whole-command wall time that has never been observed below 100ms — under one tenth of
/// one percent, and that is the generous reading. The read path is not where <c>hint</c>'s latency
/// is, and optimising it further would buy nothing measurable.</para>
///
/// <para>Corroborated from the other direction, by accident: a run against a workspace that turned
/// out to be <i>empty</i> cost the same as one against all 69 memories. The corpus contributes
/// nothing detectable to the wall clock, which is exactly what these numbers predict.</para>
///
/// <para><b>Why it seeds through <see cref="IMemoryStore.StoreAsync"/></b> rather than
/// constructing <see cref="ArtifactEntry"/> values directly the way
/// <see cref="LoadIndexBenchmarks"/> does: BM25 scores against <c>TermFrequencies</c>, which the
/// store derives on write. Hand-built entries carry none, so a search over them matches nothing
/// and measures an empty scan — fast, stable, and meaningless.</para>
///
/// <para><b>Two queries, deliberately.</b> A matching query pays for scoring plus the MMR rerank
/// over <c>InnerLimit</c> candidates; a non-matching one exits early once nothing clears the score
/// floor. The hook fires on every prompt, most of which match nothing, so the miss is the common
/// case and the one worth watching.</para>
/// </summary>
[MemoryDiagnoser]
[System.Diagnostics.CodeAnalysis.SuppressMessage("Design", "CA1001:Types that own disposable fields should be disposable", Justification = "BenchmarkDotNet invokes GlobalCleanup Cleanup, which disposes _store.")]
public class HintReadPathBenchmarks
{
    [Params(50, 250, 1_000)]
    public int EntryCount;

    private string _root = null!;
    private FileMemoryStore _store = null!;
    private HintCommand _hint = null!;

    // Distinct vocabularies so BM25 has something to discriminate on rather than scoring every
    // entry identically — a corpus of one repeated sentence measures the loop, not the ranker.
    private static readonly string[] Topics =
    [
        "merge driver resolves sidecar conflicts by keyword union above the jaccard threshold",
        "the encoder compresses chunks with brotli and base64url then verifies a crc32 checksum",
        "embedding vectors are stored per scope and quarantined when the provider signature changes",
        "the workspace root is the directory containing the scrinia folder and every path resolves from it",
        "ephemeral memories live in the process that created them and never reach disk",
        "attribute values seed the term frequency index so a memory is findable by its own facets",
        "archived versions are stamped to the millisecond and an archive is never overwritten",
        "the store derives keywords chunk entries and previews so callers never compute them",
    ];

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_hint_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);

        for (int i = 0; i < EntryCount; i++)
        {
            string body = $"{Topics[i % Topics.Length]} (entry {i}, revision {i % 7})";
            _store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse($"entry-{i}".StartsWith('/') || $"entry-{i}".StartsWith('~') ? $"entry-{i}" : "/" + $"entry-{i}"), "local", [body]))
                .GetAwaiter().GetResult();
        }

        // Warm the scope's index cache so the measured body is the steady-state read, matching
        // what the second and later searches in a long-lived `scri serve` pay.
        _store.LoadIndex("local");
        _hint = new HintCommand(_store);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Benchmark(Description = "hint, query matches")]
    public int Hint_Match() => _hint.Compute(
        "how does the merge driver resolve sidecar conflicts",
        HintCommand.DefaultMinScore,
        HintCommand.DefaultMinPromptChars,
        HintCommand.DefaultTopK,
        HintCommand.DefaultInnerLimit,
        HintCommand.DefaultDiversityLambda).Matches.Count;

    [Benchmark(Description = "hint, query matches nothing")]
    public int Hint_Miss() => _hint.Compute(
        "zzzzqqq unrelated gibberish token nobody stored",
        HintCommand.DefaultMinScore,
        HintCommand.DefaultMinPromptChars,
        HintCommand.DefaultTopK,
        HintCommand.DefaultInnerLimit,
        HintCommand.DefaultDiversityLambda).Matches.Count;
}
