using FluentAssertions;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests.Embeddings;

public class VectorStoreTests : IDisposable
{
    private readonly string _tempDir;
    private readonly VectorStore _store;

    public VectorStoreTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia_vectest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _store = new VectorStore(_tempDir);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    [Fact]
    public async Task UpsertAndGet_PersistsVector()
    {
        float[] vec = [0.1f, 0.2f, 0.3f];
        await _store.UpsertAsync("local", "test-memory", null, vec);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("test-memory");
        vectors[0].ChunkIndex.Should().BeNull();
        vectors[0].Vector.Should().BeEquivalentTo(vec);
    }

    [Fact]
    public async Task Upsert_OverwritesExisting()
    {
        float[] vec1 = [0.1f, 0.2f, 0.3f];
        float[] vec2 = [0.4f, 0.5f, 0.6f];

        await _store.UpsertAsync("local", "test", null, vec1);
        await _store.UpsertAsync("local", "test", null, vec2);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Vector.Should().BeEquivalentTo(vec2);
    }

    [Fact]
    public async Task Upsert_ChunkVectors_StoredSeparately()
    {
        float[] entryVec = [0.1f, 0.2f, 0.3f];
        float[] chunk1Vec = [0.4f, 0.5f, 0.6f];
        float[] chunk2Vec = [0.7f, 0.8f, 0.9f];

        await _store.UpsertAsync("local", "multi", null, entryVec);
        await _store.UpsertAsync("local", "multi", 1, chunk1Vec);
        await _store.UpsertAsync("local", "multi", 2, chunk2Vec);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(3);
    }

    [Fact]
    public async Task Remove_DeletesAllVectorsForName()
    {
        await _store.UpsertAsync("local", "rem", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "rem", 1, [0.3f, 0.4f]);
        await _store.UpsertAsync("local", "keep", null, [0.5f, 0.6f]);

        await _store.RemoveAsync("local", "rem");

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("keep");
    }

    [Fact]
    public async Task Remove_OnColdCache_RemovesVectorsWrittenByAnotherInstance()
    {
        // Both existing Remove tests upsert first on the SAME instance, so the scope's cache is
        // already warm by the time RemoveAsync runs. RemoveAsync must load from disk on a cache
        // miss the same way UpsertAsync does — otherwise calling it as the first operation this
        // instance performs against a scope silently no-ops (nothing looks stale in memory) and
        // leaves the on-disk vectors behind.
        await _store.UpsertAsync("local", "gone", null, [0.1f, 0.2f]);

        using (var cold = new VectorStore(_tempDir))
            await cold.RemoveAsync("local", "gone");          // first op on this instance

        using var verify = new VectorStore(_tempDir);
        verify.GetVectors("local").Should().BeEmpty();
    }

    [Fact]
    public async Task Persistence_SurvivesNewInstance()
    {
        float[] vec = [0.1f, 0.2f, 0.3f, 0.4f];
        await _store.UpsertAsync("local", "persist", null, vec);

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("persist");
        vectors[0].Vector.Should().BeEquivalentTo(vec);
    }

    [Fact]
    public async Task Replace_WhenPersistenceFails_DoesNotPublishReplacement()
    {
        await _store.UpsertAsync("local", "memo", 0, [1f, 0f]);

        string scopeDirectory = Path.Combine(_tempDir, "local");
        Directory.Delete(scopeDirectory, recursive: true);
        File.WriteAllText(scopeDirectory, "blocking file");

        Func<Task> replace = () => _store.ReplaceMemoryVectorsAsync(
            "local", "memo", [(1, new[] { 0f, 1f })]);

        await replace.Should().ThrowAsync<Exception>();
        _store.GetVectors("local").Should().ContainSingle()
            .Which.Vector.Should().BeEquivalentTo([1f, 0f]);
    }

    [Fact]
    public async Task Replace_WhenWriteFailsAfterARecordStarts_PreservesThePriorDurableLog()
    {
        await _store.UpsertAsync("local", "memo", 0, [1f, 0f]);

        Func<Task> replace = () => _store.ReplaceMemoryVectorsAsync(
            "local", "memo", [(1, new[] { 0f, 1f }), (2, null!) ]);

        await replace.Should().ThrowAsync<Exception>();

        using var reopened = new VectorStore(_tempDir);
        reopened.GetVectors("local").Should().ContainSingle()
            .Which.Vector.Should().BeEquivalentTo([1f, 0f]);
        Directory.GetFiles(Path.Combine(_tempDir, "local"), "vectors.bin.*.tmp")
            .Should().BeEmpty("a failed replacement must not leave a recoverable-looking partial record");
    }

