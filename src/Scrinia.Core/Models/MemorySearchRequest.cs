namespace Scrinia.Core.Models;

/// <summary>A parsed search request. Address filters are never raw strings.</summary>
public record MemorySearchRequest(string Query)
{
    public MemoryAddress? Address { get; init; }
}

/// <summary>A parsed listing request with an optional logical address prefix.</summary>
public sealed record MemoryListRequest
{
    public MemoryAddress? Prefix { get; init; }
    public IReadOnlyList<string>? Scopes { get; init; }
    public IReadOnlyList<string>? ExcludeTopics { get; init; }
}
