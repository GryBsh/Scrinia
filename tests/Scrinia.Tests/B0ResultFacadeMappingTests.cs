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

    /// <summary>Every file-entity (sidecar/skill/agent) outcome maps through FromFileEntity to the same
    /// B0 outcome FromWhole produces — this is the shared mapper the refactor centralises, and it is the
    /// guarantee that remember and sidecar cannot diverge. Params are int-cast so the internal enum types
    /// never surface on the public xUnit method.</summary>
    [Theory]
    [InlineData((int)WholeMemoryOutcome.Created, (int)B0Outcome.Created)]
    [InlineData((int)WholeMemoryOutcome.Replaced, (int)B0Outcome.Replaced)]
    [InlineData((int)WholeMemoryOutcome.SuccessWithWarning, (int)B0Outcome.SuccessWithWarning)]
    [InlineData((int)WholeMemoryOutcome.RecoveryRequired, (int)B0Outcome.RecoveryRequired)]
    public void FileEntityOutcome_MapsThroughSharedMapWholeOutcome(int outcomeValue, int expectedValue)
    {
        WholeMemoryOutcome outcome = (WholeMemoryOutcome)outcomeValue;
        B0Outcome expected = (B0Outcome)expectedValue;
        FileEntityMutationResult result = new(outcome, "op-sidecar",
            outcome == WholeMemoryOutcome.SuccessWithWarning ? "cleanup pending" : null,
            outcome == WholeMemoryOutcome.RecoveryRequired
                ? new RecoveryInfo("/scope/.file-entity-op.recovery", "recoveryRequired", "resolve", "blocked")
                : null);
        B0Result b0 = B0ResultWrappers.FromFileEntity(result);

        Assert.Equal(expected, b0.Outcome);
        Assert.Null(b0.Failure);
        Assert.Equal("op-sidecar", b0.OperationId);
        Assert.Equal("op-sidecar", b0.CorrelationId);
    }

    /// <summary>The live sidecar facade must still render each envelope exactly as captured pre-B1, and
    /// the outcome it renders must map through FromFileEntity to the SAME B0 outcome FromWhole produces —
    /// so remember and sidecar cannot diverge. The rendered status/action/content/scope compare through
    /// the normalised golden (operationId GUID and the file-entity recovery-evidence name are stripped);
    /// the FromFileEntity pin is a direct structural assertion.</summary>
    [Theory]
    [InlineData("created", (int)B0Outcome.Created)]
    [InlineData("success-with-warning", (int)B0Outcome.SuccessWithWarning)]
    [InlineData("recovery-required", (int)B0Outcome.RecoveryRequired)]
    public async Task Sidecar_Envelope_UnchangedAfterB0Routing(string outcomeTag, int expectedB0Outcome)
    {
        McpResponse rendered = await SidecarEnvelopeFixture.RenderAsync(outcomeTag);
        string actual = GoldenNormalizer.Normalize(rendered.ToYaml());

        string path = SidecarGoldenPath(outcomeTag);
        string committed = File.ReadAllText(path);
        string expectedYaml = GoldenNormalizer.Normalize(committed);
        Assert.Equal(expectedYaml, actual);

        WholeMemoryOutcome outcome = outcomeTag switch
        {
            "created" => WholeMemoryOutcome.Created,
            "success-with-warning" => WholeMemoryOutcome.SuccessWithWarning,
            "recovery-required" => WholeMemoryOutcome.RecoveryRequired,
            _ => throw new ArgumentOutOfRangeException(outcomeTag),
        };
        FileEntityMutationResult synthetic = new(outcome, "op-sidecar",
            outcome == WholeMemoryOutcome.SuccessWithWarning ? "cleanup pending" : null,
            outcome == WholeMemoryOutcome.RecoveryRequired
                ? new RecoveryInfo("/scope/.file-entity-op.recovery", "recoveryRequired", "resolve", "blocked")
                : null);
        B0Result b0 = B0ResultWrappers.FromFileEntity(synthetic);
        Assert.Equal((B0Outcome)expectedB0Outcome, b0.Outcome);
        Assert.Null(b0.Failure);
    }

    /// <summary>Goldens live next to the source, not in bin/ — they are committed artifacts.</summary>
    private static string GoldenPath(string outcomeTag, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", $"remember-{outcomeTag}.yaml");

    /// <summary>Sidecar goldens live next to the source, not in bin/ — they are committed artifacts.</summary>
    private static string SidecarGoldenPath(string outcomeTag, [CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", $"sidecar-{outcomeTag}.yaml");
}

/// <summary>Proves the B1 compact-facade refactor is behaviour-preserving. The golden-render case
/// pins the single <c>compact</c> <c>success_with_warning</c> envelope to its pre-B1 baseline; the
/// mapping case pins <see cref="B0ResultWrappers.FromWhole"/> to the shared whole-memory outcome
/// mapper that the success_with_warning promotion reads. B1 only re-routes the promotion through
/// <c>FromWhole</c> — every rendered field must stay byte-identical to the captured baseline.</summary>
public class CompactFacadeMappingTests
{
    /// <summary>The whole-memory success_with_warning outcome must map through FromWhole to the B0
    /// outcome the compact promotion checks, with the recovery state intact — this is the mapping
    /// the promotion relies on, so it fails if FromWhole drops or rewrites it.</summary>
    [Fact]
    public void Compact_SuccessWithWarning_B0Outcome_MapsThroughFromWhole()
    {
        WholeMemoryResult result = new(
            WholeMemoryOutcome.SuccessWithWarning, null, "op-compact",
            "Whole-memory commit succeeded, but archive cleanup requires attention.",
            new RecoveryInfo("/scope/.append-journal.json", "cleanupPending",
                "resolve retained evidence", "archive cleanup did not complete"));
        B0Result b0 = B0ResultWrappers.FromWhole(result);
        Assert.Equal(B0Outcome.SuccessWithWarning, b0.Outcome);
        Assert.NotNull(b0.Recovery);
        Assert.Equal("cleanupPending", b0.Recovery!.State);
    }

    /// <summary>The live compact facade must still render the success_with_warning envelope exactly
    /// as captured pre-B1. The volatile operationId GUID and temp workspace path are normalised so
    /// the pin is structural, not machine-dependent — the same rule the remember golden test uses.</summary>
    [Fact]
    public async Task Compact_Envelope_UnchangedAfterB0Routing()
    {
        string golden = File.ReadAllText(GoldenPath());
        McpResponse rendered = await CompactEnvelopeFixture.DriveSuccessWithWarningAsync();
        string actual = GoldenNormalizer.Normalize(rendered.ToYaml());
        string expected = GoldenNormalizer.Normalize(golden);

        Assert.Equal(expected, actual);
    }

    /// <summary>Goldens live next to the source, not in bin/ — they are committed artifacts.</summary>
    private static string GoldenPath([CallerFilePath] string thisFile = "") =>
        Path.Combine(Path.GetDirectoryName(thisFile)!, "Golden", "compact-success-with-warning.yaml");
}

/// <summary>
/// Drives the <c>compact</c> whole-memory <c>SuccessWithWarning</c> outcome through the live internal
/// <see cref="ScriniaMcpTools.Compact"/> facade so its golden envelope can be captured and pinned.
///
/// Reuses the whole-memory fault seam exactly as <c>McpWholeMemoryTransactionTests</c> and the
/// existing compact success_with warning test model it: a two-chunk write, then an <c>ArchiveCleanup</c>
/// prune failure on the compaction so the whole-memory commit lands but the facade promotes it to
/// success_with_warning. No new fault logic is invented here — the seam is reused.
/// </summary>
internal static class CompactEnvelopeFixture
{
    public static async Task<McpResponse> DriveSuccessWithWarningAsync()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var tools = new ScriniaMcpTools();
        await tools.Remember(["old one", "old two"], MemoryAddress.Parse("/b1/compact-warning"));
        // Post-commit prune/archive cleanup fails: the whole-memory commit still lands, but the
        // facade promotes the compaction to success_with_warning.
        store.FailOnceAt(WholeMemoryFaultPoint.ArchiveCleanup);
        return await tools.Compact(MemoryAddress.Parse("/b1/compact-warning"));
    }
}

/// <summary>
/// Drives the live <c>SkillCreate</c> sidecar facade through the three file-entity outcomes so their
/// golden envelopes can be captured and pinned. Mirrors the whole-memory fault seam exactly as
/// <c>RememberEnvelopeFixture</c> and the file-entity coordinator model it: <c>Created</c> is a fresh
/// write, <c>SuccessWithWarning</c> is an <c>EvidenceCleanup</c> prune failure on a rewrite (the
/// existing sidecar must be present first so <c>oldMeta.Existed</c> promotes the commit to a warning),
/// and <c>RecoveryRequired</c> is a rollback that cannot be proved (VerificationRead + RollbackWrite
/// inject). No new fault logic is invented here — the seam is reused.
/// </summary>
internal static class SidecarEnvelopeFixture
{
    private const string SkillScaffold = "researcher";

    public static async Task<McpResponse> RenderAsync(string outcomeTag)
    {
        B0FaultSeam.ClearFaults();
        switch (outcomeTag)
        {
            case "created":
                return await DriveSkillCreateAsync("sidecarcreated");
            case "success-with-warning":
                return await DriveSuccessWithWarningSkillCreateAsync();
            case "recovery-required":
                return await DriveRecoveryRequiredSkillCreateAsync();
            default:
                throw new ArgumentOutOfRangeException(
                    nameof(outcomeTag), outcomeTag, "Unknown sidecar envelope outcome tag.");
        }
    }

    private static async Task<McpResponse> DriveSkillCreateAsync(string name)
    {
        using var scope = new TestHelpers.StoreScope();
        return await ScriniaMcpTools.SkillCreate(name, SkillScaffold);
    }

    private static async Task<McpResponse> DriveSuccessWithWarningSkillCreateAsync()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        // Establish the sidecar so the rewrite's oldMeta.Existed is true.
        await ScriniaMcpTools.SkillCreate("sidecarwarning", SkillScaffold);
        // Post-commit recovery-evidence cleanup fails: the commit still lands, but the facade renders
        // it as success_with_warning. The tag must match the coordinator's point.ToString() seam.
        B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.FileEntityEvidenceCleanup, "EvidenceCleanup");
        return await ScriniaMcpTools.SkillCreate("sidecarwarning", SkillScaffold);
    }

    private static async Task<McpResponse> DriveRecoveryRequiredSkillCreateAsync()
    {
        using var scope = new TestHelpers.StoreScope();
        var tools = new ScriniaMcpTools();
        B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.FileEntityVerificationRead, "VerificationRead");
        B0FaultSeam.FailOnceAt(B0FaultSeam.FaultPhase.FileEntityRollbackWrite, "RollbackWrite");
        return await ScriniaMcpTools.SkillCreate("sidecarrecovery", SkillScaffold);
    }
}
