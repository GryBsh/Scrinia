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

        foreach (string file in Directory.EnumerateFiles(_dataRoot, "*.json", SearchOption.AllDirectories).OrderBy(f => f, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested();
            string category = Path.GetFileNameWithoutExtension(file);
            ConvoMemFile? parsed;
            await using (var stream = File.OpenRead(file))
            {
                parsed = await JsonSerializer.DeserializeAsync<ConvoMemFile>(stream, JsonOptions, ct);
            }
            if (parsed?.EvidenceItems is null) continue;

            int taken = 0;
            for (int idx = 0; idx < parsed.EvidenceItems.Count; idx++)
            {
                if (_perCategoryLimit is { } cap && taken >= cap) break;
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
                            string docId = $"{category}_{idx}_c{c}_m{globalMsgIdx}";
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
                    Id: $"{category}_{idx}",
                    QueryText: item.Question,
                    Haystack: haystack,
                    GroundTruthIds: [],
                    GroundTruthTexts: groundTruthTexts,
                    Category: category);
                taken++;
            }
        }
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
