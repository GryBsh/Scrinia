namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// A single knowledge fact used in memory system benchmarks.
/// </summary>
public sealed record BenchmarkFact(
    string Topic,
    string Key,
    string Content,
    string Question,
    string[] UniqueTerms,
    bool IsUpdate = false,
    string? OriginalContent = null);
