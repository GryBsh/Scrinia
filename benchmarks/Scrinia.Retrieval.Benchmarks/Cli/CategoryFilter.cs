using System.Runtime.CompilerServices;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Cli;

/// <summary>
/// Filters a benchmark question stream by <c>--category</c>. When the filter is null
/// or empty, the source passes through unchanged (no-op). Used by run-benchmarks.ps1's
/// parallel fanout to spawn one subprocess per category and merge results downstream.
/// </summary>
internal static class CategoryFilter
{
    public static async IAsyncEnumerable<BenchmarkQuestion> ApplyAsync(
        IAsyncEnumerable<BenchmarkQuestion> source,
        string? category,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(category))
        {
            await foreach (var q in source.WithCancellation(ct)) yield return q;
            yield break;
        }
        await foreach (var q in source.WithCancellation(ct))
        {
            if (string.Equals(q.Category, category, StringComparison.OrdinalIgnoreCase))
                yield return q;
        }
    }

    public static async IAsyncEnumerable<BenchmarkQuestionGroup> ApplyGroupedAsync(
        IAsyncEnumerable<BenchmarkQuestionGroup> source,
        string? category,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(category))
        {
            await foreach (var g in source.WithCancellation(ct)) yield return g;
            yield break;
        }
        await foreach (var g in source.WithCancellation(ct))
        {
            // Filter the per-group question list. Skip groups with no matching
            // questions entirely so we don't pay ingest cost for nothing.
            var filtered = g.Questions
                .Where(q => string.Equals(q.Category, category, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (filtered.Count == 0) continue;
            yield return g with { Questions = filtered };
        }
    }
}