    [Fact]
    public async Task EphemeralScope_NotPersistedToDisk()
    {
        await _store.UpsertAsync("ephemeral", "temp", null, [0.1f, 0.2f]);

        var vectors = _store.GetVectors("ephemeral");
        vectors.Should().HaveCount(1);

        using var store2 = new VectorStore(_tempDir);
        var vectors2 = store2.GetVectors("ephemeral");
        vectors2.Should().BeEmpty();
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("remove")]
    [InlineData("replace")]
    public async Task UnsupportedDiskFormatRefusesMutationEvenWithWarmCache(string operation)
    {
        await _store.UpsertAsync("local", "original", null, [1f, 0f]);
        string path = Path.Combine(_tempDir, "local", "vectors.bin");
        byte[] unsupported = "SVF9future-data"u8.ToArray();
        File.WriteAllBytes(path, unsupported);

        Func<Task> mutate = operation switch
        {
            "upsert" => () => _store.UpsertAsync("local", "original", null, [0f, 1f]),
            "remove" => () => _store.RemoveAsync("local", "original"),
            _ => () => _store.ReplaceMemoryVectorsAsync("local", "original", []),
        };
        await mutate.Should().ThrowAsync<VectorStoreUnavailableException>();

        _store.GetVectors("local").Should().ContainSingle().Which.Vector.Should().Equal(1f, 0f);
        File.ReadAllBytes(path).Should().Equal(unsupported);
        Directory.GetFiles(_tempDir, "*.tmp", SearchOption.AllDirectories).Should().BeEmpty();
    }

    [Theory]
    [InlineData("upsert")]
    [InlineData("remove")]
    [InlineData("replace")]
    public async Task UnreadableDiskRefusesMutationEvenWithWarmCache(string operation)
    {
        await _store.UpsertAsync("local", "original", null, [1f, 0f]);
        string path = Path.Combine(_tempDir, "local", "vectors.bin");
        byte[] before = File.ReadAllBytes(path);
        using (var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            Func<Task> mutate = operation switch
            {
                "upsert" => () => _store.UpsertAsync("local", "original", null, [0f, 1f]),
                "remove" => () => _store.RemoveAsync("local", "original"),
                _ => () => _store.ReplaceMemoryVectorsAsync("local", "original", []),
            };
            await mutate.Should().ThrowAsync<VectorStoreUnavailableException>();
            _store.GetVectors("local").Should().ContainSingle().Which.Vector.Should().Equal(1f, 0f);
        }
        File.ReadAllBytes(path).Should().Equal(before);
    }

    [Fact]
    public async Task FailedLegacyMigrationCannotBecomeMissingWriteBaseline()
    {
        string legacyPath = Path.Combine(_tempDir, "local_api", "vectors.bin");
        Directory.CreateDirectory(legacyPath);
        string marker = Path.Combine(legacyPath, "unreadable-source");
        File.WriteAllBytes(marker, "source"u8.ToArray());

        Func<Task> mutate = () => _store.UpsertAsync("local:api", "new", null, [1f]);
        await mutate.Should().ThrowAsync<VectorStoreUnavailableException>();

        File.Exists(Path.Combine(_tempDir, "local", "api", "vectors.bin")).Should().BeFalse();
        File.ReadAllBytes(marker).Should().Equal("source"u8.ToArray());
        _store.Count().Should().Be(0);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PersistentMutationRetainsLegacyFormatPayloads(int version)
    {
        string path = Path.Combine(_tempDir, "local", "vectors.bin");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(System.Text.Encoding.UTF8.GetBytes($"SVF{version}"));
            writer.Write((ushort)2);
            if (version == 1)
                writer.Write((uint)1);
            else
                writer.Write((byte)0);
            writer.Write((ushort)6);
            writer.Write("legacy"u8);
            writer.Write(-1);
            writer.Write(0.25f);
            writer.Write(0.75f);
        }
        File.WriteAllBytes(path, buffer.ToArray());

        await _store.UpsertAsync("local", "new", null, [1f, 0f]);

        using var fresh = new VectorStore(_tempDir);
        fresh.GetVectors("local").Should().HaveCount(2);
        fresh.GetVectors("local").Single(entry => entry.Name == "legacy").Vector.Should().Equal(0.25f, 0.75f);
        File.ReadAllBytes(path).Take(4).Should().Equal("SVF3"u8.ToArray());
    }

    [Fact]
    public async Task TotalVectorCount_SumsAllScopes()
    {
        await _store.UpsertAsync("local", "a", null, [0.1f]);
        await _store.UpsertAsync("local:api", "b", null, [0.2f]);
        await _store.UpsertAsync("ephemeral", "c", null, [0.3f]);

        _store.Count().Should().Be(3);
    }

