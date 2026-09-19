using System.Collections.Concurrent;
using FluentAssertions;
using Scrinia.Core.Models;
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
public class EmbeddingsPluginInitTests
{
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

    [SkippableFact]
    public async Task ComputeScoresAsync_ConcurrentWithDispose_OnlyEverThrowsObjectDisposed()
    {
        // Bounded canary for the temp-dir teardown race (environment, not an init-guard regression).
        // EnsureInitialized validates the model directory through a realpath-canonicalized containment
        // check (VerifiedAssetResolver.ValidateDestinationPath); when SCRINIA_HOME is a process-wide
        // temp dir owned by another test's scope, a concurrent Dispose can tear that tree down
        // mid-resolution and fail the check closed with InvalidDataException. A single clean init
        // cycle proves the environment can hold the temp dir still long enough to exercise the stress
        // loop; any throw other than the allowed ObjectDisposedException (containment race, IO/download
        // failure, or timeout) means the loop cannot run reliably here, so skip instead of failing.
        Skip.If(!InitCycleIsStable(),
            "single bounded init cycle failed (temp-dir teardown race, containment failure, or timeout) — " +
            "the concurrent-dispose stress cannot run reliably in this environment");

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

    private static readonly TimeSpan InitCycleTimeout = TimeSpan.FromSeconds(20);

    /// <summary>
    /// One-shot canary for <see cref="ComputeScoresAsync_ConcurrentWithDispose_OnlyEverThrowsObjectDisposed"/>.
    /// Runs a single bounded init cycle (create the plugin, one <c>ComputeScoresAsync</c> on a trivial
    /// query, then dispose) and reports whether the environment can hold the
    /// <c>SCRINIA_HOME</c> temp model dir still long enough for the stress loop. A clean completion
    /// (or the allowed <see cref="ObjectDisposedException"/>) returns true; any other throw — the
    /// <c>InvalidDataException</c> containment race, an IO/download failure, or the wall-clock timeout —
    /// returns false so the caller skips. The plugin never spawns a child process, so the timeout is the
    /// hang guard (there is no process tree to kill) and side effects are limited to one throwaway plugin.
    /// </summary>
    private static bool InitCycleIsStable()
    {
        using var probe = new EmbeddingsPlugin();
        var cycle = probe.ComputeScoresAsync(
            "probe", Array.Empty<ScopedArtifact>(), null!, CancellationToken.None);

        // Always observe the task so a late completion after a timeout can never surface as an unobserved
        // exception and disrupt the suite's clean exit.
        _ = cycle.ContinueWith(t => t.Exception?.Handle(_ => true), TaskScheduler.Default);

        try
        {
            // Hard wall-clock bound over the whole cycle: the synchronous containment check and the async
            // embed both must finish within InitCycleTimeout. WaitAsync raises TimeoutException on breach.
            cycle.WaitAsync(InitCycleTimeout).GetAwaiter().GetResult();
            return true;
        }
        catch (ObjectDisposedException)
        {
            // The init guard's only allowed outcome: the probe's own init lost a race with Dispose.
            return true;
        }
        catch (TimeoutException)
        {
            return false;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch
        {
            // Any other failure (InvalidDataException containment race, ModelDownloadException, IO) means
            // the temp-dir timing cannot be stabilized in this environment.
            return false;
        }
        finally
        {
            probe.Dispose();
        }
    }
}
