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

    private static ScopedArtifact Candidate(string name, string preview) =>
        new("local", new ArtifactEntry(name, "", 0, 1, DateTimeOffset.UtcNow)
        {
            ContentPreview = preview,
            TermFrequencies = Scrinia.Core.Search.TextAnalysis.ComputeTermFrequencies(preview),
        });
}
