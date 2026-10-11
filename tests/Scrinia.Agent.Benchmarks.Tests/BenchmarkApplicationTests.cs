using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class BenchmarkApplicationTests
{
    [Fact]
    public async Task Offline_command_preflights_runs_resumes_and_refuses_local_drift_before_inference()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload() };
        string file = Path.Combine(temp.Root, "config.json");
        File.WriteAllText(file, JsonSerializer.Serialize(config, ConfigurationJsonContext.Default.RunConfiguration));
        var clients = new Queue<ScriptedChatClient>([
            Capability(), Text("yes"), Text("learned first"), Text("learned second"), Text("candidate"), Text("no"),
            Capability(), Text("yes")]);
        using var output = new StringWriter();
        using var error = new StringWriter();
        int created = 0;
        var app = new BenchmarkApplication(_ => { created++; return clients.Dequeue(); }, output, error);
        Assert.Equal(0, await app.RunAsync(["run", "--config", file], CancellationToken.None));
        Assert.Equal(6, created);
        Assert.Equal(0, await app.RunAsync(["run", "--config", file, "--resume", config.OutputDirectory], CancellationToken.None));
        Assert.Equal(8, created);
        Assert.Equal(2, Directory.GetDirectories(Path.Combine(config.OutputDirectory, "preflight")).Length);
        Assert.Single(Directory.GetDirectories(Path.Combine(config.OutputDirectory, "cases", "000001", "attempts")));
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "summary.json")));
        Assert.Equal(1, report.RootElement.GetProperty("overall").GetProperty("graded").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("overall").GetProperty("correct").GetInt32());
        File.AppendAllText(config.Dataset, " ");
        Assert.Equal(2, await app.RunAsync(["run", "--config", file, "--resume", config.OutputDirectory], CancellationToken.None));
        Assert.Equal(8, created);
    }

    [Fact]
    public async Task Preflight_requires_actual_guide_invocation_and_never_starts_cases()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload() };
        string file = Path.Combine(temp.Root, "config.json");
        File.WriteAllText(file, JsonSerializer.Serialize(config, ConfigurationJsonContext.Default.RunConfiguration));
        using var error = new StringWriter();
        var app = new BenchmarkApplication(_ => Text("ready without tools"), new StringWriter(), error);
        Assert.Equal(2, await app.RunAsync(["run", "--config", file], CancellationToken.None));
        Assert.Contains("PREFLIGHT_FAILED", error.ToString());
        Assert.False(Directory.Exists(Path.Combine(config.OutputDirectory, "cases")));
        Assert.False(File.Exists(Path.Combine(config.OutputDirectory, "manifest.json")));
    }

    internal static ScriptedChatClient Capability() => new(ScriptedChatClient.Call("guide", "guide", []), ScriptedChatClient.Text("ready"));
    internal static ScriptedChatClient Text(string text) => new(ScriptedChatClient.Text(text));

    [Theory]
    [InlineData("")]
    [InlineData("run")]
    [InlineData("unknown")]
    [InlineData("run --config first --config second")]
    [InlineData("run --config config.json --unknown")]
    [InlineData("run --resume root")]
    public async Task Invalid_commands_fail_before_provider_creation(string command)
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        var app = new BenchmarkApplication(_ => throw new InvalidOperationException("Provider must not be created."), output, error);
        Assert.Equal(2, await app.RunAsync(command.Split(' ', StringSplitOptions.RemoveEmptyEntries), CancellationToken.None));
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task Help_succeeds_without_configuration_or_credentials()
    {
        using var output = new StringWriter();
        var app = new BenchmarkApplication(_ => throw new InvalidOperationException("No provider on help."), output);
        Assert.Equal(0, await app.RunAsync(["--help"], CancellationToken.None));
        Assert.Contains("run --config", output.ToString());
        Assert.Contains("--resume", output.ToString());
    }
}
