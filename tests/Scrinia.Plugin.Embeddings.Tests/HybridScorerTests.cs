using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Tests that FileMemoryStore.SearchAll correctly incorporates supplemental scores (from embeddings).
/// </summary>
public class HybridScorerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly FileMemoryStore _store;

    public HybridScorerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_hybrid_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(_tempDir, ".scrinia", "store"));
        _store = new FileMemoryStore(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private void AddEntry(string name, string description)
    {
        var entry = new ArtifactEntry(name, "", 100, 1, DateTimeOffset.UtcNow, description);
        _store.Upsert(entry, "local");
        // Write a dummy artifact file
        string artifactPath = _store.ArtifactPath(name, "local");
        Directory.CreateDirectory(Path.GetDirectoryName(artifactPath)!);
        File.WriteAllText(artifactPath, Nmp2ChunkedEncoder.Encode("test content"));
    }

    [Fact]
    public void SearchAll_WithSupplementalScores_BoostsRanking()
    {
        AddEntry("cats", "A document about cats");
        AddEntry("semantically-similar", "unrelated description xyz");

        // Without supplemental: "cats" matches, "semantically-similar" doesn't
        var resultsWithout = _store.SearchAll("cats", "local", 10);
        resultsWithout.Should().HaveCountGreaterOrEqualTo(1);
        (resultsWithout[0] as EntryResult)!.Item.Entry.Name.Should().Be("cats");

        // With supplemental: boost "semantically-similar" to the top
        var supplemental = new Dictionary<string, double>
        {
            ["local|semantically-similar"] = 200.0,
        };

        var resultsWith = _store.SearchAll("cats", "local", 10, supplemental);
        resultsWith.Should().HaveCount(2);
        (resultsWith[0] as EntryResult)!.Item.Entry.Name.Should().Be("semantically-similar");
    }

    [Fact]
    public void SearchAll_WithNullSupplemental_BehavesLikeLegacy()
    {
        AddEntry("test-memory", "test description about testing");

        var legacy = _store.SearchAll("test", "local", 10);
        var withNull = _store.SearchAll("test", "local", 10, supplementalScores: null);

        legacy.Should().HaveCount(withNull.Count);
        legacy[0].Score.Should().Be(withNull[0].Score);
    }

    [Fact]
    public void SearchAll_SupplementalOnly_StillReturnsResults()
    {
        // Entry with no lexical match
        AddEntry("xyz-unrelated", "nothing matches here abcdef");

        // Pure supplemental score should still surface the result
        var supplemental = new Dictionary<string, double>
        {
            ["local|xyz-unrelated"] = 100.0,
        };

        var results = _store.SearchAll("completely-different-query", "local", 10, supplemental);
        results.Should().HaveCount(1);
        (results[0] as EntryResult)!.Item.Entry.Name.Should().Be("xyz-unrelated");
    }
}

