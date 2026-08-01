using System.Text.Json;
using System.Text.Json.Serialization;
using Scrinia.Retrieval.Benchmarks.Core;

namespace Scrinia.Retrieval.Benchmarks.Loaders;

/// <summary>
/// Loads ConvoMem (Salesforce) evidence-question files into <see cref="BenchmarkQuestion"/>s.
///
/// Layout expected: <paramref name="dataRoot"/> contains one JSON file per category
/// (e.g. <c>user_evidence.json</c>, <c>assistant_facts_evidence.json</c>,
/// <c>preference_evidence.json</c>, <c>changing_evidence.json</c>,
/// <c>abstention_evidence.json</c>, <c>implicit_connection_evidence.json</c>).
/// Each file's root has an <c>evidence_items</c> array; each item has a question, a
/// list of conversations, and a list of message-level evidences. The loader yields
/// one question per item, with the haystack built from every message in the item's
/// conversations and the ground truth being the verbatim evidence texts (substring
/// match handled by <see cref="BenchmarkScorer"/>).
///
/// <para>The published layout nests persona files by evidence count —
/// <c>evidence_questions/&lt;evidence_type&gt;/&lt;N&gt;_evidence/&lt;persona&gt;.json</c>. A
/// per-category limit is split evenly across the <c>N_evidence</c> strata present in each
/// category so the sample spans the difficulty range; a flat cap would be consumed entirely
/// inside <c>1_evidence</c>, since files sort by full path.</para>
/// </summary>
public sealed class ConvoMemLoader
{
    private readonly string _dataRoot;
    private readonly int? _perCategoryLimit;

