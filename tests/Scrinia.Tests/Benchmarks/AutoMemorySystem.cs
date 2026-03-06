using System.Diagnostics;
using System.Text;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Simulates the Claude auto-memory approach: a 200-line index always loaded,
/// plus per-topic files loaded on demand based on query routing.
/// </summary>
internal sealed class AutoMemorySystem : MemorySystemBase
{
    private string _indexContent = "";
    private readonly Dictionary<string, string> _topicFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _factsByTopic = new(StringComparer.OrdinalIgnoreCase);

    public override Task SetupAsync(IReadOnlyList<BenchmarkFact> corpus)
    {
        // Group facts by topic
        var grouped = corpus.GroupBy(f => f.Topic).OrderBy(g => g.Key);

        // Build per-topic files
        foreach (var group in grouped)
        {
            var sb = new StringBuilder();
            var facts = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            sb.AppendLine($"# {group.Key} knowledge");
            sb.AppendLine();
            foreach (var fact in group)
            {
                sb.AppendLine($"## {fact.Key}");
                sb.AppendLine(fact.Content);
                sb.AppendLine();
                facts[fact.Key] = fact.Content;
            }
            _topicFiles[group.Key] = sb.ToString();
            _factsByTopic[group.Key] = facts;
        }

        // Build 200-line index (topic names + first 3 keys per topic + summaries)
        var indexSb = new StringBuilder();
        indexSb.AppendLine("# Memory Index");
        indexSb.AppendLine();
        foreach (var group in grouped)
        {
            indexSb.AppendLine($"## {group.Key}");
            indexSb.AppendLine($"Topic file: {group.Key}.md ({group.Count()} entries)");
            foreach (var fact in group.Take(3))
                indexSb.AppendLine($"  - {fact.Key}: {fact.Content[..Math.Min(80, fact.Content.Length)]}...");
            indexSb.AppendLine();
        }

        // Pad to ensure the index is meaningful but capped
        _indexContent = indexSb.ToString();
        return Task.CompletedTask;
    }

    public override Task<QueryResult> QueryAsync(string query, string? targetFactKey = null)
    {
        var sw = Stopwatch.StartNew();

        // Always charge the index
        int charsCost = _indexContent.Length;

        // Route to topic by checking if query terms match any topic name
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var matchedTopics = new List<string>();

        foreach (var topic in _topicFiles.Keys)
        {
            if (terms.Any(t => topic.Contains(t, StringComparison.OrdinalIgnoreCase) ||
                               t.Contains(topic, StringComparison.OrdinalIgnoreCase)))
            {
                matchedTopics.Add(topic);
            }
        }

        // Also check index content for topic routing hints
        if (matchedTopics.Count == 0)
        {
            foreach (var topic in _topicFiles.Keys)
            {
                // Check if any query term appears in the index section for this topic
                if (terms.Any(t => _indexContent.Contains(t, StringComparison.OrdinalIgnoreCase)))
                {
                    matchedTopics.Add(topic);
                    break; // Only load one topic as best guess
                }
            }
        }

        // Fallback: load ALL topic files if no routing match
        if (matchedTopics.Count == 0)
            matchedTopics.AddRange(_topicFiles.Keys);

        // Charge for loaded topic files
        foreach (var topic in matchedTopics)
            charsCost += _topicFiles[topic].Length;

        TokensConsumed += CharsToTokens(charsCost);

        // Search within loaded topics
        var found = new List<string>();
        foreach (var topic in matchedTopics)
        {
            if (!_factsByTopic.TryGetValue(topic, out var facts)) continue;
            foreach (var (_, content) in facts)
            {
                if (terms.All(t => content.Contains(t, StringComparison.OrdinalIgnoreCase)))
                    found.Add(content);
            }
        }

        bool foundTarget = false;
        if (targetFactKey is not null)
        {
            string targetTopic = targetFactKey.Split('-')[0];
            if (_factsByTopic.TryGetValue(targetTopic, out var topicFacts) &&
                topicFacts.TryGetValue(targetFactKey, out var targetContent))
            {
                foundTarget = found.Any(f => f == targetContent);
            }
        }

        sw.Stop();
        return Task.FromResult(new QueryResult(found, CharsToTokens(charsCost), found.Count, foundTarget, sw.Elapsed));
    }

    public override Task<(string Context, int TokensCost)> GetLlmContextAsync(string query)
    {
        // Always charge index
        var sb = new StringBuilder(_indexContent);
        int charsCost = _indexContent.Length;

        // Route to topic files (same logic as QueryAsync)
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matchedTopics = _topicFiles.Keys
            .Where(topic => terms.Any(t => topic.Contains(t, StringComparison.OrdinalIgnoreCase)
                || _indexContent.Contains(t, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        if (matchedTopics.Count == 0)
            matchedTopics = _topicFiles.Keys.ToList(); // fallback: load all

        foreach (var topic in matchedTopics)
        {
            sb.AppendLine().Append(_topicFiles[topic]);
            charsCost += _topicFiles[topic].Length;
        }

        int cost = CharsToTokens(charsCost);
        TokensConsumed += cost;
        return Task.FromResult((sb.ToString(), cost));
    }

    public override int GetColdStartTokens() => CharsToTokens(_indexContent.Length);

    public override int GetTotalCorpusTokens()
    {
        int total = _indexContent.Length;
        foreach (var file in _topicFiles.Values)
            total += file.Length;
        return CharsToTokens(total);
    }

    public override Task UpdateFactAsync(BenchmarkFact updated)
    {
        if (!_factsByTopic.TryGetValue(updated.Topic, out var facts))
            return Task.CompletedTask;

        facts[updated.Key] = updated.Content;

        // Rebuild topic file
        var sb = new StringBuilder();
        sb.AppendLine($"# {updated.Topic} knowledge");
        sb.AppendLine();
        foreach (var (key, content) in facts.OrderBy(kv => kv.Key))
        {
            sb.AppendLine($"## {key}");
            sb.AppendLine(content);
            sb.AppendLine();
        }
        _topicFiles[updated.Topic] = sb.ToString();
        return Task.CompletedTask;
    }
}
