using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Scrinia.Plugin.Llm;

namespace Scrinia.Plugin.Llm.Tests;

/// <summary>
/// Bookkeeping tests for <see cref="LlmModelManager"/>. Real downloads are not exercised
/// here — the GGUF is ~900MB and behaviour depends on HuggingFace availability. Coverage
/// focuses on the local-filesystem decisions the manager makes around an existing file.
/// </summary>
public class LlmModelManagerTests
{
    [Fact]
    public void DefaultModelUrl_PointsAtLfm2Instruct()
    {
        // The default ships LFM2.5-1.2B-Instruct-Q5_K_M per design. Instruct (not Thinking)
        // because Tier 2 wants terse direct output, not chain-of-thought. If this ever
        // changes, CHANGELOG and onboarding docs must be updated too.
        LlmModelManager.DefaultModelUrl.Should().Contain("LFM2.5-1.2B-Instruct", "this is the documented v1 default");
        LlmModelManager.DefaultModelFile.Should().EndWith(".gguf");
    }

    [Fact]
    public void FallbackModelUrl_PointsAtKnownCompatibleModel()
    {
        LlmModelManager.FallbackModelUrl.Should().Contain("Qwen2.5-1.5B-Instruct", "fallback is locked-compatible with LLamaSharp 0.25");
        LlmModelManager.FallbackModelFile.Should().EndWith(".gguf");
    }

    [Fact]
    public void IsModelAvailable_ReturnsFalse_WhenFileMissing()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-llm-mgr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            LlmModelManager.IsModelAvailable(tempDir, "nonexistent.gguf").Should().BeFalse();
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public void IsModelAvailable_ReturnsTrue_WhenFilePresent()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-llm-mgr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string file = Path.Combine(tempDir, "fake.gguf");
            File.WriteAllText(file, "x");

            LlmModelManager.IsModelAvailable(tempDir, "fake.gguf").Should().BeTrue();
            LlmModelManager.GetModelPath(tempDir, "fake.gguf").Should().Be(file);
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }

    [Fact]
    public async Task EnsureModelAsync_NoOps_WhenFileAlreadyExists()
    {
        string tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-llm-mgr-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);
        try
        {
            string file = Path.Combine(tempDir, "existing.gguf");
            File.WriteAllText(file, "preexisting");
            long originalLength = new FileInfo(file).Length;

            // A bogus URL is supplied; the method must short-circuit before hitting it.
            await LlmModelManager.EnsureModelAsync(
                tempDir,
                url: "http://localhost:0/should-not-be-fetched",
                fileName: "existing.gguf",
                logger: NullLogger.Instance);

            // File untouched: the pre-existence check should bypass the HTTP path entirely.
            new FileInfo(file).Length.Should().Be(originalLength);
            File.ReadAllText(file).Should().Be("preexisting");
        }
        finally
        {
            Directory.Delete(tempDir, recursive: true);
        }
    }
}
