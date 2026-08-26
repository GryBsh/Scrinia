using FluentAssertions;
using Scrinia.Core.Formats;

namespace Scrinia.Tests.Formats;

public class FormatVersionTests
{
    private static readonly FormatContract Sample =
        new("sample", Current: 3, AbsentMeans: 1,
            OnOlder: FormatDisposition.Load, OnNewer: FormatDisposition.LoadReadOnly);

    [Fact]
    public void Equal_Loads() =>
        FormatVersion.Evaluate(Sample, 3).Should().Be(FormatDisposition.Load);

    [Fact]
    public void Older_TakesOnOlder() =>
        FormatVersion.Evaluate(Sample, 2).Should().Be(FormatDisposition.Load);

    [Fact]
    public void Newer_TakesOnNewer() =>
        FormatVersion.Evaluate(Sample, 4).Should().Be(FormatDisposition.LoadReadOnly);

    [Fact]
    public void Absent_SubstitutesAbsentMeans_ThenEvaluates() =>
        // OnOlder is deliberately Rebuild here and NOT Load. Sample.OnOlder is Load, so
        // asserting against Sample would pass under the very defect this test exists to catch:
        // `if (declared is null) return AbsentMeans is null ? Refuse : Load;` short-circuits to
        // Load and never substitutes. Only a contract whose OnOlder differs from Load can tell
        // the two implementations apart. AbsentMeans (1) is below Current (3), so a real
        // substitution must land on OnOlder.
        FormatVersion.Evaluate(Sample with { OnOlder = FormatDisposition.Rebuild }, null)
            .Should().Be(FormatDisposition.Rebuild);

    [Fact]
    public void Absent_WhenAbsentIsInvalid_Refuses()
    {
        var strict = Sample with { AbsentMeans = null };
        FormatVersion.Evaluate(strict, null).Should().Be(FormatDisposition.Refuse);
    }

    [Fact]
    public void EveryRegisteredContract_HasADistinctName()
    {
        var names = DurableFormats.All.Select(c => c.Name).ToList();
        names.Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Registry_CoversEveryDurableFormat()
    {
        // Guard, not trivia: adding a durable format without registering a contract must fail
        // a test rather than pass silently. Bump this deliberately when a format is added.
        DurableFormats.All.Should().HaveCount(9);
    }

    // AbsentMeans is pinned here too. It was the column an adversarial pass corrected THREE
    // times (legacy-index 1 -> 3, entity-facts null -> 0, and nmp2-body's deliberate null), and
    // leaving it unasserted meant every one of those could be silently reverted with a green
    // suite.
    [Theory]
    [InlineData("artifact-sidecar", 1, 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    // Same bytes as artifact-sidecar, deliberately different OnNewer -- transport, not storage.
    [InlineData("bundle-entry", 1, 1, FormatDisposition.Load, FormatDisposition.Refuse)]
    [InlineData("entity-facts", 3, 0, FormatDisposition.Rebuild, FormatDisposition.Rebuild)]
    [InlineData("nmp2-body", 2, null, FormatDisposition.Refuse, FormatDisposition.Refuse)]
    [InlineData("bundle-manifest", 2, 1, FormatDisposition.Load, FormatDisposition.Refuse)]
    [InlineData("legacy-index", 3, 3, FormatDisposition.Load, FormatDisposition.Load)]
    [InlineData("skill-meta", 1, 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    [InlineData("agent-meta", 1, 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    [InlineData("restore-journal", 1, null, FormatDisposition.Refuse, FormatDisposition.Refuse)]
    public void RegisteredContracts_MatchTheSpecTable(
        string name, int current, int? absentMeans,
        FormatDisposition onOlder, FormatDisposition onNewer)
    {
        var c = DurableFormats.All.Single(x => x.Name == name);
        c.Current.Should().Be(current);
        c.AbsentMeans.Should().Be(absentMeans);
        c.OnOlder.Should().Be(onOlder);
        c.OnNewer.Should().Be(onNewer);
    }

    /// <summary>
    /// A bundle entry IS an artifact sidecar; only OnNewer differs. Export writes the sidecar's
    /// own version verbatim, so if these two ever drift this build exports a version it then
    /// refuses to re-import — it would reject its own bundles. Asserted rather than trusted,
    /// because the coupling is invisible at the point where someone would bump the sidecar.
    /// </summary>
    [Fact]
    public void BundleEntry_TracksTheArtifactSidecarsVersion() =>
        DurableFormats.BundleEntry.Current.Should().Be(
            DurableFormats.ArtifactSidecar.Current,
            "a bundle entry is a sidecar in transit; only its OnNewer policy differs");

    [Fact]
    public void EntityFacts_AbsentMeansZero_SoAMissingKeyRebuilds() =>
        // EntityFactSidecar.Dto.Version is a non-nullable int: a missing "version" key
        // deserializes to 0, which must route through OnOlder to Rebuild. AbsentMeans = null
        // would return Refuse and change facts.json's contract.
        FormatVersion.Evaluate(DurableFormats.EntityFacts, 0)
            .Should().Be(FormatDisposition.Rebuild);

    [Fact]
    public void RestoreJournal_RefusesAbsentOlderAndNewerVersions()
    {
        FormatVersion.Evaluate(DurableFormats.RestoreJournal, null)
            .Should().Be(FormatDisposition.Refuse);
        FormatVersion.Evaluate(DurableFormats.RestoreJournal, 0)
            .Should().Be(FormatDisposition.Refuse);
        FormatVersion.Evaluate(DurableFormats.RestoreJournal, 2)
            .Should().Be(FormatDisposition.Refuse);
    }
}
