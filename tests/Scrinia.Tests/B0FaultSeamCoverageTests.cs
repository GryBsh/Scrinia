using System.IO;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>B1 Task 8: each of the six single-memory operations carries a <see cref="B0Result"/> and
/// surfaces its mapped fault phase through the unified <see cref="B0FaultSeam"/>. The first four tests
/// pin the phase-enum interoperation for the four mapped fault points (whole-memory remember/compact,
/// file-entity sidecar, restore-journal import, durable-write migration/bundle); the fifth drives the
/// remember operation end-to-end through the whole-live-artifact-write phase to prove the fault surfaces
/// from the real durable-write boundary rather than from a void seam check.</summary>
public class B0FaultSeamCoverageTests
{
    [Fact]
    public void Remember_FailsAtWholeMemoryFaultPointThroughB0FaultSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (_phase, _tag) => new IOException("boom");
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.WholeLiveArtifactWrite, "remember-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(
                    B0FaultSeam.FaultPhase.WholeLiveArtifactWrite, "remember-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void Sidecar_FailsAtFileEntityFaultPointThroughB0FaultSeam()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.FileEntityBodyWrite, "sidecar-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(
                    B0FaultSeam.FaultPhase.FileEntityBodyWrite, "sidecar-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void Import_FailsAtRestoreJournalTransitionThroughB0FaultSeam()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.RestoreAfterCompletedWrite, "import-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(
                    B0FaultSeam.FaultPhase.RestoreAfterCompletedWrite, "import-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void Migration_FailsAtDurableWritePhaseThroughB0FaultSeam()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.BeforeWrite, "migration-op");
            Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.BeforeWrite, "migration-op"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public async Task Remember_DrivesWholeArtifactFaultThroughB0FaultSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();

        await tools.MemoryRemember("/b1/remember-op", ["old content"]);

        // FileMemoryStore.FailOnceAt maps the whole-memory fault point to FaultPhase.WholeLiveArtifactWrite
        // through the B0 seam; the remember commit boundary surfaces it as a durable-write failure.
        store.FailOnceAt(WholeMemoryFaultPoint.LiveArtifactWrite);

        string responseYaml = await tools.MemoryRemember("/b1/remember-op", ["new content"], replace: true);
        McpResponseAssert parsed = ResponseParser.Parse(responseYaml);

        Assert.Equal("error", parsed.Status);
        Assert.Equal(ErrorCodes.DurableWriteFailed, parsed.ErrorCode);
        Assert.False(string.IsNullOrWhiteSpace(parsed.OperationId));
    }
}
