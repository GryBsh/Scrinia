using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Scrinia.Server.Models;
using Scrinia.Server.Services;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class WorkflowEndpointTests : IClassFixture<ScriniaServerFactory>
{
    private readonly ScriniaServerFactory _factory;
    private readonly HttpClient _client;
    private readonly string _base;

    public WorkflowEndpointTests(ScriniaServerFactory factory)
    {
        _factory = factory;
        _client = factory.CreateAuthenticatedClient();
        _base = $"/api/v1/stores/{factory.PrimaryStore}/workflows";
    }

    // ── Workflow endpoints ────────────────────────────────────────────────────

    [Fact]
    public async Task ListWorkflows_ReturnsBuiltInWorkflows()
    {
        var resp = await _client.GetAsync(_base);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<WorkflowListResponse>();
        body.Should().NotBeNull();
        body!.Workflows.Should().NotBeEmpty();
        body.Workflows.Should().Contain(w => w.Name == "goal-execution");
        body.Workflows.Should().Contain(w => w.Name == "quick-fix");
    }

    [Fact]
    public async Task ListWorkflows_BuiltInsAreMarkedAsBuiltIn()
    {
        var resp = await _client.GetAsync(_base);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<WorkflowListResponse>();
        body.Should().NotBeNull();

        var goalExec = body!.Workflows.First(w => w.Name == "goal-execution");
        goalExec.IsBuiltIn.Should().BeTrue();
        goalExec.SeedActivityCount.Should().BeGreaterThan(0);
        goalExec.PostPlanActivityCount.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GetWorkflow_BuiltIn_ReturnsContent()
    {
        var resp = await _client.GetAsync($"{_base}/goal-execution");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<WorkflowContent>();
        body.Should().NotBeNull();
        body!.Name.Should().Be("goal-execution");
        body.YamlContent.Should().NotBeNullOrWhiteSpace();
        // The YAML should contain key workflow concepts
        body.YamlContent.Should().Contain("researcher");
    }

    [Fact]
    public async Task GetWorkflow_QuickFix_ReturnsContent()
    {
        var resp = await _client.GetAsync($"{_base}/quick-fix");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<WorkflowContent>();
        body.Should().NotBeNull();
        body!.Name.Should().Be("quick-fix");
        body.YamlContent.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetWorkflow_NotFound_Returns404()
    {
        var resp = await _client.GetAsync($"{_base}/nonexistent-workflow");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task UpdateWorkflow_ValidYaml_Returns200()
    {
        string validYaml = @"
name: custom-test
activities:
  - id: researcher
    phase: ""00""
    wave: 0
    skill: ""builtin:researcher""
    dependsOn: []
    tag: researcher
    prompt: ""## Research Task\nInvestigate scope.""
    type: agent
    role: seed
    validation:
      checkType: index-prefix
      target: ""research:{goalShort}""
      errorTemplate: ""No research found.""
  - id: qa-gate
    dependsOn:
      - ""*""
    tag: qa
    prompt: ""## QA Gate\nRun tests.""
    type: agent
    role: post-plan
    validation:
      checkType: memory-exists
      target: ""qa:latest""
      errorTemplate: ""qa:latest not found.""
";

        var content = new StringContent(
            JsonSerializer.Serialize(new WorkflowUpdateRequest(validYaml)),
            Encoding.UTF8,
            "application/json");
        var resp = await _client.PutAsync($"{_base}/custom-test", content);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var responseText = await resp.Content.ReadAsStringAsync();
        responseText.Should().Contain("saved");
    }

    [Fact]
    public async Task UpdateWorkflow_InvalidYaml_Returns400()
    {
        // Send content that parses as YAML but fails workflow validation (missing required fields)
        string invalidYaml = @"
name: """"
activities: []
";

        var content = new StringContent(
            JsonSerializer.Serialize(new WorkflowUpdateRequest(invalidYaml)),
            Encoding.UTF8,
            "application/json");
        var resp = await _client.PutAsync($"{_base}/bad-workflow", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateWorkflow_EmptyContent_Returns400()
    {
        var content = new StringContent(
            JsonSerializer.Serialize(new WorkflowUpdateRequest("")),
            Encoding.UTF8,
            "application/json");
        var resp = await _client.PutAsync($"{_base}/empty-wf", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Contain("required");
    }

    [Fact]
    public async Task UpdateWorkflow_ThenGetReturnsIt()
    {
        string validYaml = @"
name: roundtrip-wf
activities:
  - id: researcher
    phase: ""00""
    wave: 0
    skill: ""builtin:researcher""
    dependsOn: []
    tag: researcher
    prompt: ""## Research\nInvestigate.""
    type: agent
    role: seed
    validation:
      checkType: index-prefix
      target: ""research:{goalShort}""
      errorTemplate: ""No research.""
  - id: qa-gate
    dependsOn:
      - ""*""
    tag: qa
    prompt: ""## QA\nTest.""
    type: agent
    role: post-plan
    validation:
      checkType: memory-exists
      target: ""qa:latest""
      errorTemplate: ""No qa.""
";

        var putContent = new StringContent(
            JsonSerializer.Serialize(new WorkflowUpdateRequest(validYaml)),
            Encoding.UTF8,
            "application/json");
        var putResp = await _client.PutAsync($"{_base}/roundtrip-wf", putContent);
        putResp.StatusCode.Should().Be(HttpStatusCode.OK);

        // Now GET should return the stored workflow
        var getResp = await _client.GetAsync($"{_base}/roundtrip-wf");
        getResp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await getResp.Content.ReadFromJsonAsync<WorkflowContent>();
        body.Should().NotBeNull();
        body!.Name.Should().Be("roundtrip-wf");
        body.YamlContent.Should().NotBeNullOrWhiteSpace();
    }

    // ── Goal endpoints ───────────────────────────────────────────────────────

    [Fact]
    public async Task ListGoals_NoProject_ReturnsEmptyArray()
    {
        // Use the secondary store (which has no project:context) with the main authenticated client
        var resp = await _client.GetAsync(
            $"/api/v1/stores/{_factory.SecondaryStore}/workflows/goals");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<GoalListResponse>();
        body.Should().NotBeNull();
        body!.Goals.Should().BeEmpty();
    }

    [Fact]
    public async Task ListGoals_WithProject_ReturnsGoalSummaries()
    {
        // Store a project:context memory with a goals section
        string contextContent =
            "# Project Context\n\n" +
            "## Goals\n" +
            "- [G-100] [active] Implement workflow endpoints\n" +
            "- [G-101] [complete] Add authentication\n";

        var storeReq = new StoreRequest([contextContent], "project:context", "Project context with goals");
        var storeResp = await _client.PostAsJsonAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/memories", storeReq);
        storeResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await _client.GetAsync($"{_base}/goals");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<GoalListResponse>();
        body.Should().NotBeNull();
        body!.Goals.Should().NotBeEmpty();
        body.Goals.Should().Contain(g => g.Id == "G-100" && g.Status == "active");
        body.Goals.Should().Contain(g => g.Id == "G-101" && g.Status == "complete");
    }

    [Fact]
    public async Task GetGoalDetail_NotFound_Returns404()
    {
        var resp = await _client.GetAsync($"{_base}/goals/G-999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var body = await resp.Content.ReadFromJsonAsync<ErrorResponse>();
        body.Should().NotBeNull();
        body!.Error.Should().Contain("not found");
    }

    [Fact]
    public async Task GetGoalTasks_NoTasks_ReturnsEmptyList()
    {
        var resp = await _client.GetAsync($"{_base}/goals/G-888/tasks");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var body = await resp.Content.ReadFromJsonAsync<TaskListResponse>();
        body.Should().NotBeNull();
        body!.Tasks.Should().BeEmpty();
    }

    // ── Auth ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("/")]
    [InlineData("/goal-execution")]
    [InlineData("/goals")]
    [InlineData("/goals/G-1")]
    [InlineData("/goals/G-1/tasks")]
    [InlineData("/goals/G-1/events")]
    public async Task Endpoints_RequireAuth_Returns401(string suffix)
    {
        var client = _factory.CreateClient(); // no auth header
        var resp = await client.GetAsync(
            $"/api/v1/stores/{_factory.PrimaryStore}/workflows{suffix}");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    // ── SSE ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EventStream_Unauthenticated_Returns401()
    {
        // SSE endpoints still require auth — verify that constraint
        var client = _factory.CreateClient();
        var resp = await client.GetAsync($"{_base}/goals/G-1/events");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task TaskEventBroadcaster_BroadcastReachesSubscriber()
    {
        var broadcaster = new TaskEventBroadcaster();

        string subId = broadcaster.Subscribe();
        var reader = broadcaster.GetReader(subId);

        var evt = new TaskEvent("G-42", "task-01", "pending", "active",
            DateTime.UtcNow.ToString("o"));
        broadcaster.Broadcast(evt);

        // The event should be readable
        bool available = reader.TryRead(out var received);
        available.Should().BeTrue();
        received.Should().NotBeNull();
        received!.GoalId.Should().Be("G-42");
        received.TaskName.Should().Be("task-01");
        received.OldStatus.Should().Be("pending");
        received.NewStatus.Should().Be("active");

        broadcaster.Unsubscribe(subId);
    }

    [Fact]
    public async Task TaskEventBroadcaster_UnsubscribedDoesNotReceive()
    {
        var broadcaster = new TaskEventBroadcaster();

        string subId = broadcaster.Subscribe();
        var reader = broadcaster.GetReader(subId);
        broadcaster.Unsubscribe(subId);

        var evt = new TaskEvent("G-42", "task-02", "pending", "complete",
            DateTime.UtcNow.ToString("o"));
        broadcaster.Broadcast(evt);

        // After unsubscribe, the original channel should not get new events
        bool available = reader.TryRead(out _);
        available.Should().BeFalse();
    }

    [Fact]
    public async Task TaskEventBroadcaster_MultipleSubscribersReceive()
    {
        var broadcaster = new TaskEventBroadcaster();

        string sub1 = broadcaster.Subscribe();
        string sub2 = broadcaster.Subscribe();
        var reader1 = broadcaster.GetReader(sub1);
        var reader2 = broadcaster.GetReader(sub2);

        var evt = new TaskEvent("G-10", "build-task", "active", "complete",
            DateTime.UtcNow.ToString("o"));
        broadcaster.Broadcast(evt);

        reader1.TryRead(out var r1).Should().BeTrue();
        reader2.TryRead(out var r2).Should().BeTrue();
        r1!.GoalId.Should().Be("G-10");
        r2!.GoalId.Should().Be("G-10");

        broadcaster.Unsubscribe(sub1);
        broadcaster.Unsubscribe(sub2);
    }
}
