using System.IO;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class B0FaultSeamTests
{
    [Fact]
    public void ThrowOnceAt_ThrowsOnceThenClears()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (phase, tag) => new IOException($"boom {phase} {tag}");
            B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.AppendJournalWrite, "op-1");

            var first = Assert.Throws<IOException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.AppendJournalWrite, "op-1"));
            Assert.Contains("boom", first.Message);

            // Once-only: the second call at the same (phase, tag) must not throw again.
            Assert.Null(B0FaultSeam.ThrowIfInjectedOrNull(B0FaultSeam.FaultPhase.AppendJournalWrite, "op-1"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void FailPersistentlyAt_ThrowsEveryTime()
    {
        B0FaultSeam.ClearFaults();
        try
        {
            B0FaultSeam.Inject = (_phase, _tag) => new TimeoutException("held");
            B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.MultiCommittedPublished, "tx-9");
            Assert.Throws<TimeoutException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.MultiCommittedPublished, "tx-9"));
            Assert.Throws<TimeoutException>(
                () => B0FaultSeam.ThrowIfInjected(B0FaultSeam.FaultPhase.MultiCommittedPublished, "tx-9"));
        }
        finally { B0FaultSeam.ClearFaults(); B0FaultSeam.Inject = null; }
    }

    [Fact]
    public void ClearFaults_ResetsAllScheduledSeams()
    {
        B0FaultSeam.Inject = (_phase, _tag) => new IOException("x");
        B0FaultSeam.FailPersistentlyAt(B0FaultSeam.FaultPhase.WholeEvidenceWrite, "a");
        B0FaultSeam.ClearFaults();
        Assert.Null(B0FaultSeam.ThrowIfInjectedOrNull(B0FaultSeam.FaultPhase.WholeEvidenceWrite, "a"));
    }

    [Fact]
    public void FaultPhase_CoversEverySourceEnumMember()
    {
        // DurableWritePhase(3) + AppendFaultPoint(21) + WholeMemoryFaultPoint(12) +
        // RestoreJournalTransition(9) + MultiMemoryTransactionTransition(4) + FileEntityFaultPoint(10)
        // = 59. (The plan's "24/62" arithmetic is a typo; the real source enums have 21 append
        // members and 59 total — verified against the six enums below.)
        Assert.Equal(59, Enum.GetValues(typeof(B0FaultSeam.FaultPhase)).Length);
        Assert.Equal(3, Enum.GetNames(typeof(DurableWritePhase)).Length);
        Assert.Equal(21, Enum.GetNames(typeof(AppendFaultPoint)).Length);
        Assert.Equal(12, Enum.GetNames(typeof(WholeMemoryFaultPoint)).Length);
        Assert.Equal(9, Enum.GetNames(typeof(RestoreJournalTransition)).Length);
        Assert.Equal(4, Enum.GetNames(typeof(MultiMemoryTransactionTransition)).Length);
        Assert.Equal(10, Enum.GetNames(typeof(FileEntityFaultPoint)).Length);
    }
}
