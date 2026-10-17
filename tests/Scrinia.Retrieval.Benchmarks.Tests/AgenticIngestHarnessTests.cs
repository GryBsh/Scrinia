using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Search;
using Scrinia.Mcp;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Scrinia.Retrieval.Benchmarks.Llm.Providers;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticIngestHarnessTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"scrinia_agentic_{Guid.NewGuid():N}");
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private sealed class ScriptedToolClient : IToolCallingClient
    {
        private readonly Queue<ToolTurnResult?> _turns;
        public ScriptedToolClient(params ToolTurnResult?[] turns) => _turns = new(turns);
        public Task<ToolTurnResult?> ChatWithToolsAsync(
            IReadOnlyList<ToolChatMessage> m, IReadOnlyList<ToolSpec> t, int mt, CancellationToken ct)
            => Task.FromResult(_turns.Count > 0 ? _turns.Dequeue() : new ToolTurnResult(null, [], "stop"));
    }

    [Fact]
    public async Task Episode_ExecutesRememberCall_WritesToStore()
    {
        Directory.CreateDirectory(_dir);
        var store = new FileMemoryStore(_dir);
        MemoryStoreContext.Current = store;
        try
        {
            var remember = new ToolCallRequest("c1", "remember",
                """{"path":"/findings/lisbon","content":["Booked Lisbon.\nref: d1"]}""");
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [remember], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "Maria: Booked Lisbon.", null, "s1") };

            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);

            result.Succeeded.Should().BeTrue();
            result.ToolCalls.Should().ContainSingle(c => c.ToolName == "remember");
            store.SearchAll(new SearchQuery("Lisbon") { Limit = 10 }).Should().NotBeEmpty();
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Episode_ReturnsFailure_WhenClientReturnsNull()
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            var client = new ScriptedToolClient((ToolTurnResult?)null);
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "x", null, "s1") };
            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);
            result.Succeeded.Should().BeFalse("a null turn is a transport failure → fallback");
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Episode_MalformedArgs_ReturnsErrorToModel_AndSurvives()
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            var bad = new ToolCallRequest("c1", "remember", "{ not json");
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [bad], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "x", null, "s1") };
            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);
            result.Succeeded.Should().BeTrue("malformed args don't kill the episode");
            result.ToolCallErrors.Should().Be(1);
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Episode_NullArgs_ReturnsErrorToModel_AndSurvives()
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            var bad = new ToolCallRequest("c1", "remember", null!);
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [bad], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "x", null, "s1") };
            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);
            result.Succeeded.Should().BeTrue("null args don't kill the episode");
            result.ToolCallErrors.Should().Be(1);
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Theory]
    [InlineData("file" + ":///tmp/not-a-memory", "UNSUPPORTED_INPUT_FORM")]
    [InlineData("/", "MALFORMED_ADDRESS")]
    public async Task Episode_StructuredAddressErrorResponse_CountsAsToolError(
        string path, string expectedErrorCode)
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            string structuredResponse = await new ScriniaMcpTools().MemoryRemember(path, ["content"]);
            structuredResponse.Should().Contain($"errorCode: {expectedErrorCode}");

            var bad = new ToolCallRequest("c1", "remember",
                $"{{\"path\":\"{path}\",\"content\":[\"content\"]}}");
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [bad], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("d1", "x", null, "s1") };

            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);

            result.Succeeded.Should().BeTrue("structured MCP errors are returned to the model");
            result.ToolCallErrors.Should().Be(1);
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Replay_ReproducesStoreState_WithoutClient()
    {
        Directory.CreateDirectory(_dir);
        MemoryStoreContext.Current = new FileMemoryStore(_dir);
        try
        {
            var calls = new List<EpisodeToolCall>
            {
                new("remember", """{"path":"/f/x","content":["hello world\nref: d1"]}"""),
            };
            await AgenticIngestHarness.ReplayAsync(calls, CancellationToken.None);
            ((FileMemoryStore)MemoryStoreContext.Current).SearchAll(new SearchQuery("hello") { Limit = 10 })
                .Should().NotBeEmpty();
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task Append_AccumulatesOntoRemember_DoesNotClobber()
    {
        Directory.CreateDirectory(_dir);
        var store = new FileMemoryStore(_dir);
        MemoryStoreContext.Current = store;
        try
        {
            var remember = new ToolCallRequest("c1", "remember",
                """{"path":"/findings/x","content":["FIRST-FACT about Lisbon.\nref: D1:1"]}""");
            var append = new ToolCallRequest("c2", "append",
                """{"path":"/findings/x","content":"SECOND-FACT about the flight.\nref: D1:2"}""");
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [remember], "tool_calls"),
                new ToolTurnResult(null, [append], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("D1:1", "Maria: Booked Lisbon.", null, "s1") };

            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);

            result.Succeeded.Should().BeTrue();
            result.ToolCallErrors.Should().Be(0);

            // Read the stored memory back: BOTH facts must be present (append accumulated,
            // remember did NOT clobber the first).
            string recalled = await new ScriniaMcpTools().MemoryRecall("/findings/x", null, false, CancellationToken.None);
            recalled.Should().Contain("FIRST-FACT");
            recalled.Should().Contain("SECOND-FACT");
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Replay_remember_honors_explicit_replacement(bool replace)
    {
        Directory.CreateDirectory(_dir);
        using var store = new FileMemoryStore(_dir);
        MemoryStoreContext.Current = store;
        try
        {
            var calls = new List<EpisodeToolCall>
            {
                new("remember", "{\"path\":\"/facts/move\",\"content\":[\"five hours\"]}"),
                new("remember", "{\"path\":\"/facts/move\",\"content\":[\"train commute\"],\"replace\":" + (replace ? "true" : "false") + "}")
            };
            await AgenticIngestHarness.ReplayAsync(calls, CancellationToken.None);
            string recalled = await new ScriniaMcpTools().MemoryRecall("/facts/move");
            recalled.Should().Contain("train commute");
            recalled.Contains("five hours", StringComparison.Ordinal).Should().Be(!replace);
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public void PromptVersion_IsV4_AfterRememberBecameAppendByDefault() =>
        AgenticIngestPrompts.PromptVersion.Should().Be("v4");
}
