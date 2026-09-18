using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class RecoveryLifecycleStateMapperTests
{
    // `RecoveryLifecycleState` is internal (RecoveryContracts.cs:15) and cannot appear as a
    // public theory parameter without CS0051. Drive the theory by the enum's underlying int
    // and cast back inside the body so every state is still projected across all four vocabularies.
    [Theory]
    [InlineData((int)RecoveryLifecycleState.Prepared, "prepared", "Prepared", null, "Prepared")]
    [InlineData((int)RecoveryLifecycleState.Applying, "applying", "ArchiveInProgress", null, null)]
    [InlineData((int)RecoveryLifecycleState.Committed, "committed", "Committed", null, "Committed")]
    [InlineData((int)RecoveryLifecycleState.CleanupPending, "cleanupPending", "CleanupPending", null, null)]
    [InlineData((int)RecoveryLifecycleState.Cleaned, "cleaned", null, null, null)]
    [InlineData((int)RecoveryLifecycleState.RecoveryRequired, "recoveryRequired", "RecoveryRequired", "RecoveryRequired", "RecoveryRequired")]
    [InlineData((int)RecoveryLifecycleState.RolledBack, "rolledBack", "RolledBack", null, null)]
    public void ProjectsCanonicalEnumToAllFourVocabularies(
        int state, string wire, string? append, string? whole, string? multi)
    {
        RecoveryLifecycleState lifecycle = (RecoveryLifecycleState)state;
        Assert.Equal(wire, RecoveryLifecycleStateMapper.ToWireString(lifecycle));
        Assert.Equal(append, RecoveryLifecycleStateMapper.ToAppendJournalState(lifecycle));
        Assert.Equal(whole, RecoveryLifecycleStateMapper.ToWholeMemoryOutcome(lifecycle));
        Assert.Equal(multi, RecoveryLifecycleStateMapper.ToMultiMemoryState(lifecycle));
    }
}
