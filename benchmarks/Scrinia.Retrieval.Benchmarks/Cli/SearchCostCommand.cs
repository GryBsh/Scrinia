using System.Diagnostics;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Loaders;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Wires the <c>searchcost</c> subcommand — measures whether
/// <see cref="FileMemoryStore.SearchAll(SearchQuery)"/> reusing a pre-materialized
/// <see cref="SearchQuery.Candidates"/> list (what <see cref="Search.SearchPipeline"/> now
/// passes) is actually faster than letting the store call
/// <see cref="FileMemoryStore.ListScoped(string?, string?)"/> itself. This is a direct
/// store-level measurement, not a full pipeline run: no contributor, no embeddings — just the
/// two code paths inside <c>SearchAll</c> that the task's F5 change chooses between.
///
/// <para>Each query is timed in BOTH modes so the numbers are paired; which mode runs first
/// alternates per query (even index: materialized first; odd index: null first) so neither
/// mode gets to monopolize JIT warm-up or OS cache effects.</para>
/// </summary>
internal static class SearchCostCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct)
    {
        string? dataPath = Program.Flag(args, "--data");
        if (string.IsNullOrWhiteSpace(dataPath))
        {
            Console.Error.WriteLine("error: --data <locomo10.json> is required");
            return 1;
        }

        int conversations = Program.FlagInt(args, "--conversations", 3);
        int queryCount = Program.FlagInt(args, "--queries", 200);
        int warmup = Program.FlagInt(args, "--warmup", 20);
        const int topK = 50;

        Console.WriteLine($"searchcost: data={dataPath}, conversations={conversations}, queries={queryCount}, warmup={warmup}");

        string root = Path.Combine(Path.GetTempPath(), $"scrinia_searchcost_{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        try
        {
            var store = new FileMemoryStore(root);
            try
            {
                var loader = new LoCoMoLoader(dataPath);
                var allQuestions = new List<string>();
                int corpusSize = 0;
                int groupsTaken = 0;

                await foreach (var group in loader.LoadAsync(ct))
                {
                    if (groupsTaken >= conversations) break;

                    foreach (var doc in group.SharedHaystack)
                    {
                        string name = SanitizeName($"{group.GroupId}_{doc.Id}");
                        var (keywords, tf) = TextAnalysis.AnalyzeText(doc.Text);
                        var entry = new ArtifactEntry(
                            Name: name,
                            Uri: $"bench://{group.GroupId}/{doc.Id}",
                            OriginalBytes: doc.Text.Length,
                            ChunkCount: 0,
                            CreatedAt: doc.Timestamp ?? DateTimeOffset.UtcNow,
                            Keywords: keywords,
                            TermFrequencies: tf,
                            ContentPreview: doc.Text);
                        store.Upsert(entry);
                        corpusSize++;
                    }

                    foreach (var q in group.Questions)
                        allQuestions.Add(q.QueryText);

                    groupsTaken++;
                }

                if (allQuestions.Count == 0)
                {
                    Console.Error.WriteLine("error: no questions loaded from the requested conversations");
                    return 1;
                }

                // Warm-up queries come from the front of the pool and are discarded; the measured
                // queries are the NEXT queryCount (falling back to wrapping around the pool if it's
                // too small — the corpus/warm-up state don't depend on which text is asked).
                var warmupQueries = new List<string>(warmup);
                for (int i = 0; i < warmup; i++)
                    warmupQueries.Add(allQuestions[i % allQuestions.Count]);

                var measuredQueries = new List<string>(queryCount);
                for (int i = 0; i < queryCount; i++)
                    measuredQueries.Add(allQuestions[(warmup + i) % allQuestions.Count]);

                Console.WriteLine($"corpus: {corpusSize} memories across {groupsTaken} conversations; {allQuestions.Count} questions available");

                foreach (string q in warmupQueries)
                {
                    RunOne(store, q, topK, withCandidates: true);
                    RunOne(store, q, topK, withCandidates: false);
                }

                var materializedMs = new List<double>(measuredQueries.Count);
                var nullMs = new List<double>(measuredQueries.Count);

                for (int i = 0; i < measuredQueries.Count; i++)
                {
                    string q = measuredQueries[i];
                    bool materializedFirst = i % 2 == 0;
                    if (materializedFirst)
                    {
                        materializedMs.Add(RunOne(store, q, topK, withCandidates: true));
                        nullMs.Add(RunOne(store, q, topK, withCandidates: false));
                    }
                    else
                    {
                        nullMs.Add(RunOne(store, q, topK, withCandidates: false));
                        materializedMs.Add(RunOne(store, q, topK, withCandidates: true));
                    }
                }

                materializedMs.Sort();
                nullMs.Sort();
                double materializedMedian = Percentile(materializedMs, 0.5);
                double materializedP95 = Percentile(materializedMs, 0.95);
                double nullMedian = Percentile(nullMs, 0.5);
                double nullP95 = Percentile(nullMs, 0.95);

                Console.WriteLine();
                Console.WriteLine("=== searchcost ===");
                Console.WriteLine($"corpus size:  {corpusSize}");
                Console.WriteLine($"query count:  {measuredQueries.Count} (warmup: {warmup}, discarded)");
                Console.WriteLine();
                Console.WriteLine($"{"mode",-24}{"median (ms)",14}{"p95 (ms)",14}");
                Console.WriteLine($"{"Candidates materialized",-24}{materializedMedian,14:F4}{materializedP95,14:F4}");
                Console.WriteLine($"{"Candidates = null",-24}{nullMedian,14:F4}{nullP95,14:F4}");
                Console.WriteLine();
                double medianDeltaMs = nullMedian - materializedMedian;
                double p95DeltaMs = nullP95 - materializedP95;
                Console.WriteLine($"delta (null - materialized): median {medianDeltaMs:+0.0000;-0.0000} ms, p95 {p95DeltaMs:+0.0000;-0.0000} ms");
            }
            finally
            {
                store.Dispose();
            }
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }

        return 0;
    }

    /// <summary>Times a single <c>SearchAll</c> call. The candidate materialization for the
    /// <paramref name="withCandidates"/> = true case happens BEFORE the stopwatch starts — in
    /// production that cost is paid once by <see cref="Search.SearchPipeline"/> for the
    /// contributor, not by this call, so it's out of scope for what F5 changed.</summary>
    private static double RunOne(FileMemoryStore store, string queryText, int topK, bool withCandidates)
    {
        IReadOnlyList<ScopedArtifact>? candidates = withCandidates
            ? store.ListScoped(scopes: null, excludeTopics: null)
            : null;

        var query = new SearchQuery(queryText) { Limit = topK, Candidates = candidates };

        var sw = Stopwatch.StartNew();
        _ = store.SearchAll(query);
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds;
    }

    /// <summary>Linear-interpolation percentile (numpy-default semantics) over an ascending-sorted list.</summary>
    private static double Percentile(List<double> sorted, double p)
    {
        if (sorted.Count == 0) return 0;
        if (sorted.Count == 1) return sorted[0];
        double idx = p * (sorted.Count - 1);
        int lo = (int)Math.Floor(idx);
        int hi = (int)Math.Ceiling(idx);
        if (lo == hi) return sorted[lo];
        double frac = idx - lo;
        return sorted[lo] + (sorted[hi] - sorted[lo]) * frac;
    }

    /// <summary>Maps a benchmark doc/group ID pair into a filename-safe artifact name.</summary>
    private static string SanitizeName(string id)
    {
        Span<char> buf = stackalloc char[id.Length];
        for (int i = 0; i < id.Length; i++)
        {
            char c = id[i];
            buf[i] = char.IsLetterOrDigit(c) || c is '-' or '_' or '.' ? c : '_';
        }
        return new string(buf);
    }
}
