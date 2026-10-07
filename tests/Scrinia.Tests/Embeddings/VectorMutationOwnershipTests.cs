using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Tests.Embeddings;

public sealed class VectorMutationOwnershipTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scrinia-vector-ownership-" + Guid.NewGuid().ToString("N"));

    public VectorMutationOwnershipTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task SeparateProcessPreservesAnUnrelatedWriter()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var child = await TestProcessRunner.StartAsync(["vector", "interleave", _dir], timeout.Token);
        await child.ReadEventAsync("ready", timeout.Token);
        using (var peer = new VectorStore(_dir, "test"))
            await peer.ReplaceMemoryVectorsAsync("local", "beta", [(1, new float[] { 0, 1, 0 })], timeout.Token);
        await child.SendAsync("continue", timeout.Token);
        await child.ReadEventAsync("entered", timeout.Token);
        await child.ReadEventAsync("completed", timeout.Token);
        (await child.WaitForExitAsync(timeout.Token)).Should().Be(0);
        using var fresh = new VectorStore(_dir, "test");
        fresh.GetVectors("local").Select(v => v.Name).Should().BeEquivalentTo(["alpha", "beta"]);
        fresh.GetVectors("local").Single(v => v.Name == "alpha").Vector.Should().Equal(9f, 0f, 0f);
    }

    [Fact]
    public async Task CancelledLockWaitReleasesTheScopeGate()
    {
        using var store = new VectorStore(_dir, "test");
        await store.UpsertAsync("local", "alpha", 1, [1f, 0f, 0f]);
        string path = Directory.GetFiles(_dir, "vectors.bin", SearchOption.AllDirectories).Single();
        using (var held = FileLock.AcquireExclusive(path + ".lock"))
        using (var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(150)))
        {
            Func<Task> attempt = () => store.UpsertAsync("local", "beta", 1, [0f, 1f, 0f], cancelled.Token);
            await attempt.Should().ThrowAsync<OperationCanceledException>();
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.UpsertAsync("local", "beta", 1, [0f, 1f, 0f], timeout.Token);
        using var fresh = new VectorStore(_dir, "test");
        fresh.GetVectors("local").Select(v => v.Name).Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public async Task LegacyMigrationAndMutationPreserveTheExistingVector()
    {
        const string Scope = "local:foo/bar";
        using (var seed = new VectorStore(_dir, "test"))
            await seed.UpsertAsync(Scope, "alpha", 1, [1f, 0f, 0f]);
        string current = Directory.GetFiles(_dir, "vectors.bin", SearchOption.AllDirectories).Single();
        string legacyDirectory = Path.Combine(_dir, "local_foo_bar");
        Directory.CreateDirectory(legacyDirectory);
        File.Move(current, Path.Combine(legacyDirectory, "vectors.bin"));
        using var store = new VectorStore(_dir, "test");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await store.UpsertAsync(Scope, "beta", 1, [0f, 1f, 0f], timeout.Token);
        using var fresh = new VectorStore(_dir, "test");
        fresh.GetVectors(Scope).Select(v => v.Name).Should().BeEquivalentTo(["alpha", "beta"]);
        File.Exists(Path.Combine(legacyDirectory, "vectors.bin")).Should().BeFalse();
    }
}
