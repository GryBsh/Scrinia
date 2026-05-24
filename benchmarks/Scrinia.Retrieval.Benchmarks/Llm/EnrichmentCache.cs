using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Scrinia.Retrieval.Benchmarks.Llm;

/// <summary>
/// NDJSON-backed cache for Tier 2 LLM enrichment results. Reruns of the same haystack
/// hit memory; cold reruns load from disk; the per-question Tier 2 sweep of MemBench
/// is otherwise unrepeatable (hundreds of thousands of calls).
///
/// <para><b>File format:</b> one JSON object per line, fields <c>op</c> (e.g.
/// <c>"facts"</c>), <c>hash</c> (SHA256 hex of the input text), <c>value</c> (the
/// LLM result, JSON-typed per op — string, string[], int, or null). Append-only —
/// the bench never rewrites the file. Malformed lines are skipped on load (a
/// truncated last line from a killed process is recoverable).</para>
///
/// <para><b>File naming:</b> one file per <c>(provider, model)</c> tuple, e.g.
/// <c>cache/enrich-claude-cli-sonnet.ndjson</c>. Different providers and models
/// produce different outputs; cross-mixing them would invalidate the comparison.</para>
///
/// <para><b>Null caching:</b> a null LLM result IS cached (as <c>"value": null</c>).
/// Without this, every rerun would re-attempt the same hopeless input. To force a
/// retry, delete the cache file.</para>
///
/// <para><b>Concurrency:</b> the in-memory dict is a <see cref="ConcurrentDictionary{TKey,TValue}"/>.
/// File appends are guarded by a lock so parallel ingest doesn't interleave bytes
/// mid-line. Read-then-write race is benign because the file is append-only — a
/// duplicate line at worst wastes disk; lookup keys collapse it.</para>
/// </summary>
public sealed class EnrichmentCache
{
    private readonly string _filePath;
    private readonly object _writeLock = new();
    private readonly ConcurrentDictionary<string, JsonElement> _entries = new();
    private int _hits;
    private int _misses;
    private int _inflight;

    public int Hits => _hits;
    public int Misses => _misses;
    /// <summary>
    /// Number of times a caller's miss collapsed into an in-flight Tier 2 task
    /// already running for the same content hash. Distinct from disk-cache <see cref="Hits"/>:
    /// the second-through-Nth concurrent callers don't read from NDJSON, but they
    /// also don't trigger an LLM call — they await the first caller's task and
    /// share its result. Diagnostic for "did singleflight fire?" on parallel
    /// cold-cache runs (<c>--ingest-concurrency &gt; 1</c>).
    /// </summary>
    public int InflightHits => _inflight;
    public string FilePath => _filePath;

    /// <summary>Atomically increment the inflight counter. Called by Tier2Enricher
    /// when a singleflight GetOrAdd returns an already-running task.</summary>
    public void RecordInflightHit() => Interlocked.Increment(ref _inflight);

    public EnrichmentCache(string filePath)
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
    /// Counter-free presence check — does NOT increment <see cref="Hits"/> or
    /// <see cref="Misses"/>. Used by callers that need to peek at cache state
    /// (e.g. deciding whether to attempt a combined LLM call) without inflating
    /// the bench's per-run accounting. Real lookups still go through
    /// <see cref="TryGetString"/> / <see cref="TryGetStringArray"/> / <see cref="TryGetInt"/>.
    /// </summary>
    public bool Contains(string op, string hash) => _entries.ContainsKey(Key(op, hash));

    /// <summary>
    /// Look up a string-valued op (description, summary). Returns true on cache hit
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

    /// <summary>Look up a string-array-valued op (facts). Same semantics as <see cref="TryGetString"/>.</summary>
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

    /// <summary>Look up an int-valued op (importance, 1–10).</summary>
    public bool TryGetInt(string op, string hash, out int? value)
    {
        if (_entries.TryGetValue(Key(op, hash), out var el))
        {
            value = el.ValueKind == JsonValueKind.Null ? null : el.GetInt32();
            Interlocked.Increment(ref _hits);
            return true;
        }
        Interlocked.Increment(ref _misses);
        value = null;
        return false;
    }

    public void PutString(string op, string hash, string? value) => Put(op, hash, value);
    public void PutStringArray(string op, string hash, string[]? value) => Put(op, hash, value);
    public void PutInt(string op, string hash, int? value) => Put(op, hash, value);

    private void Put(string op, string hash, object? value)
    {
        // Use anonymous object — JsonSerializer handles the value's runtime type
        // (string, string[], int, null) without needing per-op overloads.
        var entry = new { op, hash, value };
        string line = JsonSerializer.Serialize(entry, SerializerOptions);
        lock (_writeLock)
        {
            // Atomic per-line append: AppendAllText is a single OS write for short
            // payloads. If a concurrent writer is between Open and Write on Windows,
            // FileShare.None inside AppendAllText serializes them.
            File.AppendAllText(_filePath, line + "\n");
            // Update in-memory dict from the just-written JSON so the live shape
            // matches what a fresh Load() would see.
            using var doc = JsonDocument.Parse(line);
            _entries[Key(op, hash)] = doc.RootElement.GetProperty("value").Clone();
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
