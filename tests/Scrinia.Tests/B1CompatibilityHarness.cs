using Scrinia.Core;
using Scrinia.Core.Migration;
using Scrinia.Mcp;
using Scrinia.Tests.Golden;
using Xunit;

namespace Scrinia.Tests;

public class B1CompatibilityHarness
{
    [Fact]
    public void Remember_And_Import_MapThroughDifferentWrappersToSameEnvelope()
    {
        WholeMemoryResult remember = new(WholeMemoryOutcome.Created, null, "op", null, null);
        ImportMemoryResult import = new(WholeMemoryOutcome.Created, null, "op", null, null);
        Assert.Equal(B0ResultWrappers.FromWhole(remember), B0ResultWrappers.FromImport(import));
    }

    [Fact]
    public void Migration_BareComplete_IsCommittedAndEmptyMigration_IsCommitted()
    {
        B0Result empty = B0ResultWrappers.FromMigration(new StoreLayoutMigration.Result(0, 0, Ran: false, null), null);
        Assert.Equal(B0Outcome.Committed, empty.Outcome);
        Assert.Null(empty.Failure);
    }

    [Fact]
    public void Migration_Conflict_IsSuccessWithWarningViaWholeMemoryTaxonomy()
    {
        B0Result conflict = B0ResultWrappers.FromMigration(
            new StoreLayoutMigration.Result(1, 1, Ran: true, null), ".scrinia-conflicts-20260918");
        Assert.Equal(B0Outcome.SuccessWithWarning, conflict.Outcome);
        Assert.Equal(B0FailureTaxonomy.WholeMemory, conflict.Failure!.Taxonomy);
    }

    [Fact]
    public void BundleExport_Successful_EnvelopeEqualsPreB1Golden()
    {
        string golden = File.ReadAllText(Path.Combine("Golden", "bundle-success.yaml"));
        McpResponse rendered = BundleEnvelopeFixture.RenderSuccess();
        Assert.Equal(golden, rendered.ToYaml());
    }
}
