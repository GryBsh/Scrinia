using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Scrinia.Retrieval.Benchmarks.Llm;

namespace Scrinia.Retrieval.Benchmarks.Agent;

/// <summary>
/// NDJSON-backed cache of agentic-ingest episode tool-call sequences, keyed by
/// <c>(model, promptVersion, session transcript)</c>. Reruns of the same haystack
/// replay the recorded <see cref="EpisodeToolCall"/> sequence via
/// <see cref="AgenticIngestHarness.ReplayAsync"/> instead of re-running the LLM —
/// the per-question agentic-ingest sweep is otherwise unrepeatable (one LLM episode
/// per session, across hundreds of benchmark questions).
///
/// <para>Modeled on <see cref="LlmResponseCache"/>: append-only NDJSON, one JSON
/// object per line, <c>FileShare.ReadWrite</c> append so cross-process bench runs
/// don't corrupt each other's writes, <see cref="Load"/> on construction, and
/// malformed lines are skipped (a truncated tail from a killed process is
/// recoverable).</para>
///
/// <para><b>Never call <see cref="Put"/> for a failed or fallback episode</b> — only
/// <c>Succeeded == true</c> LLM episodes are cacheable. Caching a failure would
/// replay nothing forever, silently starving the store of memories on every rerun.</para>
/// </summary>
public sealed class EpisodeCache
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, IReadOnlyList<EpisodeToolCall>> _entries = new();
    private int _hits;
    private int _misses;

    public int Hits => _hits;
    public int Misses => _misses;

    public EpisodeCache(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        string? dir = Path.GetDirectoryName(filePath);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);
        Load();
    }

    /// <summary>
    /// Cache key = hash(model + promptVersion + full session transcript). Bumping
    /// <see cref="AgenticIngestPrompts.PromptVersion"/> invalidates every cached
    /// episode without touching this method.
    /// </summary>
    public static string Key(string model, string promptVersion, string transcript) =>
        LlmResponseCache.Hash(model + promptVersion + transcript);

    public bool TryGet(string key, out IReadOnlyList<EpisodeToolCall> calls)
    {
        if (_entries.TryGetValue(key, out var found))
        {
            calls = found;
            Interlocked.Increment(ref _hits);
            return true;
        }
        Interlocked.Increment(ref _misses);
        calls = [];
        return false;
    }

    /// <summary>Records a successful episode's tool-call sequence. NEVER call for a failed/fallback episode.</summary>
    public void Put(string key, IReadOnlyList<EpisodeToolCall> calls)
    {
        var dto = new LineDto(key, [.. calls.Select(c => new CallDto(c.ToolName, c.ArgumentsJson))]);
        string line = JsonSerializer.Serialize(dto, SerializerOptions);
        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");

        lock (_writeLock)
        {
            AppendWithRetry(payload);
            _entries[key] = calls;
        }
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
                    _filePath,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: 4096,
                    options: FileOptions.None);
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

    private void Load()
    {
        if (!File.Exists(_filePath)) return;
        foreach (string line in File.ReadLines(_filePath))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var dto = JsonSerializer.Deserialize<LineDto>(line, SerializerOptions);
                if (dto?.key is null || dto.calls is null) continue;
                _entries[dto.key] = [.. dto.calls.Select(c => new EpisodeToolCall(c.tool, c.args))];
            }
            catch (JsonException)
            {
                // Skip malformed lines (truncated tail from a killed process, manual
                // edits, format drift). The cache is best-effort.
            }
        }
    }

    private sealed record CallDto(string tool, string args);
    private sealed record LineDto(string key, List<CallDto> calls);

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };
}
