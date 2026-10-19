using System.Diagnostics;
using System.Reflection;
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.TestDoubles;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Tests for the child-process plugin protocol DTOs and the embeddings types
/// used by <c>Scrinia.Plugin.Embeddings.Cli</c>.
///
/// These tests validate that the core types (EmbeddingOptions, EmbeddingProviderFactory,
/// VectorStore) still work correctly now that the CLI entry point is a child process
/// rather than an in-process plugin.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public class EmbeddingsPluginCliTests
{
    private readonly string _cliAssembly;

    public EmbeddingsPluginCliTests()
    {
        var assembly = typeof(EmbeddingsPluginCliTests).Assembly;
        _cliAssembly = assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .Single(a => a.Key == "EmbeddingsCliPath").Value
            ?? throw new InvalidOperationException("The build did not supply EmbeddingsCliPath.");
        string configuration = assembly.GetCustomAttribute<AssemblyConfigurationAttribute>()!.Configuration;

        Path.IsPathFullyQualified(_cliAssembly).Should().BeTrue("the build must supply an absolute TargetPath");
        _cliAssembly.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Should().Contain(configuration, "the child CLI must come from the current build configuration");
        File.Exists(_cliAssembly).Should().BeTrue("the child plugin assembly must be available to this test");
        File.Exists(Path.ChangeExtension(_cliAssembly, ".runtimeconfig.json")).Should().BeTrue(
            "the child CLI requires its own runtime configuration");
        File.Exists(Path.ChangeExtension(_cliAssembly, ".deps.json")).Should().BeTrue(
            "the child CLI requires its own dependency manifest");
    }

    [Fact]
    public async Task NoArgumentInvocation_UsesScriniaHome_AndDoesNotWritePackageDirectory()
    {
        using var environmentLock = ProcessEnvironmentLock.Acquire();
        string home = Path.Combine(Path.GetTempPath(), $"scrinia lifecycle ü {Guid.NewGuid():N}");
        string legacyModelDir = Path.Combine(AppContext.BaseDirectory, "scri-plugin-embeddings");
        string legacyDataDir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", ".scrinia"));
        var legacySnapshot = SnapshotFiles(AppContext.BaseDirectory, legacyDataDir);

        try
        {
            using var homeScope = new EnvironmentVariableScope("SCRINIA_HOME", home);
            var elapsed = Stopwatch.StartNew();
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "dotnet",
                ArgumentList = { _cliAssembly },
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
            });

            process.Should().NotBeNull();
            Task<string> stderr = process!.StandardError.ReadToEndAsync();
            Task<string> stdout = process.StandardOutput.ReadToEndAsync();
            process.StandardInput.Close();
            TimeSpan stdinClosedAt = elapsed.Elapsed;
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (timeout.IsCancellationRequested)
            {
                TimeSpan timedOutAt = elapsed.Elapsed;
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                string capturedStderr = await stderr.WaitAsync(TimeSpan.FromSeconds(5));
                string capturedStdout = await stdout.WaitAsync(TimeSpan.FromSeconds(5));
                throw new TimeoutException(
                    $"The child plugin did not exit after stdio closed. " +
                    $"Elapsed since launch: {timedOutAt.TotalSeconds:F3}s; " +
                    $"elapsed since stdin closed: {(timedOutAt - stdinClosedAt).TotalSeconds:F3}s. " +
                    $"Child assembly: '{_cliAssembly}'. " +
                    $"stdout: {capturedStdout}\n" +
                    $"stderr: {capturedStderr}");
            }

            process.ExitCode.Should().Be(0, "the child plugin must exit cleanly after stdio closes");
            (await stderr.WaitAsync(TimeSpan.FromSeconds(5))).Should().Contain("Embeddings plugin started");
            (await stdout.WaitAsync(TimeSpan.FromSeconds(5))).Should().BeEmpty();
            Directory.Exists(Path.Combine(home, "models")).Should().BeTrue();
            Directory.Exists(Path.Combine(home, "plugins")).Should().BeTrue();
            SnapshotFiles(AppContext.BaseDirectory, legacyDataDir)
                .Should().Equal(legacySnapshot, "no-argument startup must not write the package directory");
            Directory.Exists(legacyModelDir).Should().BeFalse(
                "the no-argument fallback must not create the legacy package model directory");
        }
        finally
        {
            DeleteDirectoryAndVerify(home);
        }
    }

    private static void DeleteDirectoryAndVerify(string path)
    {
        if (Directory.Exists(path))
            Directory.Delete(path, recursive: true);

        if (Directory.Exists(path) || File.Exists(path))
            throw new IOException($"CLI test cleanup did not remove '{path}'.");
    }

    private static string[] SnapshotFiles(params string[] roots) => roots
        .Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
        .Order(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    [Fact]
    public void EmbeddingOptions_HasExpectedDefaults()
    {
        var options = new EmbeddingOptions();
        options.Provider.Should().Be("model2vec");
        options.SemanticWeight.Should().Be(50.0);
    }

    [Fact]
    public void EmbeddingProviderFactory_NoneProvider_ReturnsNullProvider()
    {
        var options = new EmbeddingOptions { Provider = "none" };
        var provider = EmbeddingProviderFactory.Create(
            options, Path.GetTempPath(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        provider.Should().BeOfType<NullEmbeddingProvider>();
        provider.IsAvailable.Should().BeFalse();
    }

    [Fact]
    public void EmbeddingProviderFactory_UnknownProvider_ReturnsNullProvider()
    {
        var options = new EmbeddingOptions { Provider = "nonexistent" };
        var provider = EmbeddingProviderFactory.Create(
            options, Path.GetTempPath(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance);

        provider.Should().BeOfType<NullEmbeddingProvider>();
    }

    [Fact]
    public async Task NullEmbeddingProvider_EmbedAsync_ReturnsNull()
    {
        var provider = new NullEmbeddingProvider();
        var result = await provider.EmbedAsync("test");
        result.Should().BeNull();
    }

    [Fact]
    public async Task NullEmbeddingProvider_EmbedBatchAsync_ReturnsNull()
    {
        var provider = new NullEmbeddingProvider();
        var result = await provider.EmbedBatchAsync(["test1", "test2"]);
        result.Should().BeNull();
    }

    [Fact]
    public void VectorStore_CanBeCreatedWithTempDir()
    {
        var tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-cli-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            using var store = new VectorStore(tempDir);
            store.Count().Should().Be(0);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
