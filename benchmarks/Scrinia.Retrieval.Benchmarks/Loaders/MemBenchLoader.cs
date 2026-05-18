using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Loaders;

/// <summary>
/// Loads MemBench (ACL 2025, <c>import-myself/Membench</c>) into
/// <see cref="BenchmarkQuestion"/>s.
///
/// <para>Layout: <paramref name="dataRoot"/> contains one JSON file per category
/// (<c>simple.json</c>, <c>highlevel.json</c>, <c>knowledge_update.json</c>,
/// <c>comparative.json</c>, <c>conditional.json</c>, <c>noisy.json</c>,
/// <c>aggregative.json</c>, <c>highlevel_rec.json</c>, <c>lowlevel_rec.json</c>,
/// <c>post_processing.json</c>). Inside each file the root is a dictionary keyed by
/// topic (<c>movie</c>, <c>food</c>, etc.); each topic value is an array of items.
/// Each item has a <c>tid</c>, a <c>message_list</c> (array of sessions, each a list
/// of turns with <c>sid</c>, <c>user</c>, <c>assistant</c>, optional <c>time</c>),
/// and a <c>QA</c> block. Ground truth comes from <c>QA.target_step_id</c> — a list
/// of inner lists; we take the first element of each inner list as a turn-sid (matches
/// MemPalace's published harness).</para>
///
/// <para>Per-question haystack = every turn in the item's <c>message_list</c>. Doc IDs
/// are the turn's <c>sid</c> formatted as a string so they round-trip against
/// <c>target_step_id</c>.</para>
/// </summary>
public sealed class MemBenchLoader
{
    private readonly string _dataRoot;
    private readonly int? _perCategoryLimit;

    public MemBenchLoader(string dataRoot, int? perCategoryLimit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = dataRoot;
        _perCategoryLimit = perCategoryLimit;
    }

    public async IAsyncEnumerable<BenchmarkQuestion> LoadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(_dataRoot))
            throw new DirectoryNotFoundException($"MemBench data root not found: {_dataRoot}");

        foreach (string file in Directory.EnumerateFiles(_dataRoot, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string category = Path.GetFileNameWithoutExtension(file);

            // The root is an object keyed by topic — parse as a generic dictionary of arrays.
            Dictionary<string, List<MemBenchItem>>? parsed;
            await using (var stream = File.OpenRead(file))
            {
                parsed = await JsonSerializer.DeserializeAsync<Dictionary<string, List<MemBenchItem>>>(stream, JsonOptions, ct);
            }
            if (parsed is null) continue;

            int taken = 0;
            foreach (var (topic, items) in parsed)
            {
                if (items is null) continue;
                foreach (var item in items)
                {
                    if (_perCategoryLimit is { } cap && taken >= cap) break;
                    if (item?.QA is null || string.IsNullOrWhiteSpace(item.QA.Question)) continue;

                    var haystack = BuildHaystack(item);
                    if (haystack.Count == 0) continue;

                    var groundTruthIds = (item.QA.TargetStepId ?? [])
                        .Where(inner => inner is not null && inner.Count > 0)
                        .Select(inner => inner![0].ToString(System.Globalization.CultureInfo.InvariantCulture))
                        .ToArray();
                    if (groundTruthIds.Length == 0) continue;

                    yield return new BenchmarkQuestion(
                        Id: $"{category}_{topic}_{item.Tid}",
                        QueryText: item.QA.Question,
                        Haystack: haystack,
                        GroundTruthIds: groundTruthIds,
                        GroundTruthTexts: [],
                        Category: category);
                    taken++;
                }
            }
        }
    }

    private static List<HaystackDoc> BuildHaystack(MemBenchItem item)
    {
        var docs = new List<HaystackDoc>();
        if (item.MessageList is null) return docs;

        foreach (var session in item.MessageList)
        {
            if (session is null) continue;
            foreach (var turn in session)
            {
                if (turn is null) continue;
                string text = FormatTurn(turn);
                if (string.IsNullOrWhiteSpace(text)) continue;
                docs.Add(new HaystackDoc(
                    Id: turn.Sid.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Text: text,
                    Timestamp: TryParseTimestamp(turn.Time)));
            }
        }
        return docs;
    }

    /// <summary>
    /// Renders one MemBench turn into searchable doc text. Includes user + assistant
    /// content plus an optional timestamp prefix — matches MemPalace's loader format
    /// so vocabulary overlap with the published comparison stays clean.
    /// </summary>
    private static string FormatTurn(MemBenchTurn turn)
    {
        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(turn.Time))
            sb.Append('[').Append(turn.Time).Append("] ");
        if (!string.IsNullOrWhiteSpace(turn.User))
            sb.Append("[User] ").Append(turn.User);
        if (!string.IsNullOrWhiteSpace(turn.Assistant))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("[Assistant] ").Append(turn.Assistant);
        }
        return sb.ToString();
    }

    private static DateTimeOffset? TryParseTimestamp(string? raw) =>
        DateTimeOffset.TryParse(raw, out var dto) ? dto : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record MemBenchItem(
        [property: JsonPropertyName("tid")] int Tid,
        [property: JsonPropertyName("message_list")] IReadOnlyList<IReadOnlyList<MemBenchTurn>>? MessageList,
        [property: JsonPropertyName("QA")] MemBenchQa? QA);

    private sealed record MemBenchTurn(
        [property: JsonPropertyName("sid")] int Sid,
        [property: JsonPropertyName("user")] string? User,
        [property: JsonPropertyName("assistant")] string? Assistant,
        [property: JsonPropertyName("time")] string? Time);

    // `answer` is intentionally omitted — System.Text.Json skips unmapped fields, so we
    // sidestep the fact that MemBench ships `answer` as either a string or an object
    // (e.g. `{"A": "...", "B": "..."}` for multiple-choice categories like multi_agent).
    // Retrieval scoring only uses target_step_id, never the answer.
    private sealed record MemBenchQa(
        [property: JsonPropertyName("question")] string? Question,
        [property: JsonPropertyName("target_step_id")] IReadOnlyList<IReadOnlyList<int>?>? TargetStepId);
}
