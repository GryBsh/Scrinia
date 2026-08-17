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
        // Write config JSON manually to avoid using internal MergeConfigJsonContext. merge.config
        // on disk uses camelCase keys.
        //
        // "resolver" and "resolverCommand" are DELIBERATE, not leftovers: MergeConfig no longer
        // has those members, and this fixture is the forward-compat check that an existing
        // on-disk merge.config written before they were dropped still loads. System.Text.Json
        // skips unmapped members, so the assertions below must keep passing — if they ever stop,
        // reading a real user's older config has become a breaking change.
        string configContent = """{"jaccardThreshold":0.85,"resolver":"none","resolverCommand":null,"conflictDir":"my-conflicts"}""";
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
