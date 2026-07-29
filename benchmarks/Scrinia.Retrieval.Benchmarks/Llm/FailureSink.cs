using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Scrinia.Retrieval.Benchmarks.Llm;

/// <summary>
/// Diagnostic drain for LLM calls that never produced a usable reply — the ingest harness's
/// empty/unparseable docs and the judge's blank answerer/grader completions. Writes one NDJSON
/// record per failure (the full prompt plus every attempt's raw response) so a run's blanks can
/// be inspected after the fact — e.g. to tell a token-budget truncation (large prompt, empty
/// completion) from a transport error or genuinely unparseable output.
///
/// <para>Unlike <see cref="LlmResponseCache"/> this is NEVER loaded or reused — it is write-only
/// telemetry. The file is <b>truncated once at the start of a run</b> (the first construction for
/// a given path within the process) and appended to thereafter, so it always reflects the current
/// run. Its path is derived from the paired cache file via <see cref="PathForCache"/>:
/// <c>agent-…​.ndjson</c> → <c>agent-…​.failures.ndjson</c>.</para>
///
/// <para><b>Concurrency:</b> ingest runs under <c>Parallel.ForEachAsync</c>; appends are guarded by
/// <c>_writeLock</c> and use <c>FileMode.Append</c> + <c>FileShare.ReadWrite</c>, matching the cache.
/// In the cross-process parallel-categories bench mode the file is best-effort (same caveat as the
/// cache's cross-process notes); the judge — the primary blank source — runs single-process, so its
/// file is exact.</para>
/// </summary>
public sealed class FailureSink
{
    // One truncation per path per process: whichever sink is constructed first for a path clears it.
    private static readonly ConcurrentDictionary<string, byte> _cleared = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _filePath;
    private readonly object _writeLock = new();

    public string FilePath => _filePath;

    /// <summary>Derives the failures-file path paired with a cache file
    /// (<c>foo.ndjson</c> → <c>foo.failures.ndjson</c>).</summary>
    public static string PathForCache(string cacheFilePath) =>
        Path.ChangeExtension(cacheFilePath, ".failures.ndjson");

    public FailureSink(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);

        // Clear once per run: truncate on the first construction for this path in the process.
        if (_cleared.TryAdd(filePath, 0))
        {
            lock (_writeLock)
            {
                using var _ = new FileStream(_filePath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
            }
        }
    }

    /// <summary>Appends one failure record as an NDJSON line. Thread-safe.</summary>
    public void Record(FailureRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        string line = JsonSerializer.Serialize(record, SerializerOptions);
        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");
        lock (_writeLock)
            AppendWithRetry(payload);
    }

    private void AppendWithRetry(byte[] payload)
    {
        const int maxAttempts = 8;
        int delayMs = 5;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var fs = new FileStream(
                    _filePath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite,
                    bufferSize: 4096, options: FileOptions.None);
                fs.Write(payload, 0, payload.Length);
                return;
            }
            catch (IOException) when (attempt < maxAttempts)
            {
                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 200);
            }
        }
    }

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact one-line output; NDJSON requires no embedded newlines.
        WriteIndented = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>One failed LLM call. <see cref="AttemptLog"/> holds every attempt's raw reply so a
/// reader can see whether all were empty, or one returned unparseable text.</summary>
/// <param name="Role">"ingest" | "answerer" | "grader".</param>
/// <param name="Id">Question id (judge) or content hash (ingest).</param>
/// <param name="Reason">"empty-completion" | "unparseable" | "transport-error".</param>
/// <param name="Attempts">Attempts made before giving up.</param>
/// <param name="MaxTokens">The call's <c>max_tokens</c> — on an OpenAI-compatible server this bounds
/// the WHOLE request, so a large <see cref="PromptChars"/> against a small budget truncates to empty.</param>
/// <param name="PromptChars">System + user prompt length, to spot prompts that eat the token budget.</param>
public sealed record FailureRecord(
    string Role,
    string Id,
    string Reason,
    int Attempts,
    int MaxTokens,
    int PromptChars,
    string SystemPrompt,
    string UserPrompt,
    IReadOnlyList<FailureAttempt> AttemptLog);

/// <summary>One attempt within a failed call: its raw response (null/empty on a blank completion)
/// and an error string when the attempt threw rather than returning.</summary>
public sealed record FailureAttempt(int N, string? Response, string? Error);
