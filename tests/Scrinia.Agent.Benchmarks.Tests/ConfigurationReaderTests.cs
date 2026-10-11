using System.Text.Json.Nodes;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class ConfigurationReaderTests
{
    [Fact]
    public void Accepts_framework_dependent_payload_as_one_argument()
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        var host = temp.Write("dotnet.exe", "host identity");
        File.WriteAllText(Path.Combine(temp.Root, "payload", "scri.dll"), "managed payload");
        json["scrinia"]!["executable"] = host;
        json["scrinia"]!["prefixArguments"] = new JsonArray("payload/scri.dll");
        var actual = new ConfigurationReader().Read(temp.Write("config.json", json.ToJsonString()));
        Assert.Equal([Path.Combine(temp.Root, "payload", "scri.dll")], actual.Scrinia.PrefixArguments);
    }

    [Theory]
    [InlineData("scri-plugin-embeddings")]
    [InlineData("../outside")]
    public void Requested_plugin_must_be_an_available_seed_asset(string plugin)
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        json["scrinia"]!["workspaceConfig"]!["plugins:embeddings"] = plugin;
        Assert.Throws<InvalidDataException>(() => new ConfigurationReader().Read(temp.Write("config.json", json.ToJsonString())));
    }

    [Fact]
    public void Resolves_paths_from_configuration_and_preserves_argument_array()
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        json["dataset"] = "dataset.json";
        json["outputDirectory"] = "runs";
        json["scrinia"]!["payloadDirectory"] = "payload";
        json["scrinia"]!["executable"] = "scri.exe";
        var actual = new ConfigurationReader().Read(temp.Write("config.json", json.ToJsonString()));
        Assert.Equal(Path.Combine(temp.Root, "dataset.json"), actual.Dataset);
        Assert.Equal(Path.Combine(temp.Root, "payload", "scri.exe"), actual.Scrinia.Executable);
        Assert.Equal(Path.Combine(temp.Root, "runs"), actual.OutputDirectory);
        Assert.Empty(actual.Scrinia.PrefixArguments);
    }

    [Theory]
    [InlineData("limits.maxModelTurnsPerEpisode", "0")]
    [InlineData("limits.maxModelTurnsPerEpisode", "1025")]
    [InlineData("limits.episodeSeconds", "-1")]
    [InlineData("limits.episodeSeconds", "86401")]
    [InlineData("limits.caseSeconds", "1")]
    [InlineData("limits.caseSeconds", "604801")]
    [InlineData("limits.maxModelAttempts", "0")]
    [InlineData("limits.maxModelAttempts", "6")]
    [InlineData("agent.endpoint", "\"relative/v1\"")]
    [InlineData("agent.endpoint", "\"https://user:secret@host/v1\"")]
    [InlineData("agent.endpoint", "\"https://host/v1?secret=value\"")]
    [InlineData("agent.endpoint", "\"https://host/v1#fragment\"")]
    [InlineData("agent.model", "\" \"")]
    [InlineData("agent.temperature", "2.1")]
    [InlineData("judge.maxOutputTokens", "131073")]
    [InlineData("judge.maxOutputTokens", "0")]
    [InlineData("judge.apiKeyEnvironmentVariable", "\"bad key\"")]
    [InlineData("schemaVersion", "2")]
    [InlineData("unknown", "true")]
    [InlineData("agent.unknown", "true")]
    [InlineData("scrinia.prefixArguments", "\"scri.dll\"")]
    [InlineData("scrinia.prefixArguments", "[\"serve\",\"--workspace-root\",\"elsewhere\"]")]
    [InlineData("caseIds", "[\"same\",\"same\"]")]
    public void Rejects_invalid_configuration(string field, string value)
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        var parts = field.Split('.');
        var target = parts.Length == 1 ? json : json[parts[0]]!.AsObject();
        target[parts[^1]] = JsonNode.Parse(value);
        var path = temp.Write("config.json", json.ToJsonString());
        Assert.Throws<InvalidDataException>(() => new ConfigurationReader().Read(path));
    }

    [Theory]
    [InlineData("dataset")]
    [InlineData("caseIds")]
    [InlineData("limits")]
    [InlineData("agent")]
    [InlineData("outputDirectory")]
    public void Rejects_missing_required_property(string field)
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        json.Remove(field);
        Assert.Throws<InvalidDataException>(() => new ConfigurationReader().Read(temp.Write("config.json", json.ToJsonString())));
    }

    [Theory]
    [InlineData("payload")]
    [InlineData("dataset.json")]
    [InlineData(".")]
    public void Rejects_output_overlapping_inputs(string output)
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        json["outputDirectory"] = output;
        Assert.Throws<InvalidDataException>(() => new ConfigurationReader().Read(temp.Write("config.json", json.ToJsonString())));
    }

    [Theory]
    [InlineData("Scrinia:Shared:Enabled", "true")]
    [InlineData("Scrinia:Workspace:Tracking", "project-git")]
    [InlineData("Scrinia:StorePath", "outside")]
    [InlineData("Provider:ApiKey", "secret")]
    public void Rejects_workspace_settings_that_break_isolation(string key, string value)
    {
        using var temp = new ScratchDirectory();
        var json = CaseSamples.ConfigurationJson(temp.Root);
        json["scrinia"]!["workspaceConfig"]![key] = value;
        Assert.Throws<InvalidDataException>(() => new ConfigurationReader().Read(temp.Write("config.json", json.ToJsonString())));
    }
}
