using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the compact restore response format.
/// Restore now returns a small summary (~800B) with a followUp list
/// of memory names instead of inlining 30KB of content.
/// </summary>
public sealed class RestoreTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public RestoreTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ── Helpers ────────────────────────────────────────────────────────────────

    private async Task InitProjectWithMemories()
    {
        await ScriniaProjectTools.ProjectInit("Goals: build X\nConstraints: none", cancellationToken: CancellationToken.None);
        await _memTools.Store(
            content: ["## Agent norms\nAlways verify before commit.\nPrefer small PRs."],
            name: "agent:profile",
            cancellationToken: CancellationToken.None);
        await _memTools.Store(
            content: ["## Learned patterns\nRetry with exponential backoff.\nCircuit breaker for external calls."],
            name: "patterns:resilience",
            cancellationToken: CancellationToken.None);
        await _memTools.Store(
            content: ["## Checkpoint\nLast completed: task 42\nPhase: 01"],
            name: "checkpoint:latest",
            cancellationToken: CancellationToken.None);
        await _memTools.Store(
            content: ["## Session log\n- Reviewed auth module\n- Fixed retry bug"],
            name: "sessions:2026-01-01",
            cancellationToken: CancellationToken.None);
    }

    // ── Test 1: Compact summary ───────────────────────────────────────────────

    [Fact]
    public async Task Restore_ReturnsCompactSummary_Under2KB()
    {
        // Arrange
        await InitProjectWithMemories();

        // Act
        string result = await _memTools.Memory("restore", cancellationToken: CancellationToken.None);

        // Assert — the full YAML response should be compact
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Content.Should().NotBeNullOrEmpty();

        // Content must be compact — should NOT inline the full memory contents
        parsed.Content!.Length.Should().BeLessThan(2048,
            because: "restore content should be a compact project state summary, not inlined memories");

        // Content should NOT contain the inlined memory bodies
        parsed.Content.Should().NotContain("## Agent norms",
            because: "agent norms should be in followUp, not inlined in content");
        parsed.Content.Should().NotContain("## Learned patterns",
            because: "learned patterns should be in followUp, not inlined in content");
    }

    // ── Test 2: FollowUp contains expected memories ───────────────────────────

    [Fact]
    public async Task Restore_FollowUp_ContainsExpectedMemories()
    {
        // Arrange
        await InitProjectWithMemories();

        // Act
        string result = await _memTools.Memory("restore", cancellationToken: CancellationToken.None);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.FollowUp.Should().NotBeEmpty(because: "followUp should list memories the agent should load");

        parsed.FollowUp.Should().Contain("/agent/profile",
            because: "agent norms should be surfaced for follow-up loading");
        parsed.FollowUp.Should().Contain("/patterns/resilience",
            because: "learned patterns should be surfaced for follow-up loading");
        parsed.FollowUp.Should().Contain("/checkpoint/latest",
            because: "checkpoint should be surfaced for follow-up loading");

        // Session log: restore looks for today's date, not 2026-01-01
        // So sessions:2026-01-01 won't appear unless today IS 2026-01-01.
        // We stored sessions:2026-01-01, but restore looks for sessions:{today}.
        // Let's verify ordering: agent entries come before patterns entries.
        int agentIndex = parsed.FollowUp.FindIndex(x => x.StartsWith("/agent/"));
        int patternsIndex = parsed.FollowUp.FindIndex(x => x.StartsWith("/patterns/"));
        agentIndex.Should().BeLessThan(patternsIndex,
            because: "agent entries should come before patterns entries in followUp ordering");
    }

    // ── Test 3: FollowUp empty when no enrichment memories exist ──────────────

    [Fact]
    public async Task Restore_FollowUp_ContainsOnlySeededAgentFiles()
    {
        // Arrange — project only, no user-created agent/patterns/checkpoint/session memories
        await ScriniaProjectTools.ProjectInit("Goals: bare project\nConstraints: none", cancellationToken: CancellationToken.None);

        // Act
        string result = await _memTools.Memory("restore", cancellationToken: CancellationToken.None);

        // Assert — followUp should contain only the seeded built-in agent files
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.FollowUp.Should().OnlyContain(
            name => name.StartsWith("/agent/"),
            because: "only seeded built-in agent files should appear — no user-created memories exist");
    }

    // ── Test 4: Instruction includes follow-up guidance ───────────────────────

    [Fact]
    public async Task Restore_InstructionIncludesFollowUpGuidance()
    {
        // Arrange — need at least one followUp entry to trigger guidance
        await ScriniaProjectTools.ProjectInit("Goals: build X\nConstraints: none", cancellationToken: CancellationToken.None);
        await _memTools.Store(
            content: ["## Profile\nCareful code reviewer."],
            name: "agent:profile",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await _memTools.Memory("restore", cancellationToken: CancellationToken.None);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.FollowUp.Should().NotBeEmpty(because: "agent:profile should appear in followUp");

        // The instruction should guide the agent to load followUp items
        parsed.Instruction.Should().NotBeNullOrEmpty(
            because: "when followUp is non-empty, instruction should include loading guidance");
        (parsed.Instruction!.Contains("followUp") || parsed.Instruction.Contains("memory('show')"))
            .Should().BeTrue(
                because: "instruction should mention followUp or memory('show') so the agent knows to load them");
    }

    // ── Test 5: Existing progress test still works with new format ────────────

    [Fact]
    public async Task Restore_ShowsComputedProgress_InCompactFormat()
    {
        // Arrange — same setup as the existing Restore_ShowsComputedProgress_NotStaleStateProgress
        await ScriniaProjectTools.ProjectInit("Goals: test restore progress", CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Test", CancellationToken.None);
        await ScriniaProjectTools.PlanTasks("01",
            "## Task 01\nWave: 1\nDepends on: none\nAction: first\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nWave: 1\nDepends on: none\nAction: second\nAcceptance criteria:\n- done",
            CancellationToken.None);

        // Complete 1 of 7 tasks (2 user + 5 gates) => 14%
        await ScriniaProjectTools.TaskComplete("task:01-1-01", "completed", CancellationToken.None);

        // Overwrite project:state with stale progress
        await _memTools.Store(
            content: [
                "Project: test_restore_progress\n" +
                "ID: test\n" +
                "Phase: Phase 01\n" +
                "Progress: 20%\n" +
                "Last action: stale\n" +
                "Blockers: none\n" +
                "Next: nothing"
            ],
            name: "project:state",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await _memTools.Memory("restore", cancellationToken: CancellationToken.None);

        // Assert — compact format still contains computed progress in content
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Content.Should().Contain("Progress: 14%",
            because: "restore should replace stale progress with computed value even in compact format");
        parsed.Content.Should().NotContain("Progress: 20%",
            because: "stale progress value must not survive restore");

        // Also verify the response is compact (no inlined memory bodies)
        parsed.Content!.Length.Should().BeLessThan(2048,
            because: "compact restore should keep content small even with active tasks");
    }

    // ── Test 6: Today's session log appears in followUp ───────────────────────

    [Fact]
    public async Task Restore_FollowUp_IncludesTodaySessionLog()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: build X\nConstraints: none", cancellationToken: CancellationToken.None);

        // Store a session log with today's date
        string today = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd");
        await _memTools.Store(
            content: [$"## Session {today}\n- Started work on feature Y"],
            name: $"sessions:{today}",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await _memTools.Memory("restore", cancellationToken: CancellationToken.None);

        // Assert
        var parsed = ResponseParser.Parse(result);
        parsed.FollowUp.Should().Contain($"/sessions/{today}",
            because: "today's session log should appear in followUp for the agent to load");
    }
}
