using BenchmarkDotNet.Attributes;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Benchmarks;

/// <summary>
/// Measures <see cref="FileMemoryStore.Upsert"/> — the write path tasks 1-6 rewired. Before,
/// every Upsert called <c>LoadEntriesFromSidecars</c> to rebuild the scope's index cache: a full
/// directory enumeration plus one file read and JSON parse per existing sidecar. Writing N
/// memories sequentially therefore cost O(N^2) file reads. After, Upsert writes exactly one
/// sidecar and rebuilds the in-memory cache copy-on-write from the list already held in memory —
/// zero sidecar re-reads on any write.
///
/// <para>Both benchmarks run under <see cref="MonitoringInProcessConfig"/> — Monitoring strategy
/// because each mutates real on-disk state (writes files under a temp scope) inside
/// <see cref="IterationSetup"/>/<see cref="IterationCleanup"/>, and Monitoring is the strategy
/// BenchmarkDotNet documents for exactly that shape: it runs exactly one invocation per iteration.
/// The default Throughput strategy is free to unroll several invocations inside one iteration for
/// a workload this fast, which would silently let a "single Upsert" benchmark perform several
/// unmeasured-adjacent writes between one Setup/Cleanup pair — growing the scope past the labeled
/// N mid-iteration. See that class for why the toolchain is also pinned to in-process.</para>
/// </summary>
[Config(typeof(MonitoringInProcessConfig))]
public class WritePathBenchmarks
{
    [Params(100, 1_000, 5_000)]
    public int Entries;

    private string _seqRoot = null!;
    private FileMemoryStore _seqStore = null!;

    private string _marginalRoot = null!;
    private FileMemoryStore _marginalStore = null!;
    private int _marginalNext;

    [IterationSetup(Target = nameof(SequentialUpserts))]
    public void SetupFreshScope()
    {
        _seqRoot = Path.Combine(Path.GetTempPath(), $"scrinia_bench_write_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_seqRoot);
        _seqStore = new FileMemoryStore(_seqRoot);
    }

    [IterationCleanup(Target = nameof(SequentialUpserts))]
    public void CleanupFreshScope()
    {
        _seqStore.Dispose();
        try { Directory.Delete(_seqRoot, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// N sequential Upsert calls into a scope that starts empty. This is the benchmark that shows
    /// the O(N^2) -> O(N) shape rather than asserting it: the "before" curve should scale with the
    /// square of Entries (dominated by the per-write directory re-read), the "after" curve should
    /// scale close to linearly (dominated by N sidecar writes, each independent of scope size).
    /// </summary>
    [Benchmark]
    public void SequentialUpserts()
    {
        for (int i = 0; i < Entries; i++)
        {
            var entry = new ArtifactEntry($"entry-{i}", $"file:///entry-{i}.nmp2", 512, 1, DateTimeOffset.UtcNow);
            _seqStore.Upsert(entry, "local");
        }
    }

    [IterationSetup(Target = nameof(MarginalUpsert))]
    public void SetupWarmScope()
    {
        _marginalRoot = Path.Combine(Path.GetTempPath(), $"scrinia_bench_marginal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_marginalRoot);
        _marginalStore = new FileMemoryStore(_marginalRoot);

        // Unmeasured: populate the scope with Entries pre-existing entries so the ONE write
        // the benchmark body performs lands on a scope that already holds N entries, not an
        // empty one. Skipping this step would measure "the first write into an empty scope,"
        // a materially cheaper and different operation than what an established workspace feels
        // on every subsequent write.
        for (int i = 0; i < Entries; i++)
        {
            var entry = new ArtifactEntry($"seed-{i}", $"file:///seed-{i}.nmp2", 512, 1, DateTimeOffset.UtcNow);
            _marginalStore.Upsert(entry, "local");
        }

        // Force a load so the in-memory index cache is warm before the measured write. Upsert's
        // copy-on-write path only fires when the scope is already cached (FileMemoryStore.cs, see
        // the "Scope is not cached: invalidate only" branch); without this LoadIndex call the
        // scope's cache could still be warm from the seeding loop above in practice, but this
        // makes the precondition explicit and independent of that implementation detail.
        _marginalStore.LoadIndex("local");

        // Distinct name per invocation. Monitoring performs one invocation per iteration, so this
        // only ever advances by one between setup calls, but a repeated name would replace an
        // existing slot instead of appending a new one — cheaper and not what "one more write"
        // means for a growing workspace.
        _marginalNext = Entries;
    }

    [IterationCleanup(Target = nameof(MarginalUpsert))]
    public void CleanupWarmScope()
    {
        _marginalStore.Dispose();
        try { Directory.Delete(_marginalRoot, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    /// <summary>
    /// The marginal cost of ONE Upsert into a scope that already holds Entries entries and a warm
    /// cache — the cost a user with an established workspace actually feels on every write, as
    /// opposed to the cumulative cost <see cref="SequentialUpserts"/> measures.
    /// </summary>
    [Benchmark]
    public void MarginalUpsert()
    {
        var entry = new ArtifactEntry(
            $"seed-{_marginalNext}", $"file:///seed-{_marginalNext}.nmp2", 512, 1, DateTimeOffset.UtcNow);
        _marginalStore.Upsert(entry, "local");
        _marginalNext++;
    }
}