/// <summary>
/// Tests for the HybridReranker that re-ranks BM25 candidates using vector similarity.
/// </summary>
public class HybridRerankerTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VectorStore _vectorStore;

    public HybridRerankerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_reranker_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _vectorStore = new VectorStore(_tempDir);
    }

    public void Dispose()
    {
        _vectorStore.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task ComputeScores_WithVectors_ReturnsScores()
    {
        // Pre-index vectors for two entries (both must have non-zero cosine similarity with query)
        float[] vec1 = [1f, 0f, 0f];
        float[] vec2 = [0.5f, 0.5f, 0f]; // partial similarity with vec1
        await _vectorStore.UpsertAsync("local", "entry-a", null, vec1);
        await _vectorStore.UpsertAsync("local", "entry-b", null, vec2);

        // Create a fake embedding provider that returns vec1 for any query
        var provider = new FakeEmbeddingProvider(vec1);
        var reranker = new HybridReranker(provider, _vectorStore, weight: 50.0);

        var candidates = new ScopedArtifact[]
        {
            new("local", new ArtifactEntry("entry-a", "", 100, 1, DateTimeOffset.UtcNow, "desc a")),
            new("local", new ArtifactEntry("entry-b", "", 100, 1, DateTimeOffset.UtcNow, "desc b")),
        };

        var store = new FakeMemoryStore();
        var scores = await reranker.ComputeScoresAsync("test query", candidates, store, CancellationToken.None);

        scores.Should().NotBeNull();
        scores!.Should().ContainKey("local|entry-a");
        scores.Should().ContainKey("local|entry-b");
        scores["local|entry-a"].Should().BeGreaterThan(scores["local|entry-b"],
            because: "entry-a's vector is identical to the query vector");
    }

    [Fact]
    public async Task ComputeScores_EmptyCandidates_ReturnsNull()
    {
        var provider = new FakeEmbeddingProvider([1f, 0f, 0f]);
        var reranker = new HybridReranker(provider, _vectorStore, weight: 50.0);
        var store = new FakeMemoryStore();

        var scores = await reranker.ComputeScoresAsync("query", [], store, CancellationToken.None);

        scores.Should().BeNull();
    }

    [Fact]
    public async Task ComputeScores_UnavailableProvider_ReturnsNull()
    {
        var provider = new FakeEmbeddingProvider(null); // unavailable
        var reranker = new HybridReranker(provider, _vectorStore, weight: 50.0);

        var candidates = new ScopedArtifact[]
        {
            new("local", new ArtifactEntry("entry-a", "", 100, 1, DateTimeOffset.UtcNow, "desc a")),
        };

        var store = new FakeMemoryStore();
        var scores = await reranker.ComputeScoresAsync("query", candidates, store, CancellationToken.None);

        scores.Should().BeNull();
    }

    [Fact]
    public async Task ComputeScores_OnlyEmbedsCandidates_NotEntireCorpus()
    {
        // Index vectors for 3 entries, but only pass 1 as candidate
        await _vectorStore.UpsertAsync("local", "candidate", null, [1f, 0f, 0f]);
        await _vectorStore.UpsertAsync("local", "non-candidate-1", null, [0f, 1f, 0f]);
        await _vectorStore.UpsertAsync("local", "non-candidate-2", null, [0f, 0f, 1f]);

        var provider = new FakeEmbeddingProvider([1f, 0f, 0f]);
        var reranker = new HybridReranker(provider, _vectorStore, weight: 50.0);

        var candidates = new ScopedArtifact[]
        {
            new("local", new ArtifactEntry("candidate", "", 100, 1, DateTimeOffset.UtcNow, "desc")),
        };

        var store = new FakeMemoryStore();
        var scores = await reranker.ComputeScoresAsync("query", candidates, store, CancellationToken.None);

        scores.Should().NotBeNull();
        scores!.Should().HaveCount(1);
        scores.Should().ContainKey("local|candidate");
        scores.Should().NotContainKey("local|non-candidate-1");
        scores.Should().NotContainKey("local|non-candidate-2");
    }

    /// <summary>Fake embedding provider for testing.</summary>
    private sealed class FakeEmbeddingProvider : IEmbeddingProvider
    {
        private readonly float[]? _fixedVector;
        public bool IsAvailable => _fixedVector is not null;
        public int Dimensions => _fixedVector?.Length ?? 0;

        public FakeEmbeddingProvider(float[]? fixedVector) => _fixedVector = fixedVector;

        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            Task.FromResult(_fixedVector);

        public Task<float[][]?> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default) =>
            Task.FromResult<float[][]?>(_fixedVector is not null
                ? texts.Select(_ => _fixedVector).ToArray()
                : null);

        public void Dispose() { }
    }

    /// <summary>Minimal IMemoryStore stub for tests that don't need store functionality.</summary>
    private sealed class FakeMemoryStore : IMemoryStore
    {
        public (string Scope, string Subject) ParseQualifiedName(string name) => ("local", name);
        public string FormatQualifiedName(string scope, string subject) => $"{scope}:{subject}";
        public bool IsEphemeral(string name) => name.StartsWith('~');
        public string SanitizeName(string name) => name;
        public Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default) => Task.FromResult("");
        public List<ArtifactEntry> LoadIndex(string scope = "local") => [];
        public void SaveIndex(List<ArtifactEntry> entries, string scope = "local") { }
        public void Upsert(ArtifactEntry entry, string scope = "local") { }
        public bool Remove(string name, string scope = "local") => false;
        public List<ScopedArtifact> ListScoped(string? scopes = null) => [];
        public IReadOnlyList<SearchResult> SearchAll(string query, string? scopes = null, int limit = 20) => [];
        public void RememberEphemeral(string key, EphemeralEntry entry) { }
        public bool ForgetEphemeral(string key) => false;
        public EphemeralEntry? GetEphemeral(string key) => null;
        public bool CopyMemory(string src, string dst, bool overwrite, out string message) { message = ""; return false; }
        public void ArchiveVersion(string subject, string scope = "local") { }
        public string ArtifactPath(string name, string scope = "local") => "";
        public string ArtifactUri(string name, string scope = "local") => "";
        public string FindArtifactPath(string subject, string normalizedScope) => "";
        public string GetStoreDirForScope(string scope) => "";
        public string GenerateContentPreview(string content, int maxLength = 500) => content.Length > maxLength ? content[..maxLength] : content;
        public Task WriteArtifactAsync(string subject, string scope, string artifactText, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default) => Task.FromResult("");
        public bool DeleteArtifact(string subject, string scope) => false;
        public string[] DiscoverTopics() => [];
        public List<TopicInfo> GatherTopicInfos(string? scopes = null) => [];
        public List<(string Name, string FilePath)> ListTopicArtifacts(string topicScope) => [];
        public void ImportTopicEntries(string topicScope, List<ArtifactEntry> entries, Dictionary<string, string> artifactContents, bool overwrite) { }
        public IReadOnlyList<string> ResolveReadScopes(string? scopes = null) => ["local"];
    }
}
