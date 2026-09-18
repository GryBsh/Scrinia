using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public class B0OutcomeVocabularyTests
{
    [Fact]
    public void B0Outcome_HasExactlyTheNineUnionMembers()
    {
        var expected = new[]
        {
            nameof(B0Outcome.Created), nameof(B0Outcome.Appended), nameof(B0Outcome.Replaced),
            nameof(B0Outcome.Committed), nameof(B0Outcome.CommittedCleanupPending),
            nameof(B0Outcome.RolledBack), nameof(B0Outcome.Rejected),
            nameof(B0Outcome.SuccessWithWarning), nameof(B0Outcome.RecoveryRequired)
        };
        Assert.Equal(9, Enum.GetValues(typeof(B0Outcome)).Length);
        Assert.Equal(expected, Enum.GetNames(typeof(B0Outcome)));
    }

    [Fact]
    public void B0Failure_RecordsTheProducingTaxonomy()
    {
        B0Failure failure = new(B0FailureTaxonomy.MultiMemory, nameof(MultiMemoryFailure.LockTimeout), "lock held");
        Assert.Equal(B0FailureTaxonomy.MultiMemory, failure.Taxonomy);
        Assert.Equal("LockTimeout", failure.Code);
        Assert.Equal("lock held", failure.Detail);
    }

    [Fact]
    public void B0Outcome_ContainsEveryMemberOfAllFourSourceEnums()
    {
        var union = Enum.GetNames(typeof(B0Outcome));
        foreach (var name in Enum.GetNames(typeof(AppendStoreOutcome)))
            Assert.Contains(name, union);
        foreach (var name in Enum.GetNames(typeof(WholeMemoryOutcome)))
            Assert.Contains(name, union);
        foreach (var name in Enum.GetNames(typeof(MultiMemoryOutcome)))
            Assert.Contains(name, union);
    }
}
