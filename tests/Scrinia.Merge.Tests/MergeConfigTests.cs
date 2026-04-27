using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class MergeConfigTests : IDisposable
{
    private readonly string _tempDir;

    public MergeConfigTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-merge-config-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
            Directory.Delete(_tempDir, recursive: true);
    }

    [Fact]
    public void Config_ValidFile_Loads()
    {
        // Write config JSON manually to avoid using internal MergeConfigJsonContext
        string configContent = """{"JaccardThreshold":0.85,"Resolver":"none","ResolverCommand":null,"ConflictDir":"my-conflicts"}""";
        File.WriteAllText(Path.Combine(_tempDir, "merge.config"), configContent);

        var config = MergeConfig.Load(_tempDir);

        config.JaccardThreshold.Should().Be(0.85);
        config.ConflictDir.Should().Be("my-conflicts");
    }

    [Fact]
    public void Config_MissingFile_Defaults()
    {
        var config = MergeConfig.Load(_tempDir);

        config.JaccardThreshold.Should().Be(0.7);
        config.ConflictDir.Should().Be("conflict");
        config.Resolver.Should().Be("none");
        config.ResolverCommand.Should().BeNull();
    }

    [Fact]
    public void Config_InvalidJson_Defaults()
    {
        File.WriteAllText(Path.Combine(_tempDir, "merge.config"), "not valid json {{{");

        var config = MergeConfig.Load(_tempDir);

        config.JaccardThreshold.Should().Be(0.7,
            because: "invalid JSON should fall back to defaults");
    }
}
