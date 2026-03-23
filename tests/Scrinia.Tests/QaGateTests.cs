using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for plan_verify behavior — checklist mode returns criteria
/// without requiring evidence or blocking.
/// </summary>
public sealed class QaGateTests : IDisposable
{
    private readonly TestHelpers.StoreScope _scope;
    private readonly ScriniaProjectTools _tools;
    private readonly ScriniaMcpTools _memTools;

    public QaGateTests()
    {
        _scope = new TestHelpers.StoreScope();
        _tools = new ScriniaProjectTools();
        _memTools = new ScriniaMcpTools();
    }

    public void Dispose() => _scope.Dispose();

    [Fact]
    public async Task PlanVerify_ChecklistModeStillWorks()
    {
        // Arrange — init project with requirements and a task referencing CRIT-01, no evidence
        await _tools.ProjectInit("Goals: build a test project", cancellationToken: CancellationToken.None);
        await _tools.PlanRequirements("- CRIT-01: task storage", cancellationToken: CancellationToken.None);
        // Create a task that references CRIT-01 so plan_verify can discover the criterion
        await _tools.PlanTasks("01",
            "## Task 01\nDepends on: none\nAction: Implement CRIT-01 — task storage\nAcceptance criteria:\n- tasks stored",
            cancellationToken: CancellationToken.None);

        // Act — call plan_verify with no evidence
        string result = await _tools.PlanVerify("01", cancellationToken: CancellationToken.None);

        // Assert — should return checklist
        result.Should().Contain("Verification Checklist",
            "checklist mode should return a verification checklist");
        result.Should().NotContain("Blocked",
            "checklist mode should never be blocked");
    }
}
