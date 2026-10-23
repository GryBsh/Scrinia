using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrinia.Agent.Benchmarks.Tests;

public sealed class EndToEndTests
{
    [Fact]
    public async Task Real_tools_retain_assistant_facts_and_corrections_across_sessions_and_isolate_abstention()
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload() };
        var cases = JsonNode.Parse(File.ReadAllText(config.Dataset))!.AsArray();
        var update = cases[0]!.DeepClone();
        update["question_id"] = "update";
        update["question_type"] = "knowledge-update";
        update["question"] = "Where and when is my appointment?";
        update["answer"] = "Wednesday at Cedar Street";
        update["haystack_sessions"] = JsonNode.Parse("""
            [[{"role":"user","content":"Correction: the appointment is Wednesday."}],
             [{"role":"user","content":"My appointment is Monday."},
              {"role":"assistant","content":"The office is on Cedar Street."}]]
            """);
        cases.Insert(0, update);
        File.WriteAllText(config.Dataset, cases.ToJsonString());
        string file = SaveConfig(temp.Root, config);
        var first = new ScriptedChatClient(Write("appointment", "Monday"), Write("location", "Cedar Street"), ScriptedChatClient.Text("learned"));
        var second = new ScriptedChatClient(Read("appointment"), Write("appointment", "Wednesday"), ScriptedChatClient.Text("updated"));
        var answer = new ScriptedChatClient(Read("appointment"), Read("location"), ScriptedChatClient.Text("Wednesday at Cedar Street"));
        var abstain = new ScriptedChatClient(Read("appointment"), ScriptedChatClient.Text("I do not have a confirmation code."));
        var judge = BenchmarkApplicationTests.Text("yes");
        var clients = new Queue<ScriptedChatClient>([BenchmarkApplicationTests.Capability(), BenchmarkApplicationTests.Text("yes"),
            first, second, answer, judge, BenchmarkApplicationTests.Text("learned"), BenchmarkApplicationTests.Text("learned"), abstain, BenchmarkApplicationTests.Text("yes")]);
        using var error = new StringWriter();
        Assert.Equal(0, await new BenchmarkApplication(_ => clients.Dequeue(), new StringWriter(), error).RunAsync(["run", "--config", file], CancellationToken.None));
        Assert.Empty(clients);
        Assert.Contains("Cedar Street", first.Requests[0].Messages.Last().Text);
        Assert.Contains("2024/06/13", first.Requests[0].Messages.Last().Text);
        Assert.Contains("Monday", ToolReplies(second));
        Assert.Contains("Wednesday", ToolReplies(answer));
        Assert.Contains("Cedar Street", ToolReplies(answer));
        Assert.DoesNotContain("Correction:", answer.Requests[0].Messages.Last().Text);
        Assert.DoesNotContain("Wednesday", ToolReplies(abstain));
        Assert.Contains("status: error", ToolReplies(abstain));
        Assert.True(judge.Requests[0].Options!.Tools is null or { Count: 0 });
        var lines = File.ReadAllLines(Path.Combine(config.OutputDirectory, "answers.jsonl"));
        Assert.Equal(2, lines.Length);
        Assert.Equal("update", JsonDocument.Parse(lines[0]).RootElement.GetProperty("question_id").GetString());
        Assert.Equal("sample_abs", JsonDocument.Parse(lines[1]).RootElement.GetProperty("question_id").GetString());
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "summary.json")));
        Assert.Equal(2, report.RootElement.GetProperty("overall").GetProperty("correct").GetInt32());
        string events = File.ReadAllText(Path.Combine(config.OutputDirectory, "cases/000001/attempts/000001/evidence/events.ndjson"));
        Assert.Contains("\"kind\":\"catalog\"", events);
        Assert.Contains("inputSchema", events);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "manifest.json")));
        Assert.Contains(manifest.RootElement.GetProperty("contentHashes").EnumerateObject(), p => p.Name == "runtimeVersion/Microsoft.Agents.AI.dll");
        KeepProof(temp.Root, "continuity");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Interrupted_evidence_before_or_after_saved_answer_resumes_at_the_correct_boundary(bool afterAnswer)
    {
        using var temp = new ScratchDirectory();
        var config = CaseSamples.Configuration(temp.Root) with { Scrinia = ScriniaMcpSessionTests.Payload() };
        var cases = JsonNode.Parse(File.ReadAllText(config.Dataset))!.AsArray();
        // A single session makes the failure boundary explicit; multi-session continuity is covered above.
        foreach (string key in new[] { "haystack_sessions", "haystack_dates", "haystack_session_ids" })
            cases[0]![key]!.AsArray().RemoveAt(0);
        File.WriteAllText(config.Dataset, cases.ToJsonString());
        string file = SaveConfig(temp.Root, config);
        bool blockedEvidence = false;
        string priorEvidence = Path.Combine(config.OutputDirectory, "cases/000001/attempts/000001/evidence");
        string eventPath = Path.Combine(priorEvidence, "events.ndjson");
        string savedEvents = eventPath + ".blocked";
        void Block()
        {
            // A directory rejects append on every OS; Unix file sharing does not deny this write.
            File.Move(eventPath, savedEvents);
            Directory.CreateDirectory(eventPath);
            blockedEvidence = true;
        }
        var learning = new ScriptedChatClient(Write("partial", "old attempt"), ScriptedChatClient.Text("learned"));
        var judge = BenchmarkApplicationTests.Text("yes");
        if (afterAnswer) judge.OnRequest = Block;
        else learning.OnRequest = () => { if (learning.Requests.Count == 2) Block(); };
        var clients = new Queue<ScriptedChatClient>([BenchmarkApplicationTests.Capability(), BenchmarkApplicationTests.Text("yes"), learning,
            BenchmarkApplicationTests.Text("saved answer"), judge]);
        using var errors = new StringWriter();
        try
        {
            Assert.Equal(1, await new BenchmarkApplication(_ => clients.Dequeue(), afterAnswer ? new FailingReportWriter() : new StringWriter(), errors).RunAsync(["run", "--config", file], CancellationToken.None));
        }
        finally
        {
            if (blockedEvidence)
            {
                Directory.Delete(eventPath);
                File.Move(savedEvents, eventPath);
            }
        }
        Assert.Contains("JOURNAL_FAILED", errors.ToString());
        Assert.False(File.Exists(Path.Combine(priorEvidence, "outcome.json")));
        Assert.Equal(afterAnswer, File.Exists(Path.Combine(priorEvidence, "answer.saved.json")));
        string oldEvents = File.ReadAllText(Path.Combine(priorEvidence, "events.ndjson"));
        Assert.Contains("old attempt", oldEvents);
        var freshLearning = new ScriptedChatClient(Read("partial"), ScriptedChatClient.Text("learned fresh"));
        var retry = new Queue<ScriptedChatClient>([BenchmarkApplicationTests.Capability(), BenchmarkApplicationTests.Text("yes")]);
        if (!afterAnswer)
        {
            retry.Enqueue(freshLearning);
            retry.Enqueue(BenchmarkApplicationTests.Text("new answer"));
        }
        var retriedJudge = BenchmarkApplicationTests.Text("yes");
        retry.Enqueue(retriedJudge);
        Assert.Equal(0, await new BenchmarkApplication(_ => retry.Dequeue(), new StringWriter(), errors).RunAsync(
            ["run", "--config", file, "--resume", config.OutputDirectory], CancellationToken.None));
        Assert.Empty(retry);
        Assert.Equal(oldEvents, File.ReadAllText(Path.Combine(priorEvidence, "events.ndjson")));
        if (afterAnswer) Assert.Contains("saved answer", retriedJudge.Requests.Single().Messages.Single().Text);
        else
        {
            Assert.DoesNotContain("old attempt", ToolReplies(freshLearning));
            Assert.Contains("status: error", ToolReplies(freshLearning));
        }
        using var report = JsonDocument.Parse(File.ReadAllText(Path.Combine(config.OutputDirectory, "summary.json")));
        Assert.Equal(2, report.RootElement.GetProperty("attempts").GetArrayLength());
        Assert.Equal(2, report.RootElement.GetProperty("preflight").GetArrayLength());
        KeepProof(temp.Root, afterAnswer ? "after-answer" : "before-answer");
    }

    private sealed class FailingReportWriter : StringWriter
    {
        public override Task WriteLineAsync(string? value) => throw new IOException("Injected secondary report failure.");
    }

    private static void KeepProof(string source, string scenario)
    {
        var destination = Environment.GetEnvironmentVariable("SCRINIA_AGENT_PROOF_DIRECTORY");
        if (string.IsNullOrWhiteSpace(destination)) return;
        if (!Path.IsPathFullyQualified(destination)) throw new InvalidDataException("Proof directory must be absolute.");
        PathSafety.RequireSeparate(source, destination);
        Copy(source, Path.Combine(destination, scenario + "-" + Guid.NewGuid().ToString("N")));
        static void Copy(string from, string to)
        {
            PathSafety.RequireNoLinks(from);
            PathSafety.RequireNoLinks(to);
            Directory.CreateDirectory(to);
            foreach (string file in Directory.GetFiles(from)) { PathSafety.RequireNoLinks(file); File.Copy(file, Path.Combine(to, Path.GetFileName(file))); }
            foreach (string directory in Directory.GetDirectories(from)) Copy(directory, Path.Combine(to, Path.GetFileName(directory)));
        }
    }

    private static string SaveConfig(string root, RunConfiguration config)
    {
        string path = Path.Combine(root, "config.json");
        File.WriteAllText(path, JsonSerializer.Serialize(config, ConfigurationJsonContext.Default.RunConfiguration));
        return path;
    }
    private static Microsoft.Extensions.AI.ChatResponse Write(string name, string text) => ScriptedChatClient.Call("write-" + name, "remember", new()
        { ["path"] = "/facts/" + name, ["description"] = "Stored facts for this test.", ["content"] = (string[])[text] });
    private static Microsoft.Extensions.AI.ChatResponse Read(string name) => ScriptedChatClient.Call("read-" + name, "recall", new() { ["path"] = "/facts/" + name });
    private static string ToolReplies(ScriptedChatClient client) => string.Join("\n", client.Requests.SelectMany(r => r.Messages)
        .Where(m => m.Role == Microsoft.Extensions.AI.ChatRole.Tool).SelectMany(m => m.Contents)
        .Select(c => ChatEvidence.Build(w => { w.WritePropertyName("content"); ChatEvidence.WriteContent(w, c); }).GetRawText()));
}
