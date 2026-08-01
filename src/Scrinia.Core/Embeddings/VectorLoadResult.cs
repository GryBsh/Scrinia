using Scrinia.Core.Embeddings.Models;

namespace Scrinia.Core.Embeddings;

/// <summary>Whether a vector load reflects what is actually on disk.</summary>
public enum VectorLoadStatus
{
    /// <summary>The file was read (or is genuinely unusable) — the entry list is authoritative.</summary>
    Loaded,

    /// <summary>No file exists for the scope. An empty list is the correct answer.</summary>
    Missing,

    /// <summary>The file exists but could not be read. Nothing may be concluded about its contents.</summary>
    Failed,
}

/// <summary>
/// Result of a vector-store load attempt. Separates "this scope has no vectors" from "we could
/// not read this scope's vectors" — callers write back, and a full rewrite from an empty
/// baseline erases whatever was on disk.
/// </summary>
public readonly record struct VectorLoadResult(VectorLoadStatus Status, List<VectorEntry> Vectors)
{
    /// <summary>An empty, authoritative result for a scope with no file on disk.</summary>
    public static VectorLoadResult Missing => new(VectorLoadStatus.Missing, []);

    /// <summary>A load that failed. The empty list is a placeholder, not a fact about the scope.</summary>
    public static VectorLoadResult Failed => new(VectorLoadStatus.Failed, []);

    /// <summary>
    /// True when <see cref="Vectors"/> may be cached or used as the baseline for a write.
    /// </summary>
    public bool IsAuthoritative => Status != VectorLoadStatus.Failed;
}

/// <summary>
/// Thrown when a scope's vectors cannot be read and the caller was about to write, which would
/// rebuild the file from an incomplete baseline. Embedding indexing is retryable, so callers
/// treat this as a deferral rather than a hard failure.
/// </summary>
public sealed class VectorStoreUnavailableException(string scope, string path, Exception? inner = null)
    : IOException($"Vectors for scope '{scope}' could not be read from '{path}', so writing would risk discarding them. Check that the file is readable and not held by another process, then retry.", inner)
{
    /// <summary>The scope whose vector file could not be read.</summary>
    public string Scope { get; } = scope;

    /// <summary>The path that could not be read.</summary>
    public string Path { get; } = path;
}
