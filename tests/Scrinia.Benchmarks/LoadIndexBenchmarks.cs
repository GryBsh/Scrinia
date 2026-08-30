using BenchmarkDotNet.Attributes;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Benchmarks;

/// <summary>
/// Measures a warm <see cref="FileMemoryStore.LoadIndex"/> — the case that used to open and
/// close a .lock file handle before consulting the in-memory cache.
///
/// Sizes deliberately run past 272 entries: the prior numbers on this path came from a
/// LoCoMo-seeded corpus that topped out there, and that branch recorded its scaling beyond
/// that point as extrapolated rather than measured.
///
/// The defensive ToList() on a cache hit is included in the measurement because it is part of
/// what a caller pays today; it is measured, not removed, since removing it is a separate
/// question with its own analysis.
/// </summary>
[MemoryDiagnoser]
public class LoadIndexBenchmarks
{
    [Params(100, 1_000, 5_000)]
    public int EntryCount;

    private string _root = null!;
    private FileMemoryStore _store = null!;

    [GlobalSetup]
    public void Setup()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_bench_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);

        var entries = new List<ArtifactEntry>(EntryCount);
        for (int i = 0; i < EntryCount; i++)
            entries.Add(new ArtifactEntry(MemoryAddress.Parse($"/entry-{i}"), $"artifacts/entry-{i}.nmp2", 512, 1,
                DateTimeOffset.UtcNow));

        // SaveIndex writes every sidecar and leaves the scope's cache populated, so the
        // benchmark body below measures the warm path from its first iteration.
        _store.SaveIndex(entries, "local");
        _store.LoadIndex("local");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort temp cleanup */ }
    }

    [Benchmark]
    public int LoadIndex_WarmCache() => _store.LoadIndex("local").Count;
}
