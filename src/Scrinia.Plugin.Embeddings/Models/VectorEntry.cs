namespace Scrinia.Plugin.Embeddings.Models;

/// <summary>A named vector stored in the vector index.</summary>
/// <param name="Name">Qualified memory name (e.g. "session-notes" or "api:auth-flow").</param>
/// <param name="ChunkIndex">Null for whole-entry vectors, chunk index for per-chunk vectors.</param>
/// <param name="Vector">L2-normalized embedding vector.</param>
public sealed record VectorEntry(string Name, int? ChunkIndex, float[] Vector);
