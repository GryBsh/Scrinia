namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Result of a query against a memory system benchmark implementation.
/// </summary>
public sealed record QueryResult(
    IReadOnlyList<string> FoundContent,
    int TokensConsumed,
    int ResultCount,
    bool FoundTarget,
    TimeSpan Elapsed);
