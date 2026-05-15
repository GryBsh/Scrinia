using Microsoft.Extensions.Logging;

namespace Scrinia.Core.Resilience;

/// <summary>
/// Retry helper for filesystem operations that race against external file holders —
/// AV scanners (Windows Defender), cloud sync clients (OneDrive, Synology Drive,
/// Dropbox), and indexers (Windows Search). These tools briefly grab a read handle
/// on freshly-written files, causing <see cref="File.Move"/>/<c>FileStream</c> opens
/// to fail with <c>"The process cannot access the file"</c>.
///
/// <para>The delays (50/100/200/400/800ms, ~1.5s total) are tuned for the typical
/// release window of these tools. Longer than that and the failure is probably real
/// — the slot reservation gives up and surfaces the original <see cref="IOException"/>.</para>
///
/// <para>Only <see cref="IOException"/> is retried. Permission errors
/// (<see cref="UnauthorizedAccessException"/>) and missing-path errors
/// (<see cref="DirectoryNotFoundException"/>) are surfaced immediately so genuine
/// configuration mistakes are visible.</para>
/// </summary>
public static class FileRetry
{
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(400),
        TimeSpan.FromMilliseconds(800),
    ];

    /// <summary>Runs a synchronous file operation, retrying on transient
    /// <see cref="IOException"/>. Returns after the operation succeeds, or rethrows the
    /// last <see cref="IOException"/> after all retries are exhausted.</summary>
    public static void Run(Action action, ILogger? logger = null)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (IOException ex) when (attempt < DefaultDelays.Length && !IsPermanentIoFailure(ex))
            {
                logger?.LogDebug(
                    ex,
                    "File op retry {Attempt}/{Max} after {Delay}ms ({Message})",
                    attempt + 1, DefaultDelays.Length, DefaultDelays[attempt].TotalMilliseconds, ex.Message);
                Thread.Sleep(DefaultDelays[attempt]);
            }
        }
    }

    /// <summary>Runs an asynchronous file operation, retrying on transient
    /// <see cref="IOException"/>. Returns after the operation succeeds, or rethrows the
    /// last <see cref="IOException"/> after all retries are exhausted.</summary>
    public static async Task RunAsync(Func<Task> action, ILogger? logger = null, CancellationToken ct = default)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                await action();
                return;
            }
            catch (IOException ex) when (attempt < DefaultDelays.Length && !IsPermanentIoFailure(ex))
            {
                logger?.LogDebug(
                    ex,
                    "File op retry {Attempt}/{Max} after {Delay}ms ({Message})",
                    attempt + 1, DefaultDelays.Length, DefaultDelays[attempt].TotalMilliseconds, ex.Message);
                await Task.Delay(DefaultDelays[attempt], ct);
            }
        }
    }

    // FileNotFound / DirectoryNotFound / PathTooLong derive from IOException but are
    // permanent — no amount of waiting will fix them, and retrying obscures the real
    // problem. Filter them out of the retry loop.
    private static bool IsPermanentIoFailure(IOException ex) =>
        ex is FileNotFoundException or DirectoryNotFoundException or PathTooLongException;
}
