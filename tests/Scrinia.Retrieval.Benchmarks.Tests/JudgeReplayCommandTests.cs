using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Cli;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Judge;
using Scrinia.Retrieval.Benchmarks.Llm;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

/// <summary>
/// Tests for <see cref="JudgeReplayCommand"/> — the dataset-free judge-replay verb that
/// re-judges only the persisted <see cref="QuestionResult.JudgeContext"/>/<see
/// cref="QuestionResult.GoldAnswer"/> from a prior run, with no dataset or retriever
/// involved (Task 4 already persisted the exact date-prefixed answerer context).
/// </summary>
public sealed class JudgeReplayCommandTests : IDisposable
{
    private readonly string _cacheFile = Path.Combine(Path.GetTempPath(), $"judge_replay_{Guid.NewGuid():N}.ndjson");
    private readonly List<string> _tempFiles = [];

    public void Dispose()
    {
        try { File.Delete(_cacheFile); } catch { }
        foreach (var f in _tempFiles)
        {
            try { File.Delete(f); } catch { }
        }
    }

    private sealed class ScriptedLlmClient : IBenchLlmClient
    {
        private readonly Queue<string?> _replies;
        public ScriptedLlmClient(params string?[] replies) => _replies = new(replies);
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<string?> CompleteAsync(string s, string u, int m, bool j, CancellationToken ct) =>
            Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : null);
    }

    private AnswerabilityJudge StubJudge(string answer, string grade) =>
        new(new ScriptedLlmClient(answer, grade), new LlmResponseCache(_cacheFile), "test", "test-model", judgeK: 10);

    private static QuestionResult OneQuestion(IReadOnlyList<string>? judgeContext, string? goldAnswer) =>
        new(
            QuestionId: "q1",
            Query: "When did I go?",
            Category: "single-hop",
            GroundTruthIds: [],
            GroundTruthTexts: [],
            Retrieved: [],
            RecallAt5: 1.0,
            RecallAt10: 1.0,
            RecallAt50: 1.0,
            Ndcg10: 1.0,
            GoldAnswer: goldAnswer,
            JudgeContext: judgeContext);

    private static BenchmarkRunResult OneQuestionResult(IReadOnlyList<string>? judgeContext, string? goldAnswer)
    {
        var question = OneQuestion(judgeContext, goldAnswer);
        var meta = new RunMetadata(
            Benchmark: "longmemeval",
            Mode: "bm25_embeddings",
            TopK: 10,
            StartedAt: DateTimeOffset.UtcNow,
            CompletedAt: DateTimeOffset.UtcNow,
            QuestionCount: 1,
            ScriniaVersion: "test",
            Granularity: "session");
        var aggregate = BenchmarkScorer.Aggregate([question]);
        var perCategory = BenchmarkScorer.PerCategory([question]);
        return new BenchmarkRunResult(meta, aggregate, perCategory, [question]);
    }

    [Fact]
    public async Task ReplayAsync_judges_over_persisted_context_and_sets_all_fields()
    {
        var input = OneQuestionResult(
            judgeContext: ["created: 2023-05-04\nI went yesterday"],
            goldAnswer: "May 4, 2023");
        var judge = StubJudge(answer: "May 4, 2023", grade: "correct");

        var outp = await JudgeReplayCommand.ReplayAsync(input, judge, CancellationToken.None);

        var q = outp.Questions[0];
        Assert.True(q.SurfacedFromContext);
        Assert.True(q.AnsweredCorrectly);
        Assert.True(q.DateSurfaced);
        Assert.Equal(1.0, outp.Aggregate.CorrectRate, 5);
        Assert.Equal(BenchmarkRunner.SessionDateScenario, outp.RunMetadata.TemporalScenario);
        // Granularity is preserved from the original run, not clobbered by replay.
        Assert.Equal("session", outp.RunMetadata.Granularity);
    }

    [Fact]
    public async Task ReplayAsync_skips_questions_without_persisted_context()
    {
        var input = OneQuestionResult(judgeContext: null, goldAnswer: "x"); // pre-existing file shape
        var outp = await JudgeReplayCommand.ReplayAsync(input, StubJudge("x", "correct"), CancellationToken.None);
        Assert.Null(outp.Questions[0].AnsweredCorrectly); // untouched
    }

    [Fact]
    public async Task ReplayAsync_skips_questions_with_blank_gold_answer()
    {
        var input = OneQuestionResult(judgeContext: ["created: 2023-05-04\nI went yesterday"], goldAnswer: "  ");
        var outp = await JudgeReplayCommand.ReplayAsync(input, StubJudge("x", "correct"), CancellationToken.None);
        Assert.Null(outp.Questions[0].AnsweredCorrectly); // untouched — no gold to grade against
    }

    /// <summary>Minimal loopback OpenAI-compatible fake so <see cref="JudgeReplayCommand.RunAsync"/>
    /// can be exercised end to end (real <see cref="RetrieverFactory.BuildJudgeAsync"/> wiring,
    /// real HTTP client) without any external network dependency. First POST answers the
    /// answerer call, every POST after that answers "correct" for the grader.</summary>
    private sealed class FakeJudgeServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Task _loop;
        private int _postCount;

        public FakeJudgeServer()
        {
            int port = Random.Shared.Next(20000, 40000);
            BaseUrl = $"http://127.0.0.1:{port}/v1";
            _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
            _listener.Start();
            _loop = Task.Run(AcceptLoopAsync);
        }

        public string BaseUrl { get; }

        private async Task AcceptLoopAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception) { return; }
                _ = HandleAsync(ctx);
            }
        }

        private async Task HandleAsync(HttpListenerContext ctx)
        {
            string body;
            if (ctx.Request.HttpMethod == "GET")
            {
                body = """{"data":[]}""";
            }
            else
            {
                int n = Interlocked.Increment(ref _postCount);
                string content = n == 1 ? "May 4, 2023" : "correct";
                body = $$"""{"id":"x","model":"m","choices":[{"index":0,"message":{"role":"assistant","content":{{JsonSerializer.Serialize(content)}}},"finish_reason":"stop"}]}""";
            }
            byte[] bytes = Encoding.UTF8.GetBytes(body);
            ctx.Response.ContentType = "application/json";
            ctx.Response.ContentLength64 = bytes.Length;
            await ctx.Response.OutputStream.WriteAsync(bytes);
            ctx.Response.OutputStream.Close();
        }

        public void Dispose()
        {
            _listener.Stop();
            _listener.Close();
        }
    }

    [Fact]
    public async Task RunAsync_writes_sibling_judged_file_and_leaves_input_byte_unchanged()
    {
        using var server = new FakeJudgeServer();
        string inputPath = Path.Combine(Path.GetTempPath(), $"judge_replay_input_{Guid.NewGuid():N}.json");
        _tempFiles.Add(inputPath);
        string judgedPath = Path.Combine(Path.GetTempPath(), $"judge_replay_input_{Guid.NewGuid():N}.judged.json");

        var input = OneQuestionResult(
            judgeContext: ["created: 2023-05-04\nI went yesterday"],
            goldAnswer: "May 4, 2023");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(input, Program.JsonOutputOptions));
        byte[] originalBytes = await File.ReadAllBytesAsync(inputPath);

        judgedPath = inputPath[..^".json".Length] + ".judged.json";
        _tempFiles.Add(judgedPath);

        string[] args =
        [
            "--input", inputPath,
            "--judge",
            "--judge-provider", "openai",
            "--judge-model", "test-model",
            "--judge-base-url", server.BaseUrl,
        ];

        int exitCode = await JudgeReplayCommand.RunAsync(args, CancellationToken.None);

        exitCode.Should().Be(0);
        File.Exists(judgedPath).Should().BeTrue();

        byte[] bytesAfter = await File.ReadAllBytesAsync(inputPath);
        bytesAfter.Should().Equal(originalBytes, "judge-replay must never mutate the original input file");

        var judged = JsonSerializer.Deserialize<BenchmarkRunResult>(
            await File.ReadAllTextAsync(judgedPath), Program.JsonOutputOptions)!;
        judged.Questions[0].AnsweredCorrectly.Should().BeTrue();
        judged.Questions[0].JudgeAnswer.Should().Be("May 4, 2023");
        judged.RunMetadata.TemporalScenario.Should().Be(BenchmarkRunner.SessionDateScenario);
    }

    [Fact]
    public async Task RunAsync_ReturnsNonZero_AndWritesNothing_WhenJudgeNotConfigured()
    {
        string inputPath = Path.Combine(Path.GetTempPath(), $"judge_replay_input_{Guid.NewGuid():N}.json");
        _tempFiles.Add(inputPath);
        string judgedPath = inputPath[..^".json".Length] + ".judged.json";
        _tempFiles.Add(judgedPath);

        var input = OneQuestionResult(judgeContext: ["created: 2023-05-04\nI went yesterday"], goldAnswer: "May 4, 2023");
        await File.WriteAllTextAsync(inputPath, JsonSerializer.Serialize(input, Program.JsonOutputOptions));
        byte[] originalBytes = await File.ReadAllBytesAsync(inputPath);

        int exitCode = await JudgeReplayCommand.RunAsync(["--input", inputPath], CancellationToken.None);

        exitCode.Should().NotBe(0);
        File.Exists(judgedPath).Should().BeFalse();
        (await File.ReadAllBytesAsync(inputPath)).Should().Equal(originalBytes);
    }
}
