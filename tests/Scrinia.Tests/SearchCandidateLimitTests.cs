using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public sealed class SearchCandidateLimitTests
{
    [Fact]
    public void Parse_AbsentValue_UsesDefault()
    {
        SearchCandidateOptions.Parse(null).CandidateLimit.Should()
            .Be(SearchCandidateOptions.DefaultCandidateLimit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("not-a-number")]
    public void Parse_InvalidValue_ThrowsConfigurationFailure(string value)
    {
        Action parse = () => SearchCandidateOptions.Parse(value);

        parse.Should().Throw<Exception>()
            .Which.Message.Should().Contain("CandidateLimit");
    }

    [Fact]
    public void RankLexically_UsesStableScoreThenIdentityOrdering()
    {
        var candidates = new[]
        {
            Candidate("zeta", "unrelated"),
            Candidate("alpha", "alpha details"),
            Candidate("beta", "alpha details"),
        };

        SearchCandidateOptions.RankLexically(candidates, "alpha")
            .Select(candidate => candidate.Entry.Name)
            .Should().Equal("alpha", "beta", "zeta");
    }

    [Fact]
    public void FileStore_SelectLexicalCandidates_ReturnsOnlyTheBoundedDeterministicSnapshot()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-search-candidates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new FileMemoryStore(root);
            string scope = MemoryNaming.BuildScopedTopicScope("topic");
            foreach (string name in new[] { "zeta", "alpha", "beta" })
            {
                store.Upsert(new ArtifactEntry(name, "", 0, 1, DateTimeOffset.UtcNow)
                {
                    ContentPreview = name == "zeta" ? "unrelated" : "alpha details",
                }, scope);
                store.WriteArtifactAsync(name, scope, Nmp2ChunkedEncoder.Encode(name)).GetAwaiter().GetResult();
            }

            store.SelectLexicalCandidates([scope], null, "alpha", 2)
                .Select(candidate => candidate.Entry.Name)
                .Should().Equal("alpha", "beta");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task PipelineFiltersBeforeCandidateLimit(bool supplied, bool attributes)
    {
        using var isolation = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;
        for (int i = 0; i < 210; i++)
        {
            bool keep = i >= 205;
            store.Upsert(new ArtifactEntry(keep ? $"zz-keep-{i}" : $"alpha-drop-{i:D3}",
                "unused", 5, 1, DateTimeOffset.UtcNow)
            {
                ContentPreview = "alpha alice",
                TermFrequencies = new() { ["alpha"] = 1, ["alice"] = 1 },
                Attributes = new() { ["kind"] = [keep ? "spec" : "decoy"], ["person"] = ["alice"] },
            }, "local:notes");
        }

        var query = new SearchQuery("alpha")
        {
            Scopes = ["local:notes"],
            Limit = 3,
            PathPrefix = attributes ? null : "/notes/zz-keep",
            AttributeFilter = attributes ? new Dictionary<string, string[]> { ["kind"] = ["spec"] } : null,
            Candidates = supplied ? store.ListScoped(["local:notes"]) : null,
        };
        var observer = new CandidateObserver();

        var outcome = await SearchPipeline.ExecuteAsync(store, query, observer, null,
            context: "alice", surfaceEntityFacts: true, candidateOptions: new SearchCandidateOptions(200));

        outcome.Matches.OfType<EntryResult>().Should().HaveCount(3);
        outcome.Matches.OfType<EntryResult>().Should()
            .OnlyContain(result => result.Item.Entry.Name.StartsWith("zz-keep", StringComparison.Ordinal));
        observer.Seen.Should().HaveCount(5);
        observer.Seen.Should()
            .OnlyContain(candidate => candidate.Entry.Name.StartsWith("zz-keep", StringComparison.Ordinal));
        observer.Query.Should().Be("alpha alice");
        outcome.EntityFacts.Single(fact => fact.Entity.Key == "person" && fact.Entity.Value == "alice")
            .MentionCount.Should().Be(observer.Seen.Count);
    }

    private sealed class CandidateObserver : ISearchScoreContributor
    {
        public IReadOnlyList<ScopedArtifact> Seen { get; private set; } = [];
        public string Query { get; private set; } = "";

        public Task<IReadOnlyDictionary<string, double>?> ComputeScoresAsync(string query,
            IReadOnlyList<ScopedArtifact> candidates, IMemoryStore store, CancellationToken ct)
        {
            Query = query;
            Seen = candidates;
            return Task.FromResult<IReadOnlyDictionary<string, double>?>(null);
        }
    }

    private static ScopedArtifact Candidate(string name, string preview) =>
        new("local", new ArtifactEntry(name, "", 0, 1, DateTimeOffset.UtcNow)
        {
            ContentPreview = preview,
            TermFrequencies = Scrinia.Core.Search.TextAnalysis.ComputeTermFrequencies(preview),
        });
}
