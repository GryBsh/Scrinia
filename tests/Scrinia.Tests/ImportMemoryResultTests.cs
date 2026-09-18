using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Coverage for the per-memory import result (<see cref="ImportMemoryResult"/>) that
/// <c>FileMemoryStore.ImportTopicEntries</c> returns: the B0 boundary maps it through
/// <c>FromImport</c> identically to <c>FromWhole</c>, and the store reports the Created/Replaced
/// outcome threaded from the journaled publish plus the scoped restore-journal transaction id.
/// </summary>
public class ImportTopicEntriesResultTests
{
    [Fact]
    public void FromImport_MapsImportResultToSameEnvelopeAsRemember()
    {
        ImportMemoryResult import = new(
            WholeMemoryOutcome.Created, null,
            "47bf1102-0000-0000-0000-000000000000", null, null);
        WholeMemoryResult remember = new(
            WholeMemoryOutcome.Created, null,
            "47bf1102-0000-0000-0000-000000000000", null, null);
        Assert.Equal(
            B0ResultWrappers.FromWhole(remember),
            B0ResultWrappers.FromImport(import));
    }

    [Fact]
    public void ImportTopicEntries_ReturnsCreatedForNewMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        var result = ImportFixture.ImportOneTopic(scope, "fresh.nmp2");
        Assert.Equal(WholeMemoryOutcome.Created, result.Outcome);
        // The operation id is the scoped RestoreJournal.TransactionId — a canonical N-format GUID.
        Assert.False(string.IsNullOrEmpty(result.OperationId));
        Assert.True(Guid.TryParseExact(result.OperationId, "N", out _));
    }

    [Fact]
    public void ImportTopicEntries_ReturnsReplacedForExistingMemory()
    {
        using var scope = new TestHelpers.StoreScope();
        ImportFixture.Prepopulate(scope, "existing.nmp2");
        var result = ImportFixture.ImportOneTopic(scope, "existing.nmp2");
        Assert.Equal(WholeMemoryOutcome.Replaced, result.Outcome);
    }
}
