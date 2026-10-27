using Scrinia.Core.Configuration;
using Scrinia.Core.Models;

namespace Scrinia.Core.Search;

/// <summary>Configuration for the bounded lexical candidate snapshot used by retrieval.</summary>
public sealed record SearchCandidateOptions
{
    public const string ConfigurationKey = "Scrinia:Search:CandidateLimit";
    public const int DefaultCandidateLimit = 200;
    public const int MaximumCandidateLimit = 1000;

    public SearchCandidateOptions(int candidateLimit)
    {
        if (candidateLimit is < 1 or > MaximumCandidateLimit)
            throw new ScriniaConfigurationException(
                "Scrinia:Search:CandidateLimit must be an integer from 1 through 1000.");

        CandidateLimit = candidateLimit;
    }

    public int CandidateLimit { get; }

    private static readonly AsyncLocal<SearchCandidateOptions?> CurrentOptions = new();
    private static SearchCandidateOptions _default = new(DefaultCandidateLimit);

    public static SearchCandidateOptions Current
    {
        get => CurrentOptions.Value ?? _default;
        set => CurrentOptions.Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static SearchCandidateOptions Default
    {
        get => _default;
        set => _default = value ?? throw new ArgumentNullException(nameof(value));
    }

    public static SearchCandidateOptions Parse(string? value)
    {
        if (value is null)
            return new(DefaultCandidateLimit);

        if (int.TryParse(value, out int limit) && limit is >= 1 and <= MaximumCandidateLimit)
            return new(limit);

        throw new ScriniaConfigurationException(
            "Scrinia:Search:CandidateLimit must be an integer from 1 through 1000.");
    }

    /// <summary>Ranks a materialized corpus deterministically before downstream work begins.</summary>
    public static IReadOnlyList<ScopedArtifact> RankLexically(
        IEnumerable<ScopedArtifact> candidates, string query)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        var queryTokens = TextAnalysis.Tokenize(query).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return candidates
            .Select((candidate, index) => new RankedCandidate(candidate, LexicalScore(candidate, queryTokens), index))
            .Order(LexicalOrder)
            .Select(item => item.Candidate)
            .ToArray();
    }

    internal readonly record struct RankedCandidate(ScopedArtifact Candidate, int Score, long Order);

    internal static readonly IComparer<RankedCandidate> LexicalOrder = Comparer<RankedCandidate>.Create((left, right) =>
    {
        int order = right.Score.CompareTo(left.Score);
        if (order == 0) order = StringComparer.OrdinalIgnoreCase.Compare(left.Candidate.Scope, right.Candidate.Scope);
        if (order == 0) order = StringComparer.OrdinalIgnoreCase.Compare(left.Candidate.Entry.Name, right.Candidate.Entry.Name);
        if (order == 0) order = StringComparer.Ordinal.Compare(left.Candidate.Entry.Address.ToString(), right.Candidate.Entry.Address.ToString());
        return order == 0 ? left.Order.CompareTo(right.Order) : order;
    });

    internal static int LexicalScore(ScopedArtifact candidate, HashSet<string> queryTokens)
    {
        if (queryTokens.Count == 0)
            return 0;

        int score = 0;
        foreach (string token in queryTokens)
        {
            if (candidate.Entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase))
                score += 8;
            if (candidate.Entry.Keywords?.Any(keyword =>
                    keyword.Contains(token, StringComparison.OrdinalIgnoreCase)) == true)
                score += 4;
            if (candidate.Entry.Attributes?.Values.Any(values =>
                    values.Any(value => value.Contains(token, StringComparison.OrdinalIgnoreCase))) == true)
                score += 3;
            if (candidate.Entry.TermFrequencies?.ContainsKey(token) == true)
                score += 2;
            if (candidate.Entry.ContentPreview?.Contains(token, StringComparison.OrdinalIgnoreCase) == true)
                score++;
        }

        return score;
    }
}
