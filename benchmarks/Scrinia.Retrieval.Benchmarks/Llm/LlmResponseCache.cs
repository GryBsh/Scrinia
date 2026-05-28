using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrinia.Retrieval.Benchmarks.Llm;

/// <summary>
/// NDJSON-backed cache for LLM responses produced by the bench's agent harness. Reruns
/// of the same haystack / question set hit memory; cold reruns load from disk; the
/// per-question agent sweep is otherwise unrepeatable (hundreds of thousands of calls
/// across the larger benchmarks).
///
/// <para><b>File format:</b> one JSON object per line, fields <c>op</c> (e.g.
/// <c>"anchors"</c>), <c>hash</c> (SHA256 hex of the input text), <c>value</c> (the
/// LLM result, JSON-typed per op — string, string[], int, or null). Append-only —
/// the bench never rewrites the file. Malformed lines are skipped on load (a
/// truncated last line from a killed process is recoverable).</para>
///
/// <para><b>File naming:</b> one file per <c>(provider, model)</c> tuple, e.g.
/// <c>cache/agent-claude-cli-sonnet.ndjson</c>. Different providers and models
/// produce different outputs; cross-mixing them would invalidate the comparison.</para>
///
/// <para><b>Null caching:</b> a null LLM result IS cached (as <c>"value": null</c>).
/// Without this, every rerun would re-attempt the same hopeless input. To force a
/// retry, delete the cache file.</para>
///
/// <para><b>Concurrency:</b> the in-memory dict is a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// Within a single process, appends are guarded by <c>_writeLock</c>. Across
/// processes (the parallel-categories bench mode spawns one subprocess per
/// category, all sharing the same provider+model cache file), the file is opened
/// with <c>FileShare.ReadWrite</c> and <c>FileMode.Append</c>; on Windows this
/// maps to <c>FILE_APPEND_DATA</c>, which makes each write atomic at EOF, so two
/// concurrent appenders can't interleave bytes within a line. Read-then-write
/// race is benign because the file is append-only — duplicate lines collapse on
/// the next <see cref="Load"/>.</para>
/// </summary>
public sealed class LlmResponseCache
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, JsonElement> _entries = new();
    private int _hits;
    private int _misses;

    public int Hits => _hits;
    public int Misses => _misses;
    public string FilePath => _filePath;

    public LlmResponseCache(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        Load();
    }

    /// <summary>
    /// SHA256 of the input text (hex, lowercase). Used as the cache key so repeated
    /// calls on byte-identical content hit memory. Any whitespace change invalidates;
    /// the bench datasets are static so that's acceptable.
    /// </summary>
    public static string Hash(string text)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexStringLower(bytes);
    }

    /// <summary>
    /// Look up a string-valued op (rewrite, hyde). Returns true on cache hit
    /// (with <paramref name="value"/> possibly null if the cached result was null);
    /// false on miss. Increments the corresponding counter.
    /// </summary>
    public bool TryGetString(string op, string hash, out string? value)
    {
        if (_entries.TryGetValue(Key(op, hash), out var el))
        {
            value = el.ValueKind == JsonValueKind.Null ? null : el.GetString();
            Interlocked.Increment(ref _hits);
            return true;
        }
        Interlocked.Increment(ref _misses);
        value = null;
        return false;
    }

    /// <summary>Look up a string-array-valued op (anchors). Same semantics as <see cref="TryGetString"/>.</summary>
    public bool TryGetStringArray(string op, string hash, out string[]? value)
    {
        if (_entries.TryGetValue(Key(op, hash), out var el))
        {
            if (el.ValueKind == JsonValueKind.Null)
                value = null;
            else
            {
                var list = new List<string>(el.GetArrayLength());
                foreach (var item in el.EnumerateArray())
                    if (item.GetString() is { } s) list.Add(s);
                value = list.ToArray();
            }
            Interlocked.Increment(ref _hits);
            return true;
        }
        Interlocked.Increment(ref _misses);
        value = null;
        return false;
    }

    public void PutString(string op, string hash, string? value) => Put(op, hash, value);
    public void PutStringArray(string op, string hash, string[]? value) => Put(op, hash, value);

    private void Put(string op, string hash, object? value)
    {
        // Use anonymous object — JsonSerializer handles the value's runtime type
        // (string, string[], null) without needing per-op overloads.
        var entry = new { op, hash, value };
        string line = JsonSerializer.Serialize(entry, SerializerOptions);
        byte[] payload = Encoding.UTF8.GetBytes(line + "\n");

        lock (_writeLock)
        {
            // FileMode.Append + FileShare.ReadWrite + a brief retry handles two
            // concurrency layers: in-process callers serialize via _writeLock above,
            // and cross-process appenders (parallel bench subprocesses) rely on
            // Windows FILE_APPEND_DATA atomicity for the actual write. The retry
            // window catches the transient sharing-violation that occurs if another
            // process is between CreateFile and the first WriteFile.
            AppendWithRetry(payload);

            // Update in-memory dict from the just-written JSON so the live shape
            // matches what a fresh Load() would see.
            using var doc = JsonDocument.Parse(line);
            _entries[Key(op, hash)] = doc.RootElement.GetProperty("value").Clone();
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
                using var doc = JsonDocument.Parse(line);
                string? op = doc.RootElement.GetProperty("op").GetString();
                string? hash = doc.RootElement.GetProperty("hash").GetString();
                if (op is null || hash is null) continue;
                _entries[Key(op, hash)] = doc.RootElement.GetProperty("value").Clone();
            }
            catch (JsonException)
            {
                // Skip malformed lines (truncated tail from a killed process, manual
                // edits, format drift). The cache is best-effort.
            }
            catch (KeyNotFoundException)
            {
                // Entry without required fields — skip.
            }
        }
    }

    private static string Key(string op, string hash) => $"{op}|{hash}";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        // Compact one-line output; NDJSON requires no embedded newlines.
        WriteIndented = false,
    };
}
