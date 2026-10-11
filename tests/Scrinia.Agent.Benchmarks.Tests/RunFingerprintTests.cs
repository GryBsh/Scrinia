namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class RunFingerprintTests
{
    [Fact]
    public async Task Identity_ignores_location_and_dictionary_insertion_order()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root);
        var dataset = await CaseSamples.Dataset();
        var first = RunFingerprint.Compute(config, dataset, new Dictionary<string, string> { ["prompt"] = "a", ["payload"] = "b" });
        var moved = config with { Dataset = "moved.json", OutputDirectory = "elsewhere", Scrinia = config.Scrinia with { PayloadDirectory = "moved", Executable = Path.Combine("moved", "scri.exe") } };
        Assert.Equal(first, RunFingerprint.Compute(moved, dataset, new Dictionary<string, string> { ["payload"] = "b", ["prompt"] = "a" }));
    }

    [Fact]
    public async Task Identity_changes_with_every_behavioral_input()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { CaseIds = ["a", "b"] };
        var dataset = await CaseSamples.Dataset();
        var hashes = new Dictionary<string, string> { ["prompt"] = "a", ["sdk"] = "b", ["catalog"] = "c", ["payload"] = "d", ["seed"] = "e" };
        var baseline = RunFingerprint.Compute(config, dataset, hashes);
        var variations = new[] {
            config with { CaseIds = ["b", "a"] },
            config with { Agent = config.Agent with { Model = "changed" } },
            config with { Agent = config.Agent with { ApiKeyEnvironmentVariable = "OTHER_KEY" } },
            config with { Judge = config.Judge with { Temperature = 1 } },
            config with { Limits = config.Limits with { MaxModelAttempts = 3 } },
            config with { Scrinia = config.Scrinia with { WorkspaceConfig = new() { ["plugins:embeddings"] = "false" } } }
        };
        foreach (var changed in variations) Assert.NotEqual(baseline, RunFingerprint.Compute(changed, dataset, hashes));
        Assert.NotEqual(baseline, RunFingerprint.Compute(config, dataset with { Sha256 = "changed" }, hashes));
        foreach (var key in hashes.Keys)
        {
            var changed = new Dictionary<string, string>(hashes) { [key] = "changed" };
            Assert.NotEqual(baseline, RunFingerprint.Compute(config, dataset, changed));
        }
    }
}
