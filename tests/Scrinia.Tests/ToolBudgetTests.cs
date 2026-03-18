using System.Reflection;
using System.Text.Json;
using FluentAssertions;
using ModelContextProtocol.Server;
using Scrinia.Mcp;

namespace Scrinia.Tests;

public sealed class ToolBudgetTests
{
    [Fact]
    public void TotalToolCount_IsUnder50()
    {
        // Find all types with [McpServerToolType] in Scrinia.Mcp assembly
        var mcpAssembly = typeof(ScriniaMcpTools).Assembly;

        var toolTypes = mcpAssembly.GetTypes()
            .Where(t => t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .ToList();

        // Count all methods with [McpServerTool] attribute across all tool types
        int totalTools = toolTypes
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static))
            .Count(m => m.GetCustomAttribute<McpServerToolAttribute>() is not null);

        totalTools.Should().BeLessThan(50,
            $"total MCP tool count ({totalTools}) must stay under 50 to fit MCP client limits");
    }

    [Fact]
    public void ScriniaProjectTools_HasMcpServerToolTypeAttribute()
    {
        var attr = typeof(ScriniaProjectTools).GetCustomAttribute<McpServerToolTypeAttribute>();
        attr.Should().NotBeNull("ScriniaProjectTools must be annotated with [McpServerToolType]");
    }

    [Fact]
    public void PlanningJsonContext_SerializesProjectRecord()
    {
        // Arrange
        var record = new ProjectRecord(
            Id: "proj-1",
            Name: "Test Project",
            Description: "A test project",
            Goals: ["goal-1", "goal-2"],
            Constraints: ["constraint-1"]);

        // Act
        string json = JsonSerializer.Serialize(record, PlanningJsonContext.Default.ProjectRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.ProjectRecord);

        // Assert
        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(record.Id);
        deserialized.Name.Should().Be(record.Name);
        deserialized.Description.Should().Be(record.Description);
        deserialized.Goals.Should().BeEquivalentTo(record.Goals);
        deserialized.Constraints.Should().BeEquivalentTo(record.Constraints);
    }

    [Fact]
    public void PlanningJsonContext_SerializesPlanRecord()
    {
        var record = new PlanRecord(
            Id: "plan-01",
            Phase: "01-foundation",
            Goal: "Establish foundation",
            Status: "in-progress",
            TaskIds: ["task-1", "task-2"]);

        string json = JsonSerializer.Serialize(record, PlanningJsonContext.Default.PlanRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.PlanRecord);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(record.Id);
        deserialized.Phase.Should().Be(record.Phase);
        deserialized.Goal.Should().Be(record.Goal);
        deserialized.Status.Should().Be(record.Status);
        deserialized.TaskIds.Should().BeEquivalentTo(record.TaskIds);
    }

    [Fact]
    public void PlanningJsonContext_SerializesTaskRecord()
    {
        var record = new TaskRecord(
            Id: "task-1",
            Phase: "01-foundation",
            Name: "Create ScriniaProjectTools",
            Description: "Set up the scaffold",
            Status: "complete",
            DependsOn: [],
            AcceptanceCriteria: ["class exists", "tests pass"]);

        string json = JsonSerializer.Serialize(record, PlanningJsonContext.Default.TaskRecord);
        var deserialized = JsonSerializer.Deserialize(json, PlanningJsonContext.Default.TaskRecord);

        deserialized.Should().NotBeNull();
        deserialized!.Id.Should().Be(record.Id);
        deserialized.Name.Should().Be(record.Name);
        deserialized.AcceptanceCriteria.Should().BeEquivalentTo(record.AcceptanceCriteria);
    }
}
