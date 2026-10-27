using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests;

public sealed class SearchCandidateSelectionTests
{
    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(200)]
    public void BoundedSelectionMatchesStableFullRanking(int limit)
    {
        var random = new Random(91823);
        var entries = Enumerable.Range(0, 1200).Select(i =>
            Candidate(i % 2 == 0 ? "local:notes" : "local:other",
                $"{(random.Next(3) == 0 ? "alpha" : "note")}-{i % 40:D2}",
                i % 3 == 0 ? "keep" : "drop")).ToArray();
        var query = new SearchQuery("alpha beta")
        {
            AttributeFilter = new Dictionary<string, string[]> { ["kind"] = ["keep"] }
        };
        var expected = SearchCandidateOptions.RankLexically(entries.Where(c => c.Entry.Attributes!["kind"][0] == "keep"), query.Query)
            .Take(limit).ToArray();
        var actual = SearchCandidateSelection.Select(entries, query, limit, (scope, name) => $"/{scope}/{name}");

        actual.Should().HaveCount(expected.Length);
        for (int i = 0; i < expected.Length; i++)
            actual[i].Should().BeSameAs(expected[i], "equal-ranked candidates retain source order");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DefaultOverloadFiltersBeforeLimit(bool supplied)
    {
        var entries = Enumerable.Range(0, 205)
            .Select(i => Candidate("local:notes", $"alpha-drop-{i:D3}", "decoy"))
            .Append(Candidate("local:notes", "zz-keep", "spec")).ToArray();
        IMemoryStore store = new CatalogStore(entries);
        var query = new SearchQuery("alpha")
        {
            Scopes = ["local:notes"],
            AttributeFilter = new Dictionary<string, string[]> { ["kind"] = ["spec"] },
            Candidates = supplied ? entries : null,
        };

        store.SelectLexicalCandidates(query, 200).Select(c => c.Entry.Name)
            .Should().Equal("zz-keep");
    }

    [Fact]
    public void SuppliedCandidatesAreScopeFilteredBeforeLimit()
    {
        var entries = new[] { Candidate("local:drop", "alpha"), Candidate("local:keep", "zz-keep") };
        IMemoryStore store = new CatalogStore(entries);
        var query = new SearchQuery("alpha") { Scopes = ["local:keep"], Candidates = entries };

        store.SelectLexicalCandidates(query, 1).Single().Scope.Should().Be("local:keep");
        var excluded = query with { Scopes = null, ExcludeTopics = ["DROP"] };
        store.SelectLexicalCandidates(excluded, 1).Single().Scope.Should().Be("local:keep");
    }

    [Fact]
    public void TopicOnlyPrefixRemainsAnEligibleCandidate()
    {
        IMemoryStore store = new CatalogStore([Candidate("local:alpha", "note")]);

        store.SelectLexicalCandidates(new SearchQuery("alpha") { PathPrefix = "alpha" }, 1)
            .Should().ContainSingle();
    }

    [Fact]
    public void EmptyValueFilterMeansPresenceEmptyFilterMeansNoRestriction()
    {
        IMemoryStore store = new CatalogStore([
            Candidate("local:notes", "alpha"), Candidate("local:notes", "keep", "spec")]);
        var query = new SearchQuery("alpha")
            { AttributeFilter = new Dictionary<string, string[]> { ["kind"] = [] } };

        store.SelectLexicalCandidates(query, 2).Single().Entry.Name.Should().Be("keep");
        store.SelectLexicalCandidates(query with { AttributeFilter = new Dictionary<string, string[]>() }, 2)
            .Should().HaveCount(2);
    }

    [Fact]
    public void SuppliedEphemeralCandidateIsExcludedByDurableScopes()
    {
        var entries = new[] { Candidate("ephemeral", "~alpha"), Candidate("local:notes", "alpha") };
        IMemoryStore store = new CatalogStore(entries);
        var query = new SearchQuery("alpha") { Scopes = ["local:notes"], Candidates = entries };

        store.SelectLexicalCandidates(query, 1).Single().Scope.Should().Be("local:notes");
        store.SelectLexicalCandidates(query with { Scopes = ["ephemeral"] }, 1)
            .Single().Scope.Should().Be("ephemeral");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FileStoreFiltersBeforeLimit(bool supplied)
    {
        using var workspace = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(workspace.WorkspaceDir);
        var entries = new[]
        {
            Candidate("local:notes", "alpha-drop", "decoy"),
            Candidate("local:notes", "zz-keep", "spec"),
        };
        foreach (var candidate in entries)
        {
            store.Upsert(candidate.Entry, candidate.Scope);
            await store.WriteArtifactAsync(candidate.Entry.Name, candidate.Scope,
                Nmp2ChunkedEncoder.Encode("alpha"));
        }
        var query = new SearchQuery("alpha")
        {
            Scopes = ["local:notes"],
            AttributeFilter = new Dictionary<string, string[]> { ["kind"] = ["spec"] },
            Candidates = supplied ? entries : null,
        };

        store.SelectLexicalCandidates(query, 1).Select(candidate => candidate.Entry.Name)
            .Should().Equal("zz-keep");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PathAndAttributesFilterBeforeLimitWithTypedAddressPrecedence(bool supplied)
    {
        var entries = new[]
        {
            Candidate("local:notes", "alpha-drop", "spec"),
            Candidate("local:notes", "zz-keep", "spec"),
            Candidate("local:notes", "zz-keep-decoy", "decoy"),
        };
        IMemoryStore store = new CatalogStore(entries);
        var query = new SearchQuery("alpha")
        {
            Address = MemoryAddress.Parse("/notes/zz-keep"),
            PathPrefix = "/notes/alpha",
            AttributeFilter = new Dictionary<string, string[]> { ["kind"] = ["SPEC"] },
            Candidates = supplied ? entries : null,
        };

        store.SelectLexicalCandidates(query, 1).Single().Entry.Name.Should().Be("zz-keep");
    }

    [Fact]
    public void ActiveAttributesDisableRawTopicPrefixException()
    {
        IMemoryStore store = new CatalogStore([Candidate("local:alpha", "note", "spec")]);
        var query = new SearchQuery("alpha")
        {
            PathPrefix = "alpha",
            AttributeFilter = new Dictionary<string, string[]> { ["kind"] = ["spec"] },
        };

        store.SelectLexicalCandidates(query, 1).Should().BeEmpty();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DurableSelectionRequiresWorkspaceReadAdmissionEvenWithSnapshot(bool supplied)
    {
        using var workspace = new TestHelpers.StoreScope(createStoreDir: false);
        Directory.CreateDirectory(workspace.WorkspaceDir);
        File.WriteAllText(Path.Combine(workspace.WorkspaceDir, ".scrinia"), "blocked");
        using var store = new FileMemoryStore(workspace.WorkspaceDir);
        var query = new SearchQuery("alpha")
        {
            Scopes = ["local:notes"],
            Candidates = supplied ? [Candidate("local:notes", "alpha")] : null,
        };

        Action select = () => store.SelectLexicalCandidates(query, 1);

        select.Should().Throw<IOException>();
    }

    [Fact]
    public void EphemeralOnlySelectionDoesNotRequireWorkspaceReadAdmission()
    {
        using var workspace = new TestHelpers.StoreScope(createStoreDir: false);
        Directory.CreateDirectory(workspace.WorkspaceDir);
        File.WriteAllText(Path.Combine(workspace.WorkspaceDir, ".scrinia"), "blocked");
        using var store = new FileMemoryStore(workspace.WorkspaceDir);
        var query = new SearchQuery("alpha")
        {
            Scopes = ["ephemeral"],
            Candidates = [Candidate("ephemeral", "alpha")],
        };

        store.SelectLexicalCandidates(query, 1).Single().Scope.Should().Be("ephemeral");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CallableOverloadsValidateQueryAndLimit(bool fileStore)
    {
        using var workspace = new TestHelpers.StoreScope();
        using var production = new FileMemoryStore(workspace.WorkspaceDir);
        IMemoryStore store = fileStore ? production : new CatalogStore([]);
        // Null is intentional: exercise the public guard rather than a nullable caller contract.
        Action nullQuery = () => store.SelectLexicalCandidates(null!, 1);
        Action zeroLimit = () => store.SelectLexicalCandidates(new SearchQuery("alpha"), 0);
        Action negativeLimit = () => store.SelectLexicalCandidates(new SearchQuery("alpha"), -1);

        nullQuery.Should().Throw<ArgumentNullException>().WithParameterName("query");
        zeroLimit.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("limit");
        negativeLimit.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("limit");
    }

    private sealed class CatalogStore(ScopedArtifact[] entries) : FakeMemoryStore
    {
        public override List<ScopedArtifact> ListScoped(IReadOnlyList<string>? scopes = null)
            => entries.Where(e => ResolveReadScopes(scopes).Contains(e.Scope,
                StringComparer.OrdinalIgnoreCase)).ToList();

        public override IReadOnlyList<string> ResolveReadScopes(IReadOnlyList<string>? scopes = null)
            => scopes is { Count: > 0 } ? scopes : entries.Select(e => e.Scope).Distinct().ToArray();

        public override string FormatQualifiedName(string scope, string subject)
            => scope == "local" ? $"/{subject}" : $"/{scope[6..]}/{subject}";
    }

    private static ScopedArtifact Candidate(string scope, string name, string? kind = null)
        => new(scope, new ArtifactEntry(name, "unused", 5, 1, DateTimeOffset.UtcNow)
        {
            ContentPreview = "alpha",
            TermFrequencies = new() { ["alpha"] = 1 },
            Attributes = kind is null ? null : new() { ["kind"] = [kind] },
        });
}
