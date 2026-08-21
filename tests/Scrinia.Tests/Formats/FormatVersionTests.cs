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
        // AbsentMeans is 1, which is < Current 3, so this must land on OnOlder — NOT on Load
        // by some separate "absent is fine" shortcut. A contract whose OnOlder is Rebuild
        // depends on this substitution happening before the comparison.
        FormatVersion.Evaluate(Sample, null).Should().Be(Sample.OnOlder);

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
        DurableFormats.All.Should().HaveCount(7);
    }

    [Theory]
    [InlineData("artifact-sidecar", 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    [InlineData("entity-facts", 3, FormatDisposition.Rebuild, FormatDisposition.Rebuild)]
    [InlineData("nmp2-body", 2, FormatDisposition.Refuse, FormatDisposition.Refuse)]
    [InlineData("bundle-manifest", 2, FormatDisposition.Load, FormatDisposition.Refuse)]
    [InlineData("legacy-index", 3, FormatDisposition.Load, FormatDisposition.Load)]
    [InlineData("skill-meta", 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    [InlineData("agent-meta", 1, FormatDisposition.Load, FormatDisposition.LoadReadOnly)]
    public void RegisteredContracts_MatchTheSpecTable(
        string name, int current, FormatDisposition onOlder, FormatDisposition onNewer)
    {
        var c = DurableFormats.All.Single(x => x.Name == name);
        c.Current.Should().Be(current);
        c.OnOlder.Should().Be(onOlder);
        c.OnNewer.Should().Be(onNewer);
    }

    [Fact]
    public void EntityFacts_AbsentMeansZero_SoAMissingKeyRebuilds() =>
        // EntityFactSidecar.Dto.Version is a non-nullable int: a missing "version" key
        // deserializes to 0, which must route through OnOlder to Rebuild. AbsentMeans = null
        // would return Refuse and change facts.json's contract.
        FormatVersion.Evaluate(DurableFormats.EntityFacts, 0)
            .Should().Be(FormatDisposition.Rebuild);
}
