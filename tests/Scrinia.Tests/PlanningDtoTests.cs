using System.Text.Json;
using FluentAssertions;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Round-trip serialization tests for planning DTOs registered in PlanningJsonContext.
/// Verifies that ConcernRecord, SkillRecord, ResearchRecord, and GoalRecord are all
/// trimming-safe via source-gen JSON context registration.
/// </summary>
public sealed class PlanningDtoTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        TypeInfoResolver = PlanningJsonContext.Default,
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull
    };

    // ── ConcernRecord tests ───────────────────────────────────────────────────

    [Fact]
    public void ConcernRecord_RoundTrip_AllFieldsMatch()
    {
        // Arrange
        var original = new ConcernRecord(
            Id: "concern-01",
            Phase: "05",
            Description: "Risk of silent trimming failures",
            Severity: "high",
            Status: "open",
            Resolution: null,
            ResolvedAt: null);

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.ConcernRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ConcernRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.Phase.Should().Be(original.Phase);
        deserialized.Description.Should().Be(original.Description);
        deserialized.Severity.Should().Be(original.Severity);
        deserialized.Status.Should().Be(original.Status);
        deserialized.Resolution.Should().BeNull();
        deserialized.ResolvedAt.Should().BeNull();
    }

    [Fact]
    public void ConcernRecord_RoundTrip_WithAllNullableFields()
    {
        // Arrange
        var original = new ConcernRecord(
            Id: "concern-02",
            Phase: "06",
            Description: "Concern fully resolved",
            Severity: "low",
            Status: "resolved",
            Resolution: "Fixed by adding JsonSerializable attributes",
            ResolvedAt: "2026-03-19");

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.ConcernRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ConcernRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Resolution.Should().Be(original.Resolution);
        deserialized.ResolvedAt.Should().Be(original.ResolvedAt);
    }

    [Fact]
    public void ConcernRecord_Array_RoundTrip()
    {
        // Arrange
        var concerns = new[]
        {
            new ConcernRecord("c-01", "05", "First concern", "high", "open", null, null),
            new ConcernRecord("c-02", "06", "Second concern", "medium", "resolved", "Fixed", "2026-03-19")
        };

        // Act
        string json = JsonSerializer.Serialize(concerns, PlanningJsonContext.Default.ConcernRecordArray);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ConcernRecordArray);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Should().HaveCount(2);
        deserialized[0].Id.Should().Be("c-01");
        deserialized[1].Id.Should().Be("c-02");
    }

    [Fact]
    public void ConcernRecord_NullableFields_DefaultToNullWhenAbsentInJson()
    {
        // Arrange — JSON without optional fields
        string json = """{"id":"c-01","phase":"05","description":"A concern","severity":"low"}""";

        // Act
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ConcernRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Status.Should().BeNull();
        deserialized.Resolution.Should().BeNull();
        deserialized.ResolvedAt.Should().BeNull();
    }

    // ── SkillRecord tests ─────────────────────────────────────────────────────

    [Fact]
    public void SkillRecord_RoundTrip_AllFieldsMatch()
    {
        // Arrange
        var original = new SkillRecord(
            Id: "skill-01",
            Name: "CodeReviewer",
            Description: "Reviews code for correctness and style",
            SystemPrompt: "You are a senior code reviewer...",
            Tools: ["read_file", "search"],
            Capabilities: ["code_review", "security_analysis"]);

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.SkillRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.SkillRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.Name.Should().Be(original.Name);
        deserialized.Description.Should().Be(original.Description);
        deserialized.SystemPrompt.Should().Be(original.SystemPrompt);
        deserialized.Tools.Should().BeEquivalentTo(original.Tools);
        deserialized.Capabilities.Should().BeEquivalentTo(original.Capabilities);
    }

    [Fact]
    public void SkillRecord_RoundTrip_NullableArraysHandled()
    {
        // Arrange — minimal fields, no arrays
        var original = new SkillRecord(
            Id: "skill-02",
            Name: "MinimalSkill",
            Description: null,
            SystemPrompt: null,
            Tools: null,
            Capabilities: null);

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.SkillRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.SkillRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Description.Should().BeNull();
        deserialized.SystemPrompt.Should().BeNull();
        deserialized.Tools.Should().BeNull();
        deserialized.Capabilities.Should().BeNull();
    }

    [Fact]
    public void SkillRecord_Array_RoundTrip()
    {
        // Arrange
        var skills = new[]
        {
            new SkillRecord("s-01", "Skill A", null, null, null, null),
            new SkillRecord("s-02", "Skill B", "Description B", "Prompt B", ["tool1"], ["cap1"])
        };

        // Act
        string json = JsonSerializer.Serialize(skills, PlanningJsonContext.Default.SkillRecordArray);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.SkillRecordArray);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Should().HaveCount(2);
        deserialized[0].Name.Should().Be("Skill A");
        deserialized[1].Name.Should().Be("Skill B");
    }

    [Fact]
    public void SkillRecord_NullableFields_DefaultToNullWhenAbsentInJson()
    {
        // Arrange — JSON without optional fields
        string json = """{"id":"s-01","name":"MinimalSkill"}""";

        // Act
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.SkillRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Description.Should().BeNull();
        deserialized.SystemPrompt.Should().BeNull();
        deserialized.Tools.Should().BeNull();
        deserialized.Capabilities.Should().BeNull();
    }

    // ── ResearchRecord tests ──────────────────────────────────────────────────

    [Fact]
    public void ResearchRecord_RoundTrip_AllFieldsMatch()
    {
        // Arrange
        var original = new ResearchRecord(
            Id: "research-01",
            Topic: "defer_loading availability in ModelContextProtocol 1.0.0",
            Question: "Is defer_loading: true available in ModelContextProtocol 1.0.0?",
            Status: "complete",
            Findings: "defer_loading is available as a bool property on McpServerTool attribute.",
            Sources: ["https://github.com/modelcontextprotocol/csharp-sdk"]);

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.ResearchRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ResearchRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.Topic.Should().Be(original.Topic);
        deserialized.Question.Should().Be(original.Question);
        deserialized.Status.Should().Be(original.Status);
        deserialized.Findings.Should().Be(original.Findings);
        deserialized.Sources.Should().BeEquivalentTo(original.Sources);
    }

    [Fact]
    public void ResearchRecord_RoundTrip_NullableFieldsHandled()
    {
        // Arrange — minimal required fields only
        var original = new ResearchRecord(
            Id: "research-02",
            Topic: "Minimal topic",
            Question: null,
            Status: null,
            Findings: null,
            Sources: null);

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.ResearchRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ResearchRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Question.Should().BeNull();
        deserialized.Status.Should().BeNull();
        deserialized.Findings.Should().BeNull();
        deserialized.Sources.Should().BeNull();
    }

    [Fact]
    public void ResearchRecord_Array_RoundTrip()
    {
        // Arrange
        var records = new[]
        {
            new ResearchRecord("r-01", "Topic A", null, "pending", null, null),
            new ResearchRecord("r-02", "Topic B", "Question B?", "complete", "Findings B", ["source1"])
        };

        // Act
        string json = JsonSerializer.Serialize(records, PlanningJsonContext.Default.ResearchRecordArray);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ResearchRecordArray);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Should().HaveCount(2);
        deserialized[0].Topic.Should().Be("Topic A");
        deserialized[1].Topic.Should().Be("Topic B");
    }

    [Fact]
    public void ResearchRecord_NullableFields_DefaultToNullWhenAbsentInJson()
    {
        // Arrange — JSON with only required fields
        string json = """{"id":"r-01","topic":"Some topic"}""";

        // Act
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ResearchRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Question.Should().BeNull();
        deserialized.Status.Should().BeNull();
        deserialized.Findings.Should().BeNull();
        deserialized.Sources.Should().BeNull();
    }

    // ── GoalRecord tests ──────────────────────────────────────────────────────

    [Fact]
    public void GoalRecord_RoundTrip_AllFieldsMatch()
    {
        // Arrange
        var original = new GoalRecord(
            Id: "goal-01",
            Description: "Agents use scrinia tools organically because they make the agent more effective",
            Status: "active",
            Outcome: null,
            CompletedAt: null);

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.GoalRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GoalRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(original.Id);
        deserialized.Description.Should().Be(original.Description);
        deserialized.Status.Should().Be(original.Status);
        deserialized.Outcome.Should().BeNull();
        deserialized.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void GoalRecord_RoundTrip_WithAllNullableFields()
    {
        // Arrange
        var original = new GoalRecord(
            Id: "goal-02",
            Description: "Completed goal",
            Status: "complete",
            Outcome: "All 12 planning tools shipped",
            CompletedAt: "2026-06-01");

        // Act
        string json = JsonSerializer.Serialize(original, PlanningJsonContext.Default.GoalRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GoalRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Outcome.Should().Be(original.Outcome);
        deserialized.CompletedAt.Should().Be(original.CompletedAt);
    }

    [Fact]
    public void GoalRecord_Array_RoundTrip()
    {
        // Arrange
        var goals = new[]
        {
            new GoalRecord("g-01", "First goal", "active", null, null),
            new GoalRecord("g-02", "Second goal", "complete", "Achieved", "2026-03-19")
        };

        // Act
        string json = JsonSerializer.Serialize(goals, PlanningJsonContext.Default.GoalRecordArray);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GoalRecordArray);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Should().HaveCount(2);
        deserialized[0].Description.Should().Be("First goal");
        deserialized[1].Description.Should().Be("Second goal");
    }

    [Fact]
    public void GoalRecord_NullableFields_DefaultToNullWhenAbsentInJson()
    {
        // Arrange — JSON with only required fields
        string json = """{"id":"g-01","description":"Some goal"}""";

        // Act
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.GoalRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Status.Should().BeNull();
        deserialized.Outcome.Should().BeNull();
        deserialized.CompletedAt.Should().BeNull();
    }
}