    public ConvoMemLoader(string dataRoot, int? perCategoryLimit = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataRoot);
        _dataRoot = dataRoot;
        _perCategoryLimit = perCategoryLimit;
    }

    public async IAsyncEnumerable<BenchmarkQuestion> LoadAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        if (!Directory.Exists(_dataRoot))
            throw new DirectoryNotFoundException($"ConvoMem data root not found: {_dataRoot}");

        // Per-category accumulator — the published ConvoMem layout buckets persona
        // files under `evidence_questions/<evidence_type>/<N>_evidence/<persona>.json`,
        // so the meaningful category is the immediate subdir under the data root
        // (e.g. `abstention_evidence`), not the filename. Flat layouts (data root
        // with .json files at top level) still work because the relative path's first
        // segment is just the filename with extension; we strip that and use the bare
        // name as category, matching the original behavior our tests assume.
        var taken = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        string[] allFiles = [.. Directory
            .EnumerateFiles(_dataRoot, "*.json", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.Ordinal)];

        // Stratified quotas. Files sort by full path, so every `1_evidence` file precedes every
        // `2_evidence` file and a flat per-category cap never reaches the harder buckets.
        var quotas = BuildStratumQuotas(allFiles);
        var takenByStratum = new Dictionary<(string Category, string Stratum), int>();

        foreach (string file in allFiles)
        {
            ct.ThrowIfCancellationRequested();
            string category = DeriveCategory(file);
            var stratumKey = (category, DeriveStratum(file));
            if (quotas.TryGetValue(stratumKey, out int stratumCap) && takenByStratum.GetValueOrDefault(stratumKey) >= stratumCap) continue;
            if (_perCategoryLimit is { } pre && taken.GetValueOrDefault(category) >= pre) continue;

            ConvoMemFile? parsed;
            await using (var stream = File.OpenRead(file))
            {
                parsed = await JsonSerializer.DeserializeAsync<ConvoMemFile>(stream, JsonOptions, ct);
            }
            if (parsed?.EvidenceItems is null) continue;

            string fileTag = Path.GetFileNameWithoutExtension(file);
            for (int idx = 0; idx < parsed.EvidenceItems.Count; idx++)
            {
                if (quotas.TryGetValue(stratumKey, out int cap2) && takenByStratum.GetValueOrDefault(stratumKey) >= cap2) break;
                if (_perCategoryLimit is { } cap && taken.GetValueOrDefault(category) >= cap) break;
                var item = parsed.EvidenceItems[idx];
                if (item is null || string.IsNullOrWhiteSpace(item.Question)) continue;

                var haystack = new List<HaystackDoc>();
                int globalMsgIdx = 0;
                if (item.Conversations is not null)
                {
                    for (int c = 0; c < item.Conversations.Count; c++)
                    {
                        var conv = item.Conversations[c];
                        if (conv?.Messages is null) continue;
                        for (int m = 0; m < conv.Messages.Count; m++)
                        {
                            var msg = conv.Messages[m];
                            if (msg is null || string.IsNullOrWhiteSpace(msg.Text)) continue;
                            string docId = $"{fileTag}_{idx}_c{c}_m{globalMsgIdx}";
                            string text = msg.Speaker is null
                                ? msg.Text
                                : $"{msg.Speaker}: {msg.Text}";
                            DateTimeOffset? ts = TryParseTimestamp(msg.Timestamp);
                            haystack.Add(new HaystackDoc(docId, text, ts));
                            globalMsgIdx++;
                        }
                    }
                }

                if (haystack.Count == 0) continue;

                var groundTruthTexts = (item.MessageEvidences ?? [])
                    .Where(e => !string.IsNullOrWhiteSpace(e?.Text))
                    .Select(e => e!.Text!)
                    .ToArray();
                if (groundTruthTexts.Length == 0) continue;

                yield return new BenchmarkQuestion(
                    Id: $"{fileTag}_{idx}",
                    QueryText: item.Question,
                    Haystack: haystack,
                    GroundTruthIds: [],
                    GroundTruthTexts: groundTruthTexts,
                    Category: category);
                taken[category] = taken.GetValueOrDefault(category) + 1;
                takenByStratum[stratumKey] = takenByStratum.GetValueOrDefault(stratumKey) + 1;
            }
        }

        ReportShortfalls(quotas, takenByStratum, taken);
    }

    /// <summary>
    /// Splits <see cref="_perCategoryLimit"/> evenly across the evidence-count strata present in
    /// each category, remainder to the lowest strata. Empty when no limit is set, or for a
    /// category with a single stratum, where the flat cap already applies.
    /// </summary>
    private Dictionary<(string Category, string Stratum), int> BuildStratumQuotas(IReadOnlyList<string> files)
    {
        var quotas = new Dictionary<(string, string), int>();
        if (_perCategoryLimit is not { } limit || limit <= 0)
            return quotas;

        var strataByCategory = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in files)
        {
            string category = DeriveCategory(file);
            if (!strataByCategory.TryGetValue(category, out var strata))
                strataByCategory[category] = strata = new SortedSet<string>(StringComparer.Ordinal);
            strata.Add(DeriveStratum(file));
        }

        foreach (var (category, strata) in strataByCategory)
        {
            if (strata.Count <= 1)
                continue;

            int baseQuota = limit / strata.Count;
            int remainder = limit % strata.Count;
            int i = 0;
            foreach (string stratum in strata)
            {
                quotas[(category, stratum)] = baseQuota + (i < remainder ? 1 : 0);
                i++;
            }
        }

        return quotas;
    }

    /// <summary>
    /// Prints any stratum that could not fill its quota, so a short sample is not mistaken for
    /// a complete one.
    /// </summary>
    private void ReportShortfalls(
        Dictionary<(string Category, string Stratum), int> quotas,
        Dictionary<(string Category, string Stratum), int> takenByStratum,
        Dictionary<string, int> taken)
    {
        if (quotas.Count == 0)
            return;

        foreach (var (key, quota) in quotas.OrderBy(kvp => kvp.Key.Category, StringComparer.Ordinal).ThenBy(kvp => kvp.Key.Stratum, StringComparer.Ordinal))
        {
            int got = takenByStratum.GetValueOrDefault(key);
            if (got < quota)
            {
                Console.Error.WriteLine(
                    $"convomem: {key.Category}/{key.Stratum} yielded {got} of {quota} requested " +
                    "(stratum exhausted); category sample is smaller than the limit.");
            }
        }

        if (_perCategoryLimit is { } limit)
        {
            foreach (var (category, got) in taken.OrderBy(kvp => kvp.Key, StringComparer.Ordinal))
            {
                if (got < limit)
                    Console.Error.WriteLine($"convomem: category '{category}' sampled {got} of {limit}.");
            }
        }
    }

    /// <summary>
    /// The evidence-count bucket a file belongs to (<c>1_evidence</c>, <c>2_evidence</c>, …) —
    /// the segment between the category directory and the persona file. Flat layouts have no
    /// such segment and report a single empty stratum, disabling stratification.
    /// </summary>
    private string DeriveStratum(string file)
    {
        string rel = Path.GetRelativePath(_dataRoot, file);
        string[] segments = rel.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        return segments.Length >= 3 ? segments[1] : string.Empty;
    }

    /// <summary>
    /// Reads the meaningful category for a file. ConvoMem's published structure has
    /// evidence-type subdirs under <c>evidence_questions/</c> — the first relative path
    /// segment is the right grouping. Flat layouts (file directly in data root) fall
    /// back to the filename without extension so test fixtures keep working.
    /// </summary>
    private string DeriveCategory(string file)
    {
        string rel = Path.GetRelativePath(_dataRoot, file);
        int sepIdx = rel.IndexOfAny(['/', '\\']);
        return sepIdx > 0
            ? rel[..sepIdx]
            : Path.GetFileNameWithoutExtension(file);
    }

    private static DateTimeOffset? TryParseTimestamp(string? raw) =>
        DateTimeOffset.TryParse(raw, out var dto) ? dto : null;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private sealed record ConvoMemFile(
        [property: JsonPropertyName("evidence_items")] IReadOnlyList<ConvoMemItem>? EvidenceItems);

    private sealed record ConvoMemItem(
        [property: JsonPropertyName("question")] string? Question,
        [property: JsonPropertyName("answer")] string? Answer,
        [property: JsonPropertyName("conversations")] IReadOnlyList<ConvoMemConversation>? Conversations,
        [property: JsonPropertyName("message_evidences")] IReadOnlyList<ConvoMemEvidence>? MessageEvidences);

    private sealed record ConvoMemConversation(
        [property: JsonPropertyName("messages")] IReadOnlyList<ConvoMemMessage>? Messages);

    private sealed record ConvoMemMessage(
        [property: JsonPropertyName("speaker")] string? Speaker,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("timestamp")] string? Timestamp);

    private sealed record ConvoMemEvidence(
        [property: JsonPropertyName("speaker")] string? Speaker,
        [property: JsonPropertyName("text")] string? Text,
        [property: JsonPropertyName("timestamp")] string? Timestamp);
}
