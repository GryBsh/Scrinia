namespace Scrinia.Core.Models;

public sealed record ChunkEntry(
    int ChunkIndex,
    string? ContentPreview = null,
    string[]? Keywords = null,
    Dictionary<string, int>? TermFrequencies = null);
