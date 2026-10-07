using System.Collections.Concurrent;
using FluentAssertions;
using Scrinia.Core.Models;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Regression tests for the concurrent cold-start guard in
/// <c>EmbeddingsPlugin.EnsureInitialized</c> (double-checked lock + disposed check,
/// added to fix a check-then-act race where two concurrent request handlers could
/// each call <c>VulkanEmbeddingProvider.Create</c>, loading the GGUF model onto the
/// GPU twice and orphaning a <c>LLamaWeights</c>/<c>LLamaEmbedder</c> pair).
///
/// These tests exercise the real (non-mocked) init path through the public
/// <see cref="EmbeddingsPlugin.ComputeScoresAsync"/> entry point, which calls
/// <c>EnsureInitialized</c> before doing anything else. They do NOT touch a GPU:
/// the test process has no GGUF model at the plugin's expected
/// "SCRINIA_HOME/models" path, so
/// <c>VulkanModelManager.IsModelAvailable</c> returns false and
/// <c>VulkanEmbeddingProvider.Create</c> is never invoked. Only the lock/disposed-guard
/// logic itself is under test — this does not prove GPU-load de-duplication under a
/// real model, which would require a GPU + GGUF model (see task report for the
/// corresponding manual/integration verification plan).
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class EmbeddingsPluginInitTests : IDisposable
{
    private readonly ProcessEnvironmentLock _environmentLock;
    private readonly EnvironmentVariableScope _homeScope;
    private readonly string _home;

    public EmbeddingsPluginInitTests()
    {
        _environmentLock = ProcessEnvironmentLock.Acquire();
        _home = Path.Combine(Path.GetTempPath(), $"scrinia_init_home_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_home);
        _homeScope = new EnvironmentVariableScope("SCRINIA_HOME", _home);
    }

    public void Dispose()
    {
        _homeScope.Dispose();
        try { Directory.Delete(_home, recursive: true); } catch { }
        _environmentLock.Dispose();
    }

    [Fact]
    public async Task ComputeScoresAsync_ConcurrentColdStart_CompletesWithoutRaceExceptions()
    {
        using var plugin = new EmbeddingsPlugin();
        var candidates = Array.Empty<ScopedArtifact>();

        var tasks = Enumerable.Range(0, 32)
            .Select(_ => plugin.ComputeScoresAsync("query", candidates, null!, CancellationToken.None))
            .ToArray();

        var act = async () => await Task.WhenAll(tasks);
        await act.Should().NotThrowAsync(
            "concurrent cold-start calls should serialize on the init lock instead of racing");
    }

    [Fact]
    public async Task ComputeScoresAsync_ConcurrentWithDispose_OnlyEverThrowsObjectDisposed()
    {
        // The fixture owns an empty home under the same process lock as all environment writers.
        // Keep it alive until every init and disposal worker has completed.
        var unexpected = new ConcurrentBag<Exception>();

        // Repeat many rounds to give the race a fair chance to manifest if the guard
        // regresses; each round uses a fresh plugin instance so Dispose() is only ever
        // racing against that instance's own init calls.
        for (int round = 0; round < 25; round++)
        {
            var plugin = new EmbeddingsPlugin();
            var candidates = Array.Empty<ScopedArtifact>();

            var initTasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
            {
                try
                {
                    await plugin.ComputeScoresAsync("query", candidates, null!, CancellationToken.None);
                }
                catch (ObjectDisposedException)
                {
                    // Expected: this call's init lost the race against Dispose().
                }
                catch (Exception ex)
                {
                    unexpected.Add(ex);
                }
            })).ToArray();

            var disposeTask = Task.Run(plugin.Dispose);

            await Task.WhenAll(initTasks.Append(disposeTask));
        }

        unexpected.Should().BeEmpty(
            "the disposed-check inside the init lock should prevent torn state (e.g. NullReferenceException) " +
            "when Dispose races with EnsureInitialized; ObjectDisposedException is the only acceptable outcome");
    }

}
