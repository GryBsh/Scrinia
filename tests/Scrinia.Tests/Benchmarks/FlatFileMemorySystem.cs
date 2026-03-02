using System.Diagnostics;
using System.Text;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Simulates the AGENTS.md / flat-file approach: all knowledge in one string,
/// always fully loaded into context, substring search.
/// Every query pays the full corpus token cost.
/// </summary>
internal sealed class FlatFileMemorySystem : MemorySystemBase
{
    private string _fullContent = "";
    private readonly Dictionary<string, string> _factsByKey = new(StringComparer.OrdinalIgnoreCase);

    public override Task SetupAsync(IReadOnlyList<BenchmarkFact> corpus)
    {
        var sb = new StringBuilder();
        string? currentTopic = null;

        foreach (var fact in corpus.OrderBy(f => f.Topic).ThenBy(f => f.Key))
        {
            if (fact.Topic != currentTopic)
            {
                if (currentTopic is not null) sb.AppendLine();
                sb.AppendLine($"## {fact.Topic}");
                sb.AppendLine();
                currentTopic = fact.Topic;
            }
            sb.AppendLine($"### {fact.Key}");
            sb.AppendLine(fact.Content);
            sb.AppendLine();

            _factsByKey[fact.Key] = fact.Content;
        }

        _fullContent = sb.ToString();
        return Task.CompletedTask;
    }

    public override Task<QueryResult> QueryAsync(string query, string? targetFactKey = null)
    {
        var sw = Stopwatch.StartNew();

        // Flat file is always fully loaded — charge the full content every query
        int charsCost = _fullContent.Length;
        TokensConsumed += CharsToTokens(charsCost);

        // Substring search: split query into terms, find facts containing all terms
        var terms = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var found = new List<string>();

        foreach (var (key, content) in _factsByKey)
        {
            bool match = terms.All(t => content.Contains(t, StringComparison.OrdinalIgnoreCase));
            if (match)
                found.Add(content);
        }

        bool foundTarget = false;
        if (targetFactKey is not null && _factsByKey.TryGetValue(targetFactKey, out var targetContent))
            foundTarget = found.Any(f => f == targetContent);

        sw.Stop();
        return Task.FromResult(new QueryResult(found, CharsToTokens(charsCost), found.Count, foundTarget, sw.Elapsed));
    }

    public override int GetColdStartTokens() => CharsToTokens(_fullContent.Length);

    public override int GetTotalCorpusTokens() => CharsToTokens(_fullContent.Length);

    public override Task UpdateFactAsync(BenchmarkFact updated)
    {
        _factsByKey[updated.Key] = updated.Content;

        // Rebuild full content
        var sb = new StringBuilder();
        string? currentTopic = null;
        foreach (var (key, content) in _factsByKey.OrderBy(kv => kv.Key))
        {
            string topic = key.Split('-')[0];
            if (topic != currentTopic)
            {
                if (currentTopic is not null) sb.AppendLine();
                sb.AppendLine($"## {topic}");
                sb.AppendLine();
                currentTopic = topic;
            }
            sb.AppendLine($"### {key}");
            sb.AppendLine(content);
            sb.AppendLine();
        }
        _fullContent = sb.ToString();
        return Task.CompletedTask;
    }
}
