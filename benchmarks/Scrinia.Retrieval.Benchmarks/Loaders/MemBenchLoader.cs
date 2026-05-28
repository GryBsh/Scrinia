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
/// <c>post_processing.json</c>, <c>RecMultiSession.json</c>). Inside each file the
/// root is a dictionary keyed by topic (<c>movie</c>, <c>roles</c>,
/// <c>multi_agent</c>, etc.); each topic value is an array of items. Each item has
/// a <c>tid</c>, a <c>message_list</c> (array of sessions, each a list of turns),
/// and a <c>QA</c> block. Ground truth comes from <c>QA.target_step_id</c> — a list
/// of inner lists; we take the first element of each inner list as a turn id (matches
/// MemPalace's published harness).</para>
///
/// <para><b>Dual schema:</b> MemBench ships two turn shapes and the loader supports
/// both, matching MemPalace's upstream fallback (<c>sid = turn.get("sid",
/// turn.get("mid"))</c>; <c>user = turn.get("user") or turn.get("user_message",
/// "")</c>):
/// <list type="bullet">
/// <item><description><b>roles</b>-style files (simple, noisy, aggregative,
/// comparative, conditional, knowledge_update, post_processing): turns have
/// <c>sid</c>, <c>user_message</c>, <c>assistant_message</c>.</description></item>
/// <item><description><b>movie / multi_agent</b>-style files (highlevel,
/// highlevel_rec, lowlevel_rec, RecMultiSession): turns have <c>mid</c>,
/// <c>user</c>, <c>assistant</c>.</description></item>
/// </list>
/// An earlier version of this loader only read the <c>roles</c> shape, which made
/// every <c>movie/multi_agent</c> turn collide on doc ID 0 (default int) and zeroed
/// out R@K for any category whose gold targets didn't happen to include 0 — and
/// silently emptied the doc text for the <c>roles</c> categories too. Fixed by
/// accepting either field name on both the id and the text.</para>
///
/// <para>Per-question haystack = every turn in the item's <c>message_list</c>. Doc IDs
/// are the turn's <c>sid</c>/<c>mid</c> formatted as a string so they round-trip
/// against <c>target_step_id</c>.</para>
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
                int? id = turn.Sid ?? turn.Mid;
                if (id is null) continue;
                string text = FormatTurn(turn);
                if (string.IsNullOrWhiteSpace(text)) continue;
                docs.Add(new HaystackDoc(
                    Id: id.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    Text: text,
                    Timestamp: TryParseTimestamp(turn.Time)));
            }
        }
        return docs;
    }

    /// <summary>
    /// Renders one MemBench turn into searchable doc text. Includes user + assistant
    /// content plus an optional timestamp prefix — matches MemPalace's loader format
    /// so vocabulary overlap with the published comparison stays clean. Accepts both
    /// schemas: <c>user</c>/<c>assistant</c> (movie, multi_agent) and
    /// <c>user_message</c>/<c>assistant_message</c> (roles).
    /// </summary>
    private static string FormatTurn(MemBenchTurn turn)
    {
        string? userText = !string.IsNullOrWhiteSpace(turn.User) ? turn.User : turn.UserMessage;
        string? asstText = !string.IsNullOrWhiteSpace(turn.Assistant) ? turn.Assistant : turn.AssistantMessage;

        var sb = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(turn.Time))
            sb.Append('[').Append(turn.Time).Append("] ");
        if (!string.IsNullOrWhiteSpace(userText))
            sb.Append("[User] ").Append(userText);
        if (!string.IsNullOrWhiteSpace(asstText))
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append("[Assistant] ").Append(asstText);
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

    // Nullable Sid/Mid so we can detect which schema this turn used and choose the
    // present one. Defaulting to 0 would re-create the bug fixed in this commit.
    private sealed record MemBenchTurn(
        [property: JsonPropertyName("sid")] int? Sid,
        [property: JsonPropertyName("mid")] int? Mid,
        [property: JsonPropertyName("user")] string? User,
        [property: JsonPropertyName("user_message")] string? UserMessage,
        [property: JsonPropertyName("assistant")] string? Assistant,
        [property: JsonPropertyName("assistant_message")] string? AssistantMessage,
        [property: JsonPropertyName("time")] string? Time);

    // `answer` is intentionally omitted — System.Text.Json skips unmapped fields, so we
    // sidestep the fact that MemBench ships `answer` as either a string or an object
    // (e.g. `{"A": "...", "B": "..."}` for multiple-choice categories like multi_agent).
    // Retrieval scoring only uses target_step_id, never the answer.
    private sealed record MemBenchQa(
        [property: JsonPropertyName("question")] string? Question,
        [property: JsonPropertyName("target_step_id")] IReadOnlyList<IReadOnlyList<int>?>? TargetStepId);
}
