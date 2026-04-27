using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for the WorkflowDefinition data model, its static DefaultGoalWorkflow,
/// gate validation check types, JSON serialization roundtrip, and workflow-driven
/// seed/gate task creation.
/// </summary>
public sealed class WorkflowDefinitionTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public WorkflowDefinitionTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    // ══════════════════════════════════════════════════════════════════════════
    // 1. WorkflowDefinition unit tests — structure of DefaultGoalWorkflow
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultGoalWorkflow_HasExactly4SeedActivities()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        workflow.SeedActivities.Should().HaveCount(4,
            "DefaultGoalWorkflow should define exactly 4 seed activities (agent-specialist, researcher, auditor, planner)");
    }

    [Fact]
    public void DefaultGoalWorkflow_HasExactly5PostPlanActivities()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        workflow.PostPlanActivities.Should().HaveCount(6,
            "DefaultGoalWorkflow should define exactly 6 post-plan activities (implementation, qa, self-reflector, evolutionary, cartographer, march)");
    }

    [Fact]
    public void DefaultGoalWorkflow_SeedActivities_AllHavePhase00()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var seed in workflow.SeedActivities)
        {
            seed.Phase.Should().Be("00",
                $"seed activity '{seed.Id}' should have Phase \"00\"");
        }
    }

    [Fact]
    public void DefaultGoalWorkflow_SeedActivities_AllHaveNonNullWave()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var seed in workflow.SeedActivities)
        {
            seed.Wave.Should().NotBeNull(
                $"seed activity '{seed.Id}' should have a non-null Wave");
        }
    }

    [Fact]
    public void DefaultGoalWorkflow_PostPlanActivities_AllHaveNullPhaseAndWave()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var gate in workflow.PostPlanActivities)
        {
            gate.Phase.Should().BeNull(
                $"post-plan activity '{gate.Id}' should have null Phase (assigned dynamically)");
            gate.Wave.Should().BeNull(
                $"post-plan activity '{gate.Id}' should have null Wave (computed by topo sort)");
        }
    }

    [Fact]
    public void DefaultGoalWorkflow_QaGate_DependsOnWildcard()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var qaGate = workflow.PostPlanActivities.First(a => a.Id == "qa-gate");
        qaGate.DependsOn.Should().Contain("*",
            "qa-gate DependsOn should contain '*' (all user tasks)");
    }

    [Fact]
    public void DefaultGoalWorkflow_SelfReflectorGate_DependsOnQaGate()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var srGate = workflow.PostPlanActivities.First(a => a.Id == "self-reflector-gate");
        srGate.DependsOn.Should().Contain("qa-gate",
            "self-reflector-gate DependsOn should contain 'qa-gate'");
    }

    [Fact]
    public void DefaultGoalWorkflow_FinalGates_DependOnQaAndSelfReflector()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var finalGateIds = new[] { "evolutionary-gate", "cartographer-gate", "march-gate" };

        foreach (var gateId in finalGateIds)
        {
            var gate = workflow.PostPlanActivities.First(a => a.Id == gateId);
            gate.DependsOn.Should().Contain("qa-gate",
                $"'{gateId}' DependsOn should contain 'qa-gate'");
            gate.DependsOn.Should().Contain("self-reflector-gate",
                $"'{gateId}' DependsOn should contain 'self-reflector-gate'");
        }
    }

    [Fact]
    public void DefaultGoalWorkflow_AllActivities_HaveNonEmptyPrompt()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var all = workflow.SeedActivities.Concat(workflow.PostPlanActivities);
        foreach (var activity in all)
        {
            activity.Prompt.Should().NotBeNullOrWhiteSpace(
                $"activity '{activity.Id}' should have non-empty Prompt");
        }
    }

    [Fact]
    public void DefaultGoalWorkflow_AllPostPlanActivities_HaveNonNullValidation()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var gate in workflow.PostPlanActivities.Where(a => a.Type == "agent"))
        {
            gate.Validation.Should().NotBeNull(
                $"post-plan agent activity '{gate.Id}' should have non-null Validation");
        }
    }

    [Fact]
    public void DefaultGoalWorkflow_SeedActivities_HaveCorrectIds()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var seedIds = workflow.SeedActivities.Select(a => a.Id).ToArray();
        seedIds.Should().BeEquivalentTo(["agent-specialist", "researcher", "auditor", "planner"],
            "seed activities should be agent-specialist, researcher, auditor, and planner");
    }

    [Fact]
    public void DefaultGoalWorkflow_PostPlanActivities_HaveCorrectIds()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var gateIds = workflow.PostPlanActivities.Select(a => a.Id).ToArray();
        gateIds.Should().BeEquivalentTo(
            ["implementation", "qa-gate", "self-reflector-gate", "evolutionary-gate", "cartographer-gate", "march-gate"],
            "post-plan activities should be implementation, qa-gate, self-reflector-gate, evolutionary-gate, cartographer-gate, march-gate");
    }

    [Fact]
    public void DefaultGoalWorkflow_SeedWaveOrder_IsAgentSpecialistResearcherAuditorPlanner()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var ordered = workflow.SeedActivities.OrderBy(a => a.Wave).ToArray();
        ordered[0].Id.Should().Be("agent-specialist", "wave 0 should be agent-specialist");
        ordered[1].Id.Should().Be("researcher", "wave 1 should be researcher");
        ordered[2].Id.Should().Be("auditor", "wave 2 should be auditor");
        ordered[3].Id.Should().Be("planner", "wave 3 should be planner");
    }

    [Fact]
    public void DefaultGoalWorkflow_ResearcherSeed_DependsOnAgentSpecialist()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var researcher = workflow.SeedActivities.First(a => a.Id == "researcher");
        researcher.DependsOn.Should().Contain("agent-specialist",
            "researcher seed should depend on agent-specialist (which runs first)");
    }

    [Fact]
    public void DefaultGoalWorkflow_AuditorSeed_DependsOnResearcher()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var auditor = workflow.SeedActivities.First(a => a.Id == "auditor");
        auditor.DependsOn.Should().Contain("researcher",
            "auditor seed should depend on researcher");
    }

    [Fact]
    public void DefaultGoalWorkflow_PlannerSeed_DependsOnAuditor()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var planner = workflow.SeedActivities.First(a => a.Id == "planner");
        planner.DependsOn.Should().Contain("auditor",
            "planner seed should depend on auditor");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 2. Gate validation check types
    // ══════════════════════════════════════════════════════════════════════════

    [Theory]
    [InlineData("agent-specialist", "memory-exists")]
    [InlineData("researcher", "index-prefix")]
    [InlineData("auditor", "memory-exists")]
    [InlineData("planner", "index-no-gate")]
    [InlineData("qa-gate", "memory-exists")]
    [InlineData("self-reflector-gate", "index-prefix")]
    [InlineData("evolutionary-gate", "index-prefix")]
    [InlineData("cartographer-gate", "index-prefix")]
    [InlineData("march-gate", "filesystem-glob")]
    public void DefaultGoalWorkflow_Activity_HasExpectedCheckType(string activityId, string expectedCheckType)
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var activity = workflow.SeedActivities
            .Concat(workflow.PostPlanActivities)
            .First(a => a.Id == activityId);

        activity.Validation.Should().NotBeNull(
            $"activity '{activityId}' should have Validation");
        activity.Validation!.CheckType.Should().Be(expectedCheckType,
            $"activity '{activityId}' should have CheckType '{expectedCheckType}'");
    }

    [Fact]
    public void DefaultGoalWorkflow_AllValidations_HaveNonEmptyTargetAndErrorTemplate()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var allWithValidation = workflow.SeedActivities
            .Concat(workflow.PostPlanActivities)
            .Where(a => a.Validation is not null);

        foreach (var activity in allWithValidation)
        {
            activity.Validation!.Target.Should().NotBeNullOrWhiteSpace(
                $"activity '{activity.Id}' Validation.Target should be non-empty");
            activity.Validation.ErrorTemplate.Should().NotBeNullOrWhiteSpace(
                $"activity '{activity.Id}' Validation.ErrorTemplate should be non-empty");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 3. JSON serialization roundtrip
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultGoalWorkflow_JsonRoundtrip_PreservesCounts()
    {
        var original = WorkflowDefinition.DefaultGoalWorkflow;

        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.WorkflowDefinition);
        json.Should().NotBeNullOrWhiteSpace("serialized JSON should not be empty");

        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
        deserialized.Should().NotBeNull("deserialized workflow should not be null");
        deserialized!.SeedActivities.Should().HaveCount(original.SeedActivities.Length,
            "deserialized SeedActivities count should match original");
        deserialized.PostPlanActivities.Should().HaveCount(original.PostPlanActivities.Length,
            "deserialized PostPlanActivities count should match original");
    }

    [Fact]
    public void DefaultGoalWorkflow_JsonRoundtrip_PreservesActivityIds()
    {
        var original = WorkflowDefinition.DefaultGoalWorkflow;

        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.WorkflowDefinition);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
        deserialized.Should().NotBeNull();

        var originalIds = original.SeedActivities.Concat(original.PostPlanActivities).Select(a => a.Id).ToArray();
        var deserializedIds = deserialized!.SeedActivities.Concat(deserialized.PostPlanActivities).Select(a => a.Id).ToArray();
        deserializedIds.Should().BeEquivalentTo(originalIds,
            "deserialized activity IDs should match original");
    }

    [Fact]
    public void DefaultGoalWorkflow_JsonRoundtrip_PreservesValidation()
    {
        var original = WorkflowDefinition.DefaultGoalWorkflow;

        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.WorkflowDefinition);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
        deserialized.Should().NotBeNull();

        // Verify a specific gate's validation survives roundtrip
        var originalQa = original.PostPlanActivities.First(a => a.Id == "qa-gate");
        var deserializedQa = deserialized!.PostPlanActivities.First(a => a.Id == "qa-gate");

        deserializedQa.Validation.Should().NotBeNull();
        deserializedQa.Validation!.CheckType.Should().Be(originalQa.Validation!.CheckType);
        deserializedQa.Validation.Target.Should().Be(originalQa.Validation.Target);
        deserializedQa.Validation.ErrorTemplate.Should().Be(originalQa.Validation.ErrorTemplate);
    }

    [Fact]
    public void DefaultGoalWorkflow_JsonRoundtrip_PreservesDependsOn()
    {
        var original = WorkflowDefinition.DefaultGoalWorkflow;

        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.WorkflowDefinition);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
        deserialized.Should().NotBeNull();

        // Verify evolutionary-gate DependsOn survives roundtrip
        var originalEvo = original.PostPlanActivities.First(a => a.Id == "evolutionary-gate");
        var deserializedEvo = deserialized!.PostPlanActivities.First(a => a.Id == "evolutionary-gate");
        deserializedEvo.DependsOn.Should().BeEquivalentTo(originalEvo.DependsOn);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 4. Workflow-driven seed task creation (via goal('add'))
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoalAdd_CreatesSeedTasksMatchingWorkflow()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act
        await ScriniaProjectTools.GoalUpdate("add", "Test workflow seed creation",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — each seed activity in the workflow should have a corresponding task
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        foreach (var seed in workflow.SeedActivities)
        {
            var taskEntry = entries.FirstOrDefault(e => e.Name.Contains(seed.Id));
            taskEntry.Should().NotBeNull(
                $"seed activity '{seed.Id}' should produce a matching task entry");
            taskEntry!.Keywords.Should().Contain($"tag:{seed.Tag}",
                $"task for '{seed.Id}' should have tag:{seed.Tag} keyword");
            taskEntry.Keywords.Should().Contain($"wave:{seed.Wave}",
                $"task for '{seed.Id}' should have wave:{seed.Wave} keyword");
            taskEntry.Keywords.Should().Contain("phase:00",
                $"task for '{seed.Id}' should have phase:00 keyword");
        }
    }

    [Fact]
    public async Task GoalAdd_SeedTaskDependencies_MatchWorkflow()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build the API",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act
        await ScriniaProjectTools.GoalUpdate("add", "Test seed dependencies",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — dependency structure
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        // Agent-specialist should have no depends_on keywords
        var agentSpecialist = entries.First(e => e.Name.Contains("agent-specialist"));
        agentSpecialist.Keywords!.Where(k => k.StartsWith("depends_on:")).Should().BeEmpty(
            "agent-specialist seed should have no depends_on keywords");

        // Researcher should depend on agent-specialist
        var researcher = entries.First(e => e.Name.Contains("researcher"));
        researcher.Keywords!.Should().Contain(
            k => k.StartsWith("depends_on:") && k.Contains("agent-specialist"),
            "researcher seed should depend on agent-specialist");

        // Auditor should depend on researcher
        var auditor = entries.First(e => e.Name.Contains("auditor"));
        auditor.Keywords!.Should().Contain(
            k => k.StartsWith("depends_on:") && k.Contains("researcher"),
            "auditor seed should depend on researcher");

        // Planner should depend on auditor
        var planner = entries.First(e => e.Name.Contains("planner"));
        planner.Keywords!.Should().Contain(
            k => k.StartsWith("depends_on:") && k.Contains("auditor"),
            "planner seed should depend on auditor");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 5. Workflow-driven gate injection (via plan('tasks'))
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task PlanTasks_InjectsAllGatesFromWorkflow()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: gate injection from workflow",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Feature X",
            cancellationToken: CancellationToken.None);

        // Act
        string result = await ScriniaProjectTools.PlanTasks("01",
            "## Task 01\nDepends on: none\nAction: Implement feature X\nAcceptance criteria:\n- Feature works",
            cancellationToken: CancellationToken.None);

        // Assert — every post-plan activity ID should appear in the created tasks
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        foreach (var gate in workflow.PostPlanActivities.Where(a => a.Type != "spawner"))
        {
            entries.Should().Contain(e => e.Name.Contains(gate.Id),
                $"post-plan activity '{gate.Id}' should be injected as a task by PlanTasks");
        }
    }

    [Fact]
    public async Task PlanTasks_GateTaskKeywords_MatchWorkflow()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: gate keyword matching",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Feature X",
            cancellationToken: CancellationToken.None);

        // Act
        await ScriniaProjectTools.PlanTasks("01",
            "## Task 01\nDepends on: none\nAction: Implement feature X\nAcceptance criteria:\n- Feature works",
            cancellationToken: CancellationToken.None);

        // Assert — each injected gate should carry the correct tag:TYPE keyword
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        foreach (var gate in workflow.PostPlanActivities.Where(a => a.Type != "spawner"))
        {
            var taskEntry = entries.FirstOrDefault(e => e.Name.Contains(gate.Id));
            taskEntry.Should().NotBeNull($"gate task '{gate.Id}' should exist");
            taskEntry!.Keywords.Should().Contain($"tag:{gate.Tag}",
                $"gate task '{gate.Id}' should carry 'tag:{gate.Tag}' keyword");
        }
    }

    [Fact]
    public async Task PlanTasks_QaGateDependsOnAllUserTasks_FromWorkflow()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: wildcard dependency test",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Feature X\n- REQ-02: Feature Y",
            cancellationToken: CancellationToken.None);

        // Act — two user tasks, so qa-gate (DependsOn: ["*"]) should depend on both
        await ScriniaProjectTools.PlanTasks("01",
            "## Task 01\nDepends on: none\nAction: Build X\nAcceptance criteria:\n- done\n\n" +
            "## Task 02\nDepends on: none\nAction: Build Y\nAcceptance criteria:\n- done",
            cancellationToken: CancellationToken.None);

        // Assert
        var store = MemoryStoreContext.Current!;
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        var qaGate = entries.FirstOrDefault(e => e.Name.Contains("qa-gate"));
        qaGate.Should().NotBeNull("qa-gate should be injected");
        var depKeywords = qaGate!.Keywords!
            .Where(k => k.StartsWith("depends_on:", StringComparison.OrdinalIgnoreCase))
            .ToList();
        depKeywords.Should().HaveCount(2,
            "qa-gate should depend on both user tasks via wildcard expansion");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 6. Unknown gate passthrough (WF-09)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task TaskComplete_UnknownGateType_PassesThroughWithoutError()
    {
        // Arrange — create a project and manually insert a task with a gate keyword
        // that does not exist in the workflow definition
        await ScriniaProjectTools.ProjectInit("Goals: unknown gate passthrough test",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        var (scope, subject) = store.ParseQualifiedName("task:01-1-custom-gate");

        string content = "## Custom Gate\nAction: Do something custom\nAcceptance criteria:\n- done";
        string artifact = Nmp2ChunkedEncoder.Encode(content);
        await store.WriteArtifactAsync(subject, scope, artifact, CancellationToken.None);

        string uri = store.ArtifactUri(subject, scope);
        long originalBytes = System.Text.Encoding.UTF8.GetByteCount(content);

        var entry = new ArtifactEntry(
            Name: subject,
            Uri: uri,
            OriginalBytes: originalBytes,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: content[..Math.Min(200, content.Length)],
            Keywords: ["status:pending", "wave:1", "phase:01", "tag:nonexistent-custom"]);
        store.Upsert(entry, scope);

        // Act — complete the task with the unknown gate type
        string result = await ScriniaProjectTools.TaskComplete("task:01-1-custom-gate", "Custom gate done",
            cancellationToken: CancellationToken.None);

        // Assert — should pass through without validation error (WF-09 backward compat)
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success",
            "task with unknown gate type should complete successfully (WF-09 passthrough)");
        r.Content.Should().Contain("marked complete",
            "task with unknown gate type should complete successfully (WF-09 passthrough)");
    }

    [Fact]
    public async Task TaskComplete_UnknownGateType_NoValidationInWorkflow()
    {
        // Verify that the unknown gate type is truly not in the workflow
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var allTags = workflow.SeedActivities
            .Concat(workflow.PostPlanActivities)
            .Select(a => a.Tag)
            .ToArray();

        allTags.Should().NotContain("nonexistent-custom",
            "the test tag should not exist in the workflow definition");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 7. Workflow name
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultGoalWorkflow_HasCorrectName()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        workflow.Name.Should().Be("goal-execution",
            "DefaultGoalWorkflow should have the name 'goal-execution'");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 8. Seed activities all have Validation (they do gate on completion)
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultGoalWorkflow_AllSeedActivities_HaveValidation()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var seed in workflow.SeedActivities)
        {
            seed.Validation.Should().NotBeNull(
                $"seed activity '{seed.Id}' should have Validation for gate-on-completion");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 9. Tag matches Id pattern
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultGoalWorkflow_PostPlanActivities_TagMatchesIdWithoutSuffix()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var gate in workflow.PostPlanActivities)
        {
            // Gate ID is "qa-gate", Tag is "qa"
            string expectedTag = gate.Id.Replace("-gate", "");
            gate.Tag.Should().Be(expectedTag,
                $"gate '{gate.Id}' Tag should be '{expectedTag}'");
        }
    }

    [Fact]
    public void DefaultGoalWorkflow_SeedActivities_TagMatchesId()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var seed in workflow.SeedActivities)
        {
            // Seed ID and Tag should match (e.g., "researcher" and "researcher")
            seed.Tag.Should().Be(seed.Id,
                $"seed '{seed.Id}' Tag should match its Id");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 10. All activities have a Skill reference
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void DefaultGoalWorkflow_AllActivities_HaveSkill()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var agentActivities = workflow.Activities.Where(a => a.Type == "agent");
        foreach (var activity in agentActivities)
        {
            activity.Skill.Should().NotBeNullOrWhiteSpace(
                $"agent activity '{activity.Id}' should have a non-empty Skill reference");
            activity.Skill.Should().StartWith("builtin:",
                $"agent activity '{activity.Id}' Skill should start with 'builtin:'");
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 11. Workflow validation — valid workflows
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_DefaultGoalWorkflow_PassesValidation()
    {
        var errors = WorkflowDefinition.Validate(WorkflowDefinition.DefaultGoalWorkflow);
        errors.Should().BeEmpty(
            "DefaultGoalWorkflow should pass all validation checks");
    }

    [Fact]
    public void Validate_QuickFixWorkflow_PassesValidation()
    {
        var errors = WorkflowDefinition.Validate(WorkflowDefinition.QuickFixWorkflow);
        errors.Should().BeEmpty(
            "QuickFixWorkflow should pass all validation checks");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 12. Workflow validation — error cases
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_MissingName_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:researcher", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"))
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Name"),
            "missing Name should produce a validation error");
    }

    [Fact]
    public void Validate_EmptyActivities_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "empty-activities",
            Activities: []);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Activities"),
            "empty Activities should produce a validation error");
    }

    [Fact]
    public void Validate_DuplicateActivityIds_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "dup-ids",
            Activities:
            [
                new WorkflowActivity("researcher", "00", 0, "builtin:researcher", [], "researcher",
                    "content1", new GateValidation("index-prefix", "research:", "err")),
                new WorkflowActivity("researcher", "00", 1, "builtin:researcher", [], "dup",
                    "content2", new GateValidation("index-prefix", "research:", "err"))
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Duplicate") && e.Contains("researcher"),
            "duplicate activity IDs should produce a validation error");
    }

    [Fact]
    public void Validate_CircularDependsOn_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "cycle",
            Activities:
            [
                new WorkflowActivity("a", "00", 0, "builtin:a", ["b"], "a",
                    "content-a", new GateValidation("index-prefix", "a:", "err")),
                new WorkflowActivity("b", "00", 1, "builtin:b", ["a"], "b",
                    "content-b", new GateValidation("index-prefix", "b:", "err"))
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("cycle"),
            "circular DependsOn should produce a validation error about a cycle");
    }

    [Fact]
    public void Validate_InvalidCheckType_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "bad-check",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("nonexistent-check-type", "target:", "err"))
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("CheckType") && e.Contains("nonexistent-check-type"),
            "invalid CheckType should produce a validation error");
    }

    [Fact]
    public void Validate_SeedWithoutPhase_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "no-phase",
            Activities:
            [
                new WorkflowActivity("r", null, 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"))
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Phase") && e.Contains("required"),
            "seed without Phase should produce a validation error");
    }

    [Fact]
    public void Validate_SeedWithoutWave_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "no-wave",
            Activities:
            [
                new WorkflowActivity("r", "00", null, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"))
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Wave") && e.Contains("required"),
            "seed without Wave should produce a validation error");
    }

    [Fact]
    public void Validate_PostPlanWithPhaseSet_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "postplan-with-phase",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err")),
                new WorkflowActivity("qa-gate", "01", null, "builtin:qa", ["*"], "qa",
                    "content", new GateValidation("memory-exists", "qa:latest", "err"),
                    Type: "agent", Role: "post-plan")
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Phase") && e.Contains("null") && e.Contains("post-plan"),
            "post-plan activity with Phase set should produce a validation error");
    }

    [Fact]
    public void Validate_PostPlanWithWaveSet_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "postplan-with-wave",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err")),
                new WorkflowActivity("qa-gate", null, 5, "builtin:qa", ["*"], "qa",
                    "content", new GateValidation("memory-exists", "qa:latest", "err"),
                    Type: "agent", Role: "post-plan")
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Wave") && e.Contains("null") && e.Contains("post-plan"),
            "post-plan activity with Wave set should produce a validation error");
    }

    [Fact]
    public void Validate_DependsOnReferencingNonExistentId_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "bad-dep",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", ["ghost"], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"))
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("ghost") && e.Contains("unknown"),
            "DependsOn referencing non-existent ID should produce a validation error");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 13. QuickFixWorkflow — structure and content
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void QuickFixWorkflow_HasCorrectName()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        wf.Name.Should().Be("quick-fix",
            "QuickFixWorkflow should have the name 'quick-fix'");
    }

    [Fact]
    public void QuickFixWorkflow_HasExactly2SeedActivities()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        wf.SeedActivities.Should().HaveCount(2,
            "QuickFixWorkflow should have exactly 2 seed activities (researcher and planner)");
    }

    [Fact]
    public void QuickFixWorkflow_HasExactly1PostPlanActivity()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        wf.PostPlanActivities.Should().HaveCount(2,
            "QuickFixWorkflow should have exactly 2 post-plan activities (implementation + qa-gate)");
    }

    [Fact]
    public void QuickFixWorkflow_SeedActivities_AreResearcherAndPlanner()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var seedIds = wf.SeedActivities.Select(a => a.Id).ToArray();
        seedIds.Should().BeEquivalentTo(["researcher", "planner"],
            "QuickFixWorkflow seeds should be researcher and planner (no auditor)");
    }

    [Fact]
    public void QuickFixWorkflow_DoesNotIncludeAuditor()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var allIds = wf.SeedActivities.Concat(wf.PostPlanActivities).Select(a => a.Id);
        allIds.Should().NotContain("auditor",
            "QuickFixWorkflow should not include an auditor activity");
    }

    [Fact]
    public void QuickFixWorkflow_PostPlanActivity_IsQaGateOnly()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var gateIds = wf.PostPlanActivities.Select(a => a.Id).ToArray();
        gateIds.Should().BeEquivalentTo(["implementation", "qa-gate"],
            "QuickFixWorkflow post-plan activities should contain implementation + qa-gate");
    }

    [Fact]
    public void QuickFixWorkflow_DoesNotIncludeHeavyGates()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var gateIds = wf.PostPlanActivities.Select(a => a.Id).ToHashSet();
        gateIds.Should().NotContain("self-reflector-gate",
            "QuickFixWorkflow should not include self-reflector-gate");
        gateIds.Should().NotContain("evolutionary-gate",
            "QuickFixWorkflow should not include evolutionary-gate");
        gateIds.Should().NotContain("cartographer-gate",
            "QuickFixWorkflow should not include cartographer-gate");
        gateIds.Should().NotContain("march-gate",
            "QuickFixWorkflow should not include march-gate");
    }

    [Fact]
    public void QuickFixWorkflow_PlannerDependsOnResearcher()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var planner = wf.SeedActivities.First(a => a.Id == "planner");
        planner.DependsOn.Should().Contain("researcher",
            "QuickFixWorkflow planner should depend on researcher");
    }

    [Fact]
    public void QuickFixWorkflow_ResearcherIsWave0_PlannerIsWave1()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var researcher = wf.SeedActivities.First(a => a.Id == "researcher");
        var planner = wf.SeedActivities.First(a => a.Id == "planner");
        researcher.Wave.Should().Be(0, "QuickFixWorkflow researcher should be wave 0");
        planner.Wave.Should().Be(1, "QuickFixWorkflow planner should be wave 1");
    }

    [Fact]
    public void QuickFixWorkflow_QaGate_DependsOnWildcard()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var qaGate = wf.PostPlanActivities.First(a => a.Id == "qa-gate");
        qaGate.DependsOn.Should().Contain("*",
            "QuickFixWorkflow qa-gate should depend on '*' (all user tasks)");
    }

    [Fact]
    public void QuickFixWorkflow_AllActivities_HaveValidation()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        var agentActivities = wf.Activities.Where(a => a.Type == "agent");
        foreach (var activity in agentActivities)
        {
            activity.Validation.Should().NotBeNull(
                $"QuickFixWorkflow agent activity '{activity.Id}' should have Validation");
        }
    }

    [Fact]
    public void QuickFixWorkflow_JsonRoundtrip_PreservesStructure()
    {
        var original = WorkflowDefinition.QuickFixWorkflow;

        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.WorkflowDefinition);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
        deserialized.Should().NotBeNull("deserialized QuickFixWorkflow should not be null");
        deserialized!.Name.Should().Be("quick-fix");
        deserialized.SeedActivities.Should().HaveCount(2);
        deserialized.PostPlanActivities.Should().HaveCount(2);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 14. ResolveWorkflowAsync — quick-fix keyword resolves correctly
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task GoalAdd_WithQuickFixWorkflowRef_StoresWorkflowKeyword()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Fix a bug",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act — add a goal with workflowRef="quick-fix"
        await ScriniaProjectTools.GoalUpdate("add", "Fix login timeout bug",
            null, null, workflowRef: "quick-fix",
            cancellationToken: CancellationToken.None);

        // Assert — seed tasks should carry workflow:quick-fix keyword
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);
        var seedTasks = entries.Where(e =>
            e.Keywords?.Any(k => k.StartsWith("tag:", StringComparison.OrdinalIgnoreCase)) == true);

        seedTasks.Should().NotBeEmpty("goal('add') with workflowRef should create seed tasks");
        foreach (var task in seedTasks)
        {
            task.Keywords.Should().Contain("workflow:quick-fix",
                $"seed task '{task.Name}' should carry 'workflow:quick-fix' keyword");
        }
    }

    [Fact]
    public async Task GoalAdd_WithQuickFixWorkflowRef_Creates2SeedTasks()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Fix a bug",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act
        await ScriniaProjectTools.GoalUpdate("add", "Fix null reference in parser",
            null, null, workflowRef: "quick-fix",
            cancellationToken: CancellationToken.None);

        // Assert — quick-fix workflow should create researcher + planner (no auditor)
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        entries.Should().Contain(e => e.Name.Contains("researcher"),
            "quick-fix workflow should create a researcher seed task");
        entries.Should().Contain(e => e.Name.Contains("planner"),
            "quick-fix workflow should create a planner seed task");
        entries.Should().NotContain(e => e.Name.Contains("auditor"),
            "quick-fix workflow should NOT create an auditor seed task");
    }

    [Fact]
    public async Task GoalAdd_WithDefaultWorkflow_Creates3SeedTasks()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- Build a feature",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act — no workflowRef, should default to goal-execution
        await ScriniaProjectTools.GoalUpdate("add", "Build caching layer",
            null, null, cancellationToken: CancellationToken.None);

        // Assert — default workflow creates researcher + auditor + planner
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        entries.Should().Contain(e => e.Name.Contains("researcher"),
            "default workflow should create a researcher seed task");
        entries.Should().Contain(e => e.Name.Contains("auditor"),
            "default workflow should create an auditor seed task");
        entries.Should().Contain(e => e.Name.Contains("planner"),
            "default workflow should create a planner seed task");
    }

    [Fact]
    public async Task PlanTasks_WithQuickFixGoal_InjectsOnlyQaGate()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals: quick fix gate injection test",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.PlanRequirements("## v1\n- REQ-01: Fix the bug",
            cancellationToken: CancellationToken.None);

        // Add goal with quick-fix workflow so seed tasks get workflow:quick-fix keyword
        await ScriniaProjectTools.GoalUpdate("add", "Fix timeout issue",
            null, null, workflowRef: "quick-fix",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Act — plan tasks for phase 01
        await ScriniaProjectTools.PlanTasks("01",
            "## Task 01\nDepends on: none\nAction: Fix the timeout\nAcceptance criteria:\n- Timeout fixed",
            cancellationToken: CancellationToken.None);

        // Assert — quick-fix workflow should only inject qa-gate
        var (taskScope, _) = store.ParseQualifiedName("task:placeholder");
        var entries = store.LoadIndex(taskScope);

        entries.Should().Contain(e => e.Name.Contains("qa-gate"),
            "quick-fix workflow should inject qa-gate");
        entries.Should().NotContain(e => e.Name.Contains("self-reflector-gate"),
            "quick-fix workflow should NOT inject self-reflector-gate");
        entries.Should().NotContain(e => e.Name.Contains("evolutionary-gate"),
            "quick-fix workflow should NOT inject evolutionary-gate");
        entries.Should().NotContain(e => e.Name.Contains("cartographer-gate"),
            "quick-fix workflow should NOT inject cartographer-gate");
        entries.Should().NotContain(e => e.Name.Contains("march-gate"),
            "quick-fix workflow should NOT inject march-gate");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 15. ResolveGoalWorkflowName — unit-level behavior
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void ResolveGoalWorkflowName_NullGoalId_ReturnsDefault()
    {
        var store = MemoryStoreContext.Current!;
        string result = ScriniaProjectTools.ResolveGoalWorkflowName(store, null);
        result.Should().Be("default",
            "null goalId should resolve to 'default' workflow name");
    }

    [Fact]
    public async Task ResolveGoalWorkflowName_GoalWithQuickFixTasks_ReturnsQuickFix()
    {
        // Arrange — create a goal with quick-fix workflow so tasks have workflow:quick-fix keyword
        await ScriniaProjectTools.ProjectInit("Goals:\n- Fix a bug",
            cancellationToken: CancellationToken.None);
        await ScriniaProjectTools.GoalUpdate("add", "Fix a test failure",
            null, null, workflowRef: "quick-fix",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;

        // Find the goal ID from context — format is [G-N-hex] e.g. [G-2-abc]
        string context = await ReadMemoryText(store, "project:context");
        var goalMatch = Regex.Match(context, @"\[(G-\d+(?:-[a-fA-F0-9]+)?)\]");
        goalMatch.Success.Should().BeTrue("a goal should have been created in project:context");
        string goalId = goalMatch.Groups[1].Value;

        // Act
        string result = ScriniaProjectTools.ResolveGoalWorkflowName(store, goalId);

        // Assert
        result.Should().Be("quick-fix",
            "goal with quick-fix seed tasks should resolve to 'quick-fix' workflow name");
    }

    private static async Task<string> ReadMemoryText(IMemoryStore store, string qualifiedName)
    {
        string artifact = await store.ResolveArtifactAsync(qualifiedName);
        byte[] decoded = new Nmp2Strategy().Decode(artifact);
        return System.Text.Encoding.UTF8.GetString(decoded);
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 16. YAML workflow file resolution
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public async Task ResolveWorkflow_YamlFile_ParsesCorrectly()
    {
        // Arrange — initialize project so the store is set up
        await ScriniaProjectTools.ProjectInit("Goals:\n- YAML workflow test",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        string baseDir = ScriniaProjectTools.GetScriniaBaseDir(store);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);

        // Write a minimal valid YAML workflow with 2 seeds and 0 post-plan
        string yaml = """
            name: yaml-test
            activities:
              - id: researcher
                type: agent
                role: seed
                phase: "00"
                wave: 0
                skill: "builtin:researcher"
                dependsOn: []
                tag: researcher
                prompt: "Research the codebase."
                validation:
                  checkType: index-prefix
                  target: "research:"
                  errorTemplate: "No research found."
              - id: planner
                type: agent
                role: seed
                phase: "00"
                wave: 1
                skill: "builtin:planner"
                dependsOn:
                  - researcher
                tag: planner
                prompt: "Create the plan."
                validation:
                  checkType: index-no-gate
                  target: "task:"
                  errorTemplate: "No tasks found."
            """;
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "yaml-test.yaml"), yaml);

        // Act — resolve via entity('show', { type: 'workflow', id: 'yaml-test' })
        string result = await ScriniaProjectTools.EntityDispatch("show", "workflow", id: "yaml-test",
            cancellationToken: CancellationToken.None);

        // Assert — should use override (not built-in) and have 2 seed activities
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show workflow should succeed");
        r.Content.Should().Contain("(override)",
            "YAML workflow should be resolved as an override, not built-in");
        r.Content.Should().Contain("yaml-test",
            "resolved workflow should carry the name from the YAML file");

        // Deserialize the JSON portion to verify activity count
        string jsonPart = r.Content![(r.Content.IndexOf('{'))..];
        var parsed = JsonSerializer.Deserialize(jsonPart, PlanningJsonContext.Default.WorkflowDefinition);
        parsed.Should().NotBeNull();
        parsed!.SeedActivities.Should().HaveCount(2,
            "YAML workflow should have exactly 2 seed activities (researcher, planner)");
        parsed.PostPlanActivities.Should().BeEmpty(
            "YAML workflow should have 0 post-plan activities");
    }

    [Fact]
    public async Task ResolveWorkflow_YamlFallbackToJson()
    {
        // Arrange — only a .json file exists (no .yaml or .yml)
        await ScriniaProjectTools.ProjectInit("Goals:\n- JSON fallback test",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        string baseDir = ScriniaProjectTools.GetScriniaBaseDir(store);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);

        // Write a JSON workflow (no YAML sibling)
        var wf = new WorkflowDefinition(
            Name: "json-only",
            Activities:
            [
                new WorkflowActivity("researcher", "00", 0, "builtin:researcher", [], "researcher",
                    "Research.", new GateValidation("index-prefix", "research:", "err"))
            ]);
        string json = JsonSerializer.Serialize(wf, PlanningJsonContext.Default.WorkflowDefinition);
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "json-only.json"), json);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "workflow", id: "json-only",
            cancellationToken: CancellationToken.None);

        // Assert — should resolve from JSON as an override
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show workflow should succeed");
        r.Content.Should().Contain("(override)",
            ".json workflow should be used when no .yaml exists");
        r.Content.Should().Contain("json-only",
            "resolved workflow name should match the JSON file");

        // Verify seed count
        string jsonPart = r.Content![(r.Content.IndexOf('{'))..];
        var parsed = JsonSerializer.Deserialize(jsonPart, PlanningJsonContext.Default.WorkflowDefinition);
        parsed.Should().NotBeNull();
        parsed!.SeedActivities.Should().HaveCount(1,
            "JSON-only workflow should have 1 seed activity");
    }

    [Fact]
    public async Task ResolveWorkflow_CorruptedYaml_FallsBackWithWarning()
    {
        // Arrange — write a corrupted YAML file
        await ScriniaProjectTools.ProjectInit("Goals:\n- Corrupted YAML test",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        string baseDir = ScriniaProjectTools.GetScriniaBaseDir(store);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);

        await File.WriteAllTextAsync(
            Path.Combine(workflowsDir, "corrupted.yaml"),
            "{{{invalid yaml content that cannot be parsed");

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "workflow", id: "corrupted",
            cancellationToken: CancellationToken.None);

        // Assert — should fall back to built-in with a warning
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success",
            "corrupted YAML should still return success (falls back to built-in)");
        r.ActionNeeded.Should().NotBeEmpty(
            "corrupted YAML should produce warnings");
        string warningText = string.Join(" ", r.ActionNeeded);
        warningText.Should().Contain("corrupted.yaml",
            "warning should reference the corrupted file name");
        r.Content.Should().Contain("(built-in)",
            "corrupted YAML should fall back to the built-in default workflow");
    }

    [Fact]
    public async Task ResolveWorkflow_YamlPrecedenceOverYml()
    {
        // Arrange — create both .yaml and .yml with different names/content
        await ScriniaProjectTools.ProjectInit("Goals:\n- YAML precedence test",
            cancellationToken: CancellationToken.None);

        var store = MemoryStoreContext.Current!;
        string baseDir = ScriniaProjectTools.GetScriniaBaseDir(store);
        string workflowsDir = Path.Combine(baseDir, "workflows");
        Directory.CreateDirectory(workflowsDir);

        // .yaml version — has 2 seed activities
        string yamlContent = """
            name: precedence-test
            activities:
              - id: researcher
                type: agent
                role: seed
                phase: "00"
                wave: 0
                skill: "builtin:researcher"
                dependsOn: []
                tag: researcher
                prompt: "From .yaml file."
                validation:
                  checkType: index-prefix
                  target: "research:"
                  errorTemplate: "err"
              - id: planner
                type: agent
                role: seed
                phase: "00"
                wave: 1
                skill: "builtin:planner"
                dependsOn:
                  - researcher
                tag: planner
                prompt: "Plan from .yaml file."
                validation:
                  checkType: index-no-gate
                  target: "task:"
                  errorTemplate: "err"
            """;

        // .yml version — has 1 seed activity (different content)
        string ymlContent = """
            name: precedence-test
            activities:
              - id: researcher
                type: agent
                role: seed
                phase: "00"
                wave: 0
                skill: "builtin:researcher"
                dependsOn: []
                tag: researcher
                prompt: "From .yml file."
                validation:
                  checkType: index-prefix
                  target: "research:"
                  errorTemplate: "err"
            """;

        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "precedence-test.yaml"), yamlContent);
        await File.WriteAllTextAsync(Path.Combine(workflowsDir, "precedence-test.yml"), ymlContent);

        // Act
        string result = await ScriniaProjectTools.EntityDispatch("show", "workflow", id: "precedence-test",
            cancellationToken: CancellationToken.None);

        // Assert — .yaml should win, so we get 2 seed activities (not 1 from .yml)
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success", "entity show workflow should succeed");
        r.Content.Should().Contain("(override)",
            ".yaml file should be used as the override");

        string jsonPart = r.Content![(r.Content.IndexOf('{'))..];
        var parsed = JsonSerializer.Deserialize(jsonPart, PlanningJsonContext.Default.WorkflowDefinition);
        parsed.Should().NotBeNull();
        parsed!.SeedActivities.Should().HaveCount(2,
            ".yaml (2 seeds) should take precedence over .yml (1 seed)");
        parsed.SeedActivities.First(a => a.Id == "researcher").Prompt
            .Should().Contain(".yaml",
                "content should come from the .yaml file, not the .yml file");
    }

    [Fact]
    public async Task CreateOrUpdateWorkflow_YamlInput_ParsedCorrectly()
    {
        // Arrange
        await ScriniaProjectTools.ProjectInit("Goals:\n- YAML create test",
            cancellationToken: CancellationToken.None);

        // YAML definition string (not a file on disk — passed as the definition parameter)
        string yamlDefinition = """
            name: yaml-created
            activities:
              - id: researcher
                type: agent
                role: seed
                phase: "00"
                wave: 0
                skill: "builtin:researcher"
                dependsOn: []
                tag: researcher
                prompt: "Research via YAML input."
                validation:
                  checkType: index-prefix
                  target: "research:"
                  errorTemplate: "No research found."
            """;

        // Act — entity('create', { type: 'workflow', definition: yamlString })
        string result = await ScriniaProjectTools.EntityDispatch("create", "workflow",
            definition: yamlDefinition,
            cancellationToken: CancellationToken.None);

        // Assert — should succeed and store as JSON on disk
        var r = ResponseParser.Parse(result);
        r.Status.Should().Be("success",
            "creating a workflow from YAML input should not produce an error");
        r.Content.Should().Contain("yaml-created",
            "result should reference the workflow name");

        // Verify the stored file is valid JSON (not YAML)
        var store = MemoryStoreContext.Current!;
        string baseDir = ScriniaProjectTools.GetScriniaBaseDir(store);
        string storedPath = Path.Combine(baseDir, "workflows", "yaml-created.json");
        File.Exists(storedPath).Should().BeTrue(
            "workflow should be stored as a .json file on disk");

        string storedJson = await File.ReadAllTextAsync(storedPath);
        var storedWf = JsonSerializer.Deserialize(storedJson, PlanningJsonContext.Default.WorkflowDefinition);
        storedWf.Should().NotBeNull();
        storedWf!.Name.Should().Be("yaml-created");
        storedWf.SeedActivities.Should().HaveCount(1,
            "stored workflow should have 1 seed activity from YAML input");
        storedWf.SeedActivities[0].Id.Should().Be("researcher");
        storedWf.SeedActivities[0].Prompt.Should().Contain("YAML input",
            "stored content should preserve the YAML input text");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 17. RequiredOutputs — model validation tests
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_RequiredOutputs_ValidEntries_Passes()
    {
        var wf = new WorkflowDefinition(
            Name: "ro-valid",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"),
                    RequiredOutputs:
                    [
                        new GateValidation("index-prefix", "research:{goalShort}", "No research.", "Store research first."),
                        new GateValidation("memory-exists", "project:requirements", "No requirements.", "Create requirements.")
                    ])
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().BeEmpty(
            "workflow with valid RequiredOutputs entries should pass validation");
    }

    [Fact]
    public void Validate_RequiredOutputs_InvalidCheckType_Fails()
    {
        var wf = new WorkflowDefinition(
            Name: "ro-bad-check",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"),
                    RequiredOutputs:
                    [
                        new GateValidation("nonexistent-check", "target:", "err", "instr")
                    ])
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("RequiredOutputs") && e.Contains("CheckType") && e.Contains("nonexistent-check"),
            "RequiredOutputs with invalid CheckType should produce a validation error");
    }

    [Fact]
    public void Validate_RequiredOutputs_EmptyTarget_Fails()
    {
        var wf = new WorkflowDefinition(
            Name: "ro-empty-target",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"),
                    RequiredOutputs:
                    [
                        new GateValidation("memory-exists", "", "err", "instr")
                    ])
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("RequiredOutputs") && e.Contains("Target") && e.Contains("non-empty"),
            "RequiredOutputs with empty Target should produce a validation error");
    }

    [Fact]
    public void Validate_RequiredOutputs_Null_Passes()
    {
        // Backward compat: RequiredOutputs=null should not cause validation errors
        var wf = new WorkflowDefinition(
            Name: "ro-null",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"),
                    RequiredOutputs: null)
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().BeEmpty(
            "workflow with null RequiredOutputs should pass validation (backward compat)");
    }

    [Fact]
    public void Validate_RequiredOutputs_Empty_Passes()
    {
        // Empty array should not cause validation errors
        var wf = new WorkflowDefinition(
            Name: "ro-empty",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"),
                    RequiredOutputs: [])
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().BeEmpty(
            "workflow with empty RequiredOutputs array should pass validation");
    }

    [Fact]
    public void DefaultGoalWorkflow_SeedActivities_HaveRequiredOutputs()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        foreach (var seed in workflow.SeedActivities)
        {
            seed.RequiredOutputs.Should().NotBeNull(
                $"DefaultGoalWorkflow seed '{seed.Id}' should have RequiredOutputs");
            seed.RequiredOutputs.Should().NotBeEmpty(
                $"DefaultGoalWorkflow seed '{seed.Id}' should have at least one RequiredOutput");
        }
    }

    [Fact]
    public void QuickFixWorkflow_SeedActivities_HaveRequiredOutputs()
    {
        var wf = WorkflowDefinition.QuickFixWorkflow;
        foreach (var seed in wf.SeedActivities)
        {
            seed.RequiredOutputs.Should().NotBeNull(
                $"QuickFixWorkflow seed '{seed.Id}' should have RequiredOutputs");
            seed.RequiredOutputs.Should().NotBeEmpty(
                $"QuickFixWorkflow seed '{seed.Id}' should have at least one RequiredOutput");
        }
    }

    [Fact]
    public void RequiredOutputs_JsonRoundTrip()
    {
        var original = WorkflowDefinition.DefaultGoalWorkflow;

        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.WorkflowDefinition);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.WorkflowDefinition);
        deserialized.Should().NotBeNull();

        // Verify each seed's RequiredOutputs survived the roundtrip
        foreach (var originalSeed in original.SeedActivities)
        {
            var deserializedSeed = deserialized!.SeedActivities.First(a => a.Id == originalSeed.Id);
            deserializedSeed.RequiredOutputs.Should().NotBeNull(
                $"deserialized seed '{originalSeed.Id}' should have RequiredOutputs");
            deserializedSeed.RequiredOutputs!.Length.Should().Be(originalSeed.RequiredOutputs!.Length,
                $"deserialized seed '{originalSeed.Id}' RequiredOutputs count should match original");

            for (int i = 0; i < originalSeed.RequiredOutputs.Length; i++)
            {
                var origRo = originalSeed.RequiredOutputs[i];
                var deserRo = deserializedSeed.RequiredOutputs[i];
                deserRo.CheckType.Should().Be(origRo.CheckType,
                    $"seed '{originalSeed.Id}' RequiredOutputs[{i}].CheckType should match");
                deserRo.Target.Should().Be(origRo.Target,
                    $"seed '{originalSeed.Id}' RequiredOutputs[{i}].Target should match");
                deserRo.ErrorTemplate.Should().Be(origRo.ErrorTemplate,
                    $"seed '{originalSeed.Id}' RequiredOutputs[{i}].ErrorTemplate should match");
            }
        }
    }

    // ══════════════════════════════════════════════════════════════════════════
    // Agent-specialist seed tests
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void EmbeddedPrompts_LoadsAgentSpecialist()
    {
        string? content = EmbeddedPrompts.Load("skills/agent-specialist.md");
        content.Should().NotBeNullOrWhiteSpace(
            "EmbeddedPrompts should load the agent-specialist skill markdown");
        content.Should().Contain("Agent Specialist",
            "agent-specialist.md should contain its role header");
    }

    [Fact]
    public void DefaultGoalWorkflow_Has4Seeds()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        workflow.SeedActivities.Should().HaveCount(4,
            "DefaultGoalWorkflow should have 4 seed activities: agent-specialist, researcher, auditor, planner");
    }

    [Fact]
    public void DefaultGoalWorkflow_AgentSpecialistIsWave0()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var agentSpecialist = workflow.SeedActivities.First(a => a.Id == "agent-specialist");
        agentSpecialist.Wave.Should().Be(0,
            "agent-specialist should be wave 0 (runs first)");
        agentSpecialist.DependsOn.Should().BeEmpty(
            "agent-specialist should have no dependencies");
        agentSpecialist.Phase.Should().Be("00",
            "agent-specialist should be in phase 00");
        agentSpecialist.Tag.Should().Be("agent-specialist",
            "agent-specialist Tag should match its id");
    }

    [Fact]
    public void DefaultGoalWorkflow_ResearcherDependsOnAgentSpecialist()
    {
        var workflow = WorkflowDefinition.DefaultGoalWorkflow;
        var researcher = workflow.SeedActivities.First(a => a.Id == "researcher");
        researcher.DependsOn.Should().Contain("agent-specialist",
            "researcher should depend on agent-specialist");
        researcher.Wave.Should().Be(1,
            "researcher should be wave 1 (runs after agent-specialist)");
    }

    // ══════════════════════════════════════════════════════════════════════════
    // 18. New v2 validation — Type, Role, spawner constraints
    // ══════════════════════════════════════════════════════════════════════════

    [Fact]
    public void Validate_InvalidType_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "bad-type",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"),
                    Type: "invalid-type")
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Type") && e.Contains("invalid-type"),
            "invalid Type should produce a validation error");
    }

    [Fact]
    public void Validate_InvalidRole_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "bad-role",
            Activities:
            [
                new WorkflowActivity("r", "00", 0, "builtin:r", [], "researcher",
                    "content", new GateValidation("index-prefix", "research:", "err"),
                    Role: "invalid-role")
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("Role") && e.Contains("invalid-role"),
            "invalid Role should produce a validation error");
    }

    [Fact]
    public void Validate_MultipleSpawners_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "multi-spawner",
            Activities:
            [
                new WorkflowActivity("s1", "00", 0, "builtin:s1", [], "spawner1",
                    "content1", null, Type: "spawner"),
                new WorkflowActivity("s2", "00", 1, "builtin:s2", [], "spawner2",
                    "content2", null, Type: "spawner")
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("spawner"),
            "multiple spawners should produce a validation error");
    }

    [Fact]
    public void Validate_NoSeedActivities_ReturnsError()
    {
        var wf = new WorkflowDefinition(
            Name: "no-seeds",
            Activities:
            [
                new WorkflowActivity("qa-gate", null, null, "builtin:qa", ["*"], "qa",
                    "Run QA", new GateValidation("memory-exists", "qa:latest", "err"),
                    Type: "agent", Role: "post-plan")
            ]);

        var errors = WorkflowDefinition.Validate(wf);
        errors.Should().Contain(e => e.Contains("seed"),
            "workflow with no seed activities should produce a validation error");
    }
}
