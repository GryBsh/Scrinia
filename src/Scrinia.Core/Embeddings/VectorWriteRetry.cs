using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Embeddings;

/// <summary>
/// Retries a vector write that was declined because the scope's file could not be read.
///
/// <para>For detached indexing only — event-sink handlers run on a background task, so waiting
/// costs nothing user-visible. The delays are much longer than <see cref="Resilience.FileRetry"/>'s
/// millisecond backoff, covering a handle held for tens of seconds rather than momentarily. Not
/// for bulk reindex loops, where a long wait per item would stall the whole run, or for
/// request/response tools, where the caller retries.</para>
/// </summary>
public static class VectorWriteRetry
{
    private static readonly TimeSpan[] Delays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(30),
    ];

    /// <summary>
    /// Runs <paramref name="write"/>, retrying while the vector file stays unreadable.
    /// Returns true if it succeeded, false if the attempts were exhausted or cancelled.
    /// </summary>
    public static async Task<bool> RunAsync(
        Func<Task> write, ILogger? logger, string description, CancellationToken ct)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await write();
                if (attempt > 0)
                    logger?.LogInformation("Indexed '{What}' on retry {Attempt}.", description, attempt);
                return true;
            }
            catch (VectorStoreUnavailableException ex)
            {
                if (attempt >= Delays.Length)
                {
                    logger?.LogWarning(
                        "Skipped indexing '{What}' after {Attempts} attempts: {Reason} Search falls back to BM25 until the next write or `scri reindex`.",
                        description, attempt + 1, ex.Message);
                    return false;
                }

                logger?.LogDebug(
                    "Vector write for '{What}' declined (attempt {Attempt}); retrying in {Delay}s.",
                    description, attempt + 1, Delays[attempt].TotalSeconds);

                try
                {
                    await Task.Delay(Delays[attempt], ct);
                }
                catch (OperationCanceledException)
                {
                    return false;
                }
            }
        }
    }
}
