using System.Text.Json;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class BenchmarkApplicationTests
{
    [Theory]
    [InlineData("syntax", "Invalid configuration JSON")]
    [InlineData("dataset", "question_date")]
    [InlineData("existing", "--resume")]
    [InlineData("credential", "BENCH_MISSING_")]
    public async Task Validation_explains_the_fix_without_echoing_raw_values(string scenario, string expected)
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload() };
        if (scenario == "credential") config = config with { Agent = config.Agent with { ApiKeyEnvironmentVariable = "BENCH_MISSING_" + Guid.NewGuid().ToString("N") } };
        string file = Path.Combine(temp.Root, "config.json");
        File.WriteAllText(file, scenario == "syntax" ? "{\"agent\": secret-sentinel" : JsonSerializer.Serialize(config, ConfigurationJsonContext.Default.RunConfiguration));
        if (scenario == "dataset") File.WriteAllText(config.Dataset, File.ReadAllText(config.Dataset).Replace("question_date", "missing_date", StringComparison.Ordinal));
        if (scenario == "existing") { Directory.CreateDirectory(config.OutputDirectory); File.WriteAllText(Path.Combine(config.OutputDirectory, "manifest.json"), "{}"); }
        using var error = new StringWriter();
        var app = new BenchmarkApplication(output: new StringWriter(), error: error);
        Assert.Equal(2, await app.RunAsync(["run", "--config", file], CancellationToken.None));
        Assert.Contains(expected, error.ToString());
        Assert.DoesNotContain("secret-sentinel", error.ToString());
    }

    [Fact]
    public async Task User_cancellation_during_preflight_returns_130()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload() };
        string file = Path.Combine(temp.Root, "config.json");
        File.WriteAllText(file, JsonSerializer.Serialize(config, ConfigurationJsonContext.Default.RunConfiguration));
        using var cancellation = new CancellationTokenSource();
        var client = Text("ready");
        client.OnRequest = cancellation.Cancel;
        using var error = new StringWriter();
        Assert.Equal(130, await new BenchmarkApplication(_ => client, new StringWriter(), error).RunAsync(["run", "--config", file], cancellation.Token));
        Assert.Contains("CANCELLED", error.ToString());
        Assert.False(File.Exists(Path.Combine(config.OutputDirectory, "manifest.json")));
    }

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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Exhausted_endpoint_outage_stops_before_next_case_and_resume_retries_unfinished_work(bool duringJudge)
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with
        {
            Scrinia = ScriniaMcpSessionTests.Payload(),
            Limits = new(16, 60, 600, 1)
        };
        var dataset = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(config.Dataset))!.AsArray();
        var next = dataset[0]!.DeepClone();
        next["question_id"] = "sample_next";
        dataset.Add(next);
        File.WriteAllText(config.Dataset, dataset.ToJsonString());
        string file = Path.Combine(temp.Root, "config.json");
        File.WriteAllText(file, JsonSerializer.Serialize(config, ConfigurationJsonContext.Default.RunConfiguration));
        var clients = new Queue<ScriptedChatClient>([Capability(), Text("yes")]);
        if (duringJudge)
            foreach (var value in new[] { "learned first", "learned second", "candidate" }) clients.Enqueue(Text(value));
        clients.Enqueue(new ScriptedChatClient(new HttpRequestException("private server error", null, System.Net.HttpStatusCode.BadGateway)));
        using var output = new StringWriter();
        using var error = new StringWriter();
        var app = new BenchmarkApplication(_ => clients.Dequeue(), output, error);
        Assert.Equal(1, await app.RunAsync(["run", "--config", file], CancellationToken.None));
        Assert.Contains("PROVIDER_UNAVAILABLE", error.ToString());
        Assert.DoesNotContain("private server error", error.ToString());
        Assert.False(Directory.Exists(Path.Combine(config.OutputDirectory, "cases", "000002")));
        using (var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "summary.json"))))
            Assert.Equal(1, report.RootElement.GetProperty("states").GetProperty("Pending").GetInt32());
        string evidence = Path.Combine(config.OutputDirectory, "cases", "000001", "attempts", "000001", "evidence");
        using (var outcome = JsonDocument.Parse(File.ReadAllText(Path.Combine(evidence, "outcome.json"))))
        {
            Assert.Equal("PROVIDER_UNAVAILABLE", outcome.RootElement.GetProperty("outcome").GetProperty("failureCode").GetString());
            Assert.Equal(duringJudge ? "GradingFailed" : "ExecutionFailed", outcome.RootElement.GetProperty("outcome").GetProperty("state").GetString());
        }
        string? saved = duringJudge ? File.ReadAllText(Path.Combine(evidence, "answer.json")) : null;
        clients.Enqueue(Capability()); clients.Enqueue(Text("yes"));
        if (!duringJudge)
            foreach (var value in new[] { "learned first", "learned second", "candidate" }) clients.Enqueue(Text(value));
        clients.Enqueue(Text("yes"));
        foreach (var value in new[] { "learned first", "learned second", "candidate", "no" }) clients.Enqueue(Text(value));
        Assert.Equal(0, await app.RunAsync(["run", "--config", file, "--resume", config.OutputDirectory], CancellationToken.None));
        Assert.Empty(clients);
        if (duringJudge) Assert.Equal(saved, File.ReadAllText(Path.Combine(evidence, "answer.json")));
        using var final = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "summary.json")));
        Assert.Equal(2, final.RootElement.GetProperty("overall").GetProperty("graded").GetInt32());
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
