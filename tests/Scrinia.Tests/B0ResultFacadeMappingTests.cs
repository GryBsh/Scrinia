using System.Runtime.CompilerServices;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Scrinia.Tests.Golden;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Proves the B1 remember-facade refactor is behaviour-preserving. The golden-render case pins the
/// four <c>remember</c> envelopes (Created / Replaced / SuccessWithWarning / RecoveryRequired) to the
/// pre-B1 baselines; the mapping case pins <see cref="B0ResultWrappers.FromWhole"/> to the shared
/// whole-memory outcome mapper. B1 only re-routes the <c>success_with_warning</c> promotion through
/// <c>FromWhole</c> — every rendered field must stay byte-identical to the captured baseline.
/// </summary>
public class B0ResultFacadeMappingTests
{
    /// <summary>Every whole-memory outcome maps through FromWhole to its expected B0 outcome. This is
    /// the mapping the success_with_warning promotion relies on — it fails if FromWhole diverges.
    /// Params are int-cast so the internal enum types never surface on the public xUnit method.</summary>
    [Theory]
    [InlineData((int)WholeMemoryOutcome.Created, (int)B0Outcome.Created)]
    [InlineData((int)WholeMemoryOutcome.Replaced, (int)B0Outcome.Replaced)]
    [InlineData((int)WholeMemoryOutcome.SuccessWithWarning, (int)B0Outcome.SuccessWithWarning)]
    [InlineData((int)WholeMemoryOutcome.RecoveryRequired, (int)B0Outcome.RecoveryRequired)]
    public void FromWhole_Outcome_MatchesExpectedB0Outcome(int outcomeValue, int expectedValue)
    {
        WholeMemoryOutcome outcome = (WholeMemoryOutcome)outcomeValue;
        B0Outcome expected = (B0Outcome)expectedValue;
        WholeMemoryResult result = new(outcome, null, "op-remember",
            outcome == WholeMemoryOutcome.SuccessWithWarning ? "cleanup pending" : null,
            outcome == WholeMemoryOutcome.RecoveryRequired
                ? new RecoveryInfo("/scope/.append-journal.json", "recoveryRequired", "resolve", "blocked")
                : null);
        B0Result b0 = B0ResultWrappers.FromWhole(result);

        Assert.Equal(expected, b0.Outcome);
        Assert.Equal("op-remember", b0.OperationId);
        Assert.Equal("op-remember", b0.CorrelationId);
    }

    /// <summary>The live remember facade must still render each envelope exactly as captured pre-B1.
    /// Fields that are a pure function of the input (status / action / content) compare byte-for-byte;
    /// volatile fields (operationId GUID, temp workspace path) are normalised so the pin is structural,
    /// not timestamp- or machine-dependent — the same rule the existing response-golden harness applies.</summary>
    [Theory]
    [InlineData("created")]
    [InlineData("replaced")]
    [InlineData("success-with-warning")]
    [InlineData("recovery-required")]
    public async Task Remember_Envelope_IsUnchangedAfterB0Routing(string outcomeTag)
    {
        McpResponse rendered = await RememberEnvelopeFixture.RenderAsync(outcomeTag);
        string actual = GoldenNormalizer.Normalize(rendered.ToYaml());

        string path = GoldenPath(outcomeTag);
        string committed = File.ReadAllText(path);
        string expected = GoldenNormalizer.Normalize(committed);

        Assert.Equal(expected, actual);
    }

    /// <summary>Goldens live next to the source, not in bin/ — they are committed artifacts.</summary>
    private static string GoldenPath(string outcomeTag, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", $"remember-{outcomeTag}.yaml");
}