    [Fact]
    public void GetVectors_EmptyScope_ReturnsEmpty()
    {
        _store.GetVectors("nonexistent").Should().BeEmpty();
    }

    [Fact]
    public async Task ProbeScopesDetailed_DeduplicatesAndReportsMissingNestedScopes()
    {
        await _store.UpsertAsync("local:topic/subtopic", "memo", null, [1f]);

        var probes = _store.ProbeScopesDetailed([
            "local:topic/subtopic", "LOCAL:TOPIC/SUBTOPIC", "local:missing"]);

        probes.Should().HaveCount(2);
        probes.Should().ContainSingle(probe => probe.Scope == "local:topic/subtopic"
            && probe.HasVectorFile && !probe.IsStale && probe.Vectors.Count == 1);
        probes.Should().ContainSingle(probe => probe.Scope == "local:missing"
            && !probe.HasVectorFile && !probe.IsStale && probe.Vectors.Count == 0);
    }

    [Fact]
    public async Task ProbeScopes_DoesNotReturnEarlierQuarantineOutsideRequestedScopes()
    {
        using (var oldStore = new VectorStore(_tempDir, expectedSignature: "old"))
            await oldStore.UpsertAsync("unrelated", "memo", null, [1f]);

        using var currentStore = new VectorStore(_tempDir, expectedSignature: "current");
        await currentStore.UpsertAsync("requested", "memo", null, [1f]);
        currentStore.GetVectors("unrelated").Should().BeEmpty();

        currentStore.ProbeScopes(["requested"])
            .Should().BeEmpty("only stale results from the requested scopes should be returned");
    }

    // -- Persistent mutation behavior tests --

    [Fact]
    public async Task PersistentUpsertPreservesOtherEntries()
    {
        await _store.UpsertAsync("local", "first", null, [0.1f, 0.2f, 0.3f]);
        await _store.UpsertAsync("local", "second", null, [0.4f, 0.5f, 0.6f]);

        var vectors = _store.GetVectors("local");
        vectors.Should().HaveCount(2);
    }

    [Fact]
    public async Task PersistentUpsertsSurviveNewInstance()
    {
        await _store.UpsertAsync("local", "svf2-persist", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "svf2-persist2", null, [0.3f, 0.4f]);

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(2);
        vectors.Should().Contain(v => v.Name == "svf2-persist");
        vectors.Should().Contain(v => v.Name == "svf2-persist2");
    }

    [Fact]
    public async Task PersistentUpsertReplacesExistingPayload()
    {
        await _store.UpsertAsync("local", "update-me", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "update-me", null, [0.3f, 0.4f]);

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Vector.Should().BeEquivalentTo(new[] { 0.3f, 0.4f });
    }

    [Fact]
    public async Task PersistentRemoveDeletesOnlyMatchingName()
    {
        await _store.UpsertAsync("local", "del-me", null, [0.1f, 0.2f]);
        await _store.UpsertAsync("local", "keep", null, [0.3f, 0.4f]);

        await _store.RemoveAsync("local", "del-me");

        using var store2 = new VectorStore(_tempDir);
        var vectors = store2.GetVectors("local");
        vectors.Should().HaveCount(1);
        vectors[0].Name.Should().Be("keep");
    }

    // ── Signature / migration tests ─────────────────────────────────────────────

    [Fact]
    public async Task Svf3_PersistsSignatureAcrossSessions()
    {
        using var sigStore = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text");
        await sigStore.UpsertAsync("local", "x", null, [0.1f, 0.2f, 0.3f]);

        using var reopen = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text");
        var vectors = reopen.GetVectors("local");
        vectors.Should().HaveCount(1);
        reopen.HasStaleQuarantines.Should().BeFalse();
    }

