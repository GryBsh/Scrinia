using System.Diagnostics;
using Scrinia.Mcp;

namespace Scrinia.Tests.Benchmarks;

/// <summary>
/// Wraps real <see cref="ScriniaMcpTools"/> with <see cref="TestHelpers.StoreScope"/>
/// for isolated benchmark testing. Uses actual NMP/2 encoding, BM25 search, and chunked retrieval.
/// </summary>
internal sealed class ScriniaMemorySystem : MemorySystemBase
{
    private readonly TestHelpers.StoreScope _scope = new();
    private readonly ScriniaMcpTools _tools = new();

    public override async Task SetupAsync(IReadOnlyList<BenchmarkFact> corpus)
    {
        foreach (var fact in corpus)
        {
            string name = $"/{fact.Topic}/{fact.Key}";
            McpResponse response = await _tools.Remember(
                [fact.Content],
                name,
                description: fact.Question,
                keywords: fact.UniqueTerms);
            ThrowIfWriteFailed(response, name);
        }
    }

    /// <summary>
    /// Surfaces a failed write instead of letting it pass silently. A discarded error turns an
    /// infrastructure failure (lock timeout under load) into a misleading correctness verdict —
    /// the memory keeps its previous content and the benchmark reports it as stale data.
    /// </summary>
    private static void ThrowIfWriteFailed(McpResponse response, string name)
    {
        if (response.Status == "error")
            throw new InvalidOperationException($"Write to '{name}' failed:\n{response.Error}");
    }

    public override async Task<QueryResult> QueryAsync(string query, string? targetFactKey = null)
    {
        var sw = Stopwatch.StartNew();
        int charsConsumed = 0;

        // Search
        string searchResult = (await _tools.Search(query, limit: 10)).ToYaml();
        charsConsumed += searchResult.Length;

        var foundContent = new List<string>();
        bool foundTarget = false;

        if (searchResult != "No matching memories found.")
        {
            // Parse the search result table to extract memory names
            var names = ParseSearchResultNames(searchResult);

            // Show the top result to simulate actual retrieval
            if (names.Count > 0)
            {
                McpResponse recalled = await _tools.Recall(names[0]);
                string showResult = recalled.ToYaml();
                charsConsumed += showResult.Length;
                if (recalled.Status != "error")
                    foundContent.Add(showResult);

                // Check if target is in the results
                if (targetFactKey is not null)
                {
                    string targetName = $"/{targetFactKey.Split('-')[0]}/{targetFactKey}";
                    foundTarget = names.Any(n => n.Equals(targetName, StringComparison.OrdinalIgnoreCase));
                }
            }
        }

        int tokens = CharsToTokens(charsConsumed);
        TokensConsumed += tokens;

        sw.Stop();
        return new QueryResult(foundContent, tokens, foundContent.Count, foundTarget, sw.Elapsed);
    }

    /// <summary>Raw search output, for diagnosing an unexpected hit.</summary>
    public async Task<string> RawSearchAsync(string query) => (await _tools.Search(query, limit: 10)).ToYaml();

    /// <summary>Raw stored content, for checking whether a hit reflects old content.</summary>
    public async Task<string> RawRecallAsync(string name) => (await _tools.Recall(name)).ToYaml();

    public override int GetColdStartTokens() => 0; // Nothing loaded until queried

    public override int GetTotalCorpusTokens()
    {
        // Would need to show() every memory — estimate from setup
        // Use the corpus size as a proxy
        return 0; // Scrinia never loads everything at once
    }

    public override async Task UpdateFactAsync(BenchmarkFact updated)
    {
        string name = $"/{updated.Topic}/{updated.Key}";
        McpResponse response = await _tools.Remember(
            [updated.Content],
            name,
            description: updated.Question,
            keywords: updated.UniqueTerms);
        ThrowIfWriteFailed(response, name);
    }

    /// <summary>
    /// Parses the formatted search result table to extract qualified memory names.
    /// Table format: type  name  score  ~tokens  description
    /// </summary>
    internal static List<string> ParseSearchResultNames(string searchOutput)
    {
        var names = new List<string>();
        var lines = searchOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        // Skip header line and separator line
        for (int i = 2; i < lines.Length; i++)
        {
            var line = lines[i].Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('-')) continue;

            // Split by 2+ spaces to get columns
            var parts = System.Text.RegularExpressions.Regex.Split(line, @"\s{2,}");
            if (parts.Length >= 2)
            {
                string name = parts[1].Trim();
                // Strip chunk suffix if present: "name [chunk 1/3]" -> "name"
                int bracketIdx = name.IndexOf('[');
                if (bracketIdx > 0)
                    name = name[..bracketIdx].Trim();
                if (name.Length > 0)
                    names.Add(name);
            }
        }

        return names;
    }

    public override ValueTask DisposeAsync()
    {
        _scope.Dispose();
        return ValueTask.CompletedTask;
    }
}
