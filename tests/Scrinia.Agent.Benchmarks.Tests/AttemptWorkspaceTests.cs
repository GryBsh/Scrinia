using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class AttemptWorkspaceTests
{
    [Fact]
    public void Attempts_have_separate_homes_and_cannot_be_reopened()
    {
        using var temp = new ScratchDirectory();
        var settings = CaseSamples.Configuration(temp.Root).Scrinia;
        var isolation = new AttemptWorkspace();
        var root = Path.Combine(temp.Root, "runs", "space \u03bb");
        var first = isolation.Create(root, 1, 1, settings);
        var second = isolation.Create(root, 1, 2, settings);
        Assert.NotEqual(first.Workspace, second.Workspace);
        Assert.NotEqual(first.Home, second.Home);
        File.WriteAllText(Path.Combine(first.Workspace, "retained.txt"), "old attempt");
        Assert.False(File.Exists(Path.Combine(second.Workspace, "retained.txt")));
        Assert.Throws<IOException>(() => isolation.Create(root, 1, 1, settings));
        var environment = isolation.CreateEnvironment(first);
        Assert.Equal(first.Home, environment["SCRINIA_HOME"]);
        foreach (var key in new[] { "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR", "XDG_CONFIG_HOME", "XDG_DATA_HOME", "XDG_CACHE_HOME" })
        {
            Assert.StartsWith(first.Root + Path.DirectorySeparatorChar, environment[key]);
            Assert.True(Directory.Exists(environment[key]));
        }
        if (OperatingSystem.IsWindows()) Assert.Equal(environment["PATH"], environment["Path"]);
        Assert.DoesNotContain("BENCH_AGENT_API_KEY", environment.Keys);
        Assert.DoesNotContain("OPENAI_API_KEY", environment.Keys);
        using var config = JsonDocument.Parse(File.ReadAllText(Path.Combine(first.Workspace, ".scrinia", "config.json")));
        Assert.Equal("local", config.RootElement.GetProperty("Scrinia:Workspace:Tracking").GetString());
        Assert.Equal("false", config.RootElement.GetProperty("Scrinia:Shared:Enabled").GetString());
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData(".scrinia/memory.nmp")]
    [InlineData(".bundle-restore-journal.json")]
    public void Rejects_private_seed_content(string relative)
    {
        using var temp = new ScratchDirectory();
        var settings = CaseSamples.Configuration(temp.Root).Scrinia;
        var seed = Path.Combine(temp.Root, "seed");
        var path = Path.Combine(seed, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "private");
        Assert.Throws<InvalidDataException>(() => new AttemptWorkspace().Create(Path.Combine(temp.Root, "runs"), 1, 1, settings with { HomeSeedDirectory = seed }));
    }

    [Fact]
    public void Copies_only_valid_assets_and_hashes_the_actual_bytes()
    {
        using var temp = new ScratchDirectory();
        var settings = CaseSamples.Configuration(temp.Root).Scrinia;
        var seed = Path.Combine(temp.Root, "seed");
        Directory.CreateDirectory(Path.Combine(seed, "models"));
        File.WriteAllText(Path.Combine(seed, "models", "fixture.gguf"), "bytes");
        var attempt = new AttemptWorkspace().Create(Path.Combine(temp.Root, "runs"), 1, 1, settings with { HomeSeedDirectory = seed });
        Assert.Equal("bytes", File.ReadAllText(Path.Combine(attempt.Home, "models", "fixture.gguf")));
        Assert.Equal(RunFingerprint.HashTree(seed), RunFingerprint.HashTree(attempt.Home));
        File.AppendAllText(Path.Combine(seed, "models", "fixture.gguf"), "changed");
        Assert.NotEqual(RunFingerprint.HashTree(seed), RunFingerprint.HashTree(attempt.Home));
    }

    [Fact]
    public void Rejects_seed_inside_output_and_missing_assets()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        Directory.CreateDirectory(config.OutputDirectory);
        Assert.Throws<InvalidDataException>(() => new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia with { HomeSeedDirectory = config.OutputDirectory }));
        Assert.Throws<InvalidDataException>(() => new AttemptWorkspace().Create(config.OutputDirectory, 1, 1, config.Scrinia with { HomeSeedDirectory = Path.Combine(temp.Root, "missing") }));
    }
}