    [Fact]
    public async Task Svf3_MismatchedSignature_QuarantinesAndStartsFresh()
    {
        // Write vectors as ollama
        using (var oldStore = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text"))
        {
            await oldStore.UpsertAsync("local", "x", null, [0.1f, 0.2f, 0.3f]);
        }

        // Reopen as openai — should quarantine the file
        using var newStore = new VectorStore(_tempDir, expectedSignature: "openai:text-embedding-3-small");
        var vectors = newStore.GetVectors("local");

        vectors.Should().BeEmpty("signature mismatch should produce an empty store");
        newStore.HasStaleQuarantines.Should().BeTrue();
        newStore.StaleQuarantineScopes.Should().Contain("local");

        // Quarantined file should still exist on disk under a .stale name (recoverable).
        string localDir = Path.Combine(_tempDir, "local");
        Directory.GetFiles(localDir, "vectors.bin.stale-*").Should().NotBeEmpty();
        File.Exists(Path.Combine(localDir, "vectors.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task Svf3_ChunkedSignature_RoundTrips()
    {
        // The composed signature {provider}|c{size}o{overlap} should persist + match on
        // reopen the same way bare signatures do — VectorStore is opaque to the format.
        string sig = ChunkedSignature.Compose("ollama:nomic-embed-text", 1200, 200);

        using (var w = new VectorStore(_tempDir, expectedSignature: sig))
        {
            await w.UpsertAsync("local", "memo", 0, [0.1f, 0.2f, 0.3f]);
            await w.UpsertAsync("local", "memo", 1, [0.4f, 0.5f, 0.6f]);
        }

        using var r = new VectorStore(_tempDir, expectedSignature: sig);
        r.HasStaleQuarantines.Should().BeFalse();
        var vectors = r.GetVectors("local");
        vectors.Should().HaveCount(2);
        vectors.Select(v => v.ChunkIndex).Should().BeEquivalentTo(new int?[] { 0, 1 });
    }

    [Fact]
    public async Task Svf3_ChunkSizeChange_TriggersQuarantine()
    {
        // Changing ChunkSize via config must produce a different composed signature, which
        // must quarantine the old file — same flow as a model change.
        string oldSig = ChunkedSignature.Compose("ollama:nomic-embed-text", 1200, 200);
        string newSig = ChunkedSignature.Compose("ollama:nomic-embed-text", 1800, 300);

        using (var old = new VectorStore(_tempDir, expectedSignature: oldSig))
        {
            await old.UpsertAsync("local", "memo", 0, [0.1f, 0.2f, 0.3f]);
        }

        using var fresh = new VectorStore(_tempDir, expectedSignature: newSig);
        fresh.GetVectors("local").Should().BeEmpty();
        fresh.HasStaleQuarantines.Should().BeTrue();
    }

    [Fact]
    public async Task Svf3_PreBranchSignature_QuarantinesUnderTheLayoutMarkedSignature()
    {
        // Pre-branch files were signed with the un-marked "{provider}|c{size}o{overlap}" form —
        // no "|e1" suffix — because their chunk index was a raw window ordinal. Reopening under
        // today's ChunkedSignature.Compose (which appends the layout marker) must quarantine
        // them rather than let the new element/window reader misinterpret old indices.
        const string preBranchSignature = "ollama:nomic-embed-text|c1200o200";
        string layoutMarkedSignature = ChunkedSignature.Compose("ollama:nomic-embed-text", 1200, 200);
        layoutMarkedSignature.Should().NotBe(preBranchSignature);

        using (var old = new VectorStore(_tempDir, expectedSignature: preBranchSignature))
        {
            await old.UpsertAsync("local", "memo", 0, [0.1f, 0.2f, 0.3f]);
        }

        using var fresh = new VectorStore(_tempDir, expectedSignature: layoutMarkedSignature);
        fresh.GetVectors("local").Should().BeEmpty();
        fresh.HasStaleQuarantines.Should().BeTrue();
        fresh.StaleQuarantineScopes.Should().Contain("local");
    }

    [Fact]
    public async Task Svf3_NullExpectedSignature_DisablesMismatchCheck()
    {
        // Write with one signature
        using (var oldStore = new VectorStore(_tempDir, expectedSignature: "ollama:nomic-embed-text"))
        {
            await oldStore.UpsertAsync("local", "x", null, [0.1f, 0.2f, 0.3f]);
        }

        // Reopen without an expected signature — test/ad-hoc path. Should read OK,
        // no quarantine.
        using var reopen = new VectorStore(_tempDir, expectedSignature: null);
        var vectors = reopen.GetVectors("local");
        vectors.Should().HaveCount(1);
        reopen.HasStaleQuarantines.Should().BeFalse();
    }

    [Fact]
    public async Task NullSignatureStore_LoadsExistingSignedFile_WithoutQuarantine()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"scrinia_vs_null_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            // Write a signed file under signature "x".
            var signed = new VectorStore(dir, expectedSignature: "x");
            await signed.UpsertAsync("local:memory", "doc", null, new float[] { 1, 0, 0 });

            // A store constructed with a null signature must NOT quarantine it (non-destructive):
            var unsigned = new VectorStore(dir, expectedSignature: null);
            var loaded = unsigned.GetVectors("local:memory");

            loaded.Should().ContainSingle(v => v.Name == "doc");
            unsigned.HasStaleQuarantines.Should().BeFalse();
            Directory.GetFiles(dir, "*.stale-*", SearchOption.AllDirectories).Should().BeEmpty();
        }
        finally { try { Directory.Delete(dir, recursive: true); } catch { } }
    }
}
