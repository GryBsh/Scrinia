using System.Globalization;
using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

public sealed class AttributeReadersTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 12, 0, 0, 0, TimeSpan.Zero);
    private static Dictionary<string, string[]> F(params (string k, string[] v)[] kv)
        => kv.ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);

    // ---- TryReadReview ----
    [Fact]
    public void TryReadReview_extracts_date_and_condition()
    {
        var date = Now.AddDays(-1).ToString("o", CultureInfo.InvariantCulture);
        var found = AttributeReaders.TryReadReview(
            F(("review", new[] { date, "some condition" })), out var parsedDate, out var condition);
        found.Should().BeTrue();
        parsedDate.Should().Be(DateTimeOffset.Parse(date, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        condition.Should().Be("some condition");
    }

    [Fact]
    public void TryReadReview_returns_false_when_missing()
    {
        var found = AttributeReaders.TryReadReview(null, out var date, out var condition);
        found.Should().BeFalse();
        date.Should().BeNull();
        condition.Should().BeNull();

        var found2 = AttributeReaders.TryReadReview(F(("tag", new[] { "x" })), out var date2, out var condition2);
        found2.Should().BeFalse();
        date2.Should().BeNull();
        condition2.Should().BeNull();
    }

    // ---- GetReviewStatus: sources Attributes["review"] ----
    [Fact]
    public void ReviewStatus_stale_from_fields_past_date()
    {
        var past = Now.AddDays(-1).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { past })), Now);
        s.IsStale.Should().BeTrue();
        s.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public void ReviewStatus_needsReview_from_fields_condition_only()
    {
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { "after refactor X" })), Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public void ReviewStatus_future_date_with_condition_is_needsReview_not_stale()
    {
        var future = Now.AddDays(30).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { future, "when ready" })), Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public void ReviewStatus_future_date_no_condition_shows_nothing()
    {
        // Mirrors legacy: future ReviewAfter with no ReviewWhen renders no marker.
        var future = Now.AddDays(30).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { future })), Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public void ReviewStatus_condition_that_looks_numeric_is_not_a_date()
    {
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { "2026" })), Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue(); // "2026" is not round-trip("o") format → treated as condition
    }

    [Fact]
    public void ReviewStatus_none_when_no_data()
        => AttributeReaders.GetReviewStatus(null, Now).Should().Be(new AttributeReaders.ReviewStatus(false, false));

    [Fact]
    public void GetReviewStatus_EmptyReviewArray_NotStale_NotNeedsReview()
    {
        // Empty-but-present "review" key yields no date/condition -> not stale, not needs-review.
        var fields = new Dictionary<string, string[]> { ["review"] = Array.Empty<string>() };
        var s = AttributeReaders.GetReviewStatus(fields, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeFalse();
    }

    // ---- PreviewText ----
    [Fact]
    public void PreviewText_prefers_contentPreview()
        => AttributeReaders.PreviewText("preview").Should().Be("preview");

    [Fact]
    public void PreviewText_falls_back_to_empty_when_null()
        => AttributeReaders.PreviewText(null).Should().Be(string.Empty);

    // ---- DisplayTags ----
    [Fact]
    public void DisplayTags_reads_fields_tag()
        => AttributeReaders.DisplayTags(F(("tag", new[] { "a", "b" })))
            .Should().Equal("a", "b");

    [Fact]
    public void DisplayTags_empty_when_no_tag_key()
        => AttributeReaders.DisplayTags(F(("review", new[] { "x" })))
            .Should().BeEmpty();

    [Fact]
    public void DisplayTags_never_null()
        => AttributeReaders.DisplayTags(null).Should().BeEmpty();

    // ---- DisplayAnchors (mirrors DisplayTags with KnownAttributes.AnchorKey / "anchor") ----
    [Fact]
    public void DisplayAnchors_reads_fields_anchor()
        => AttributeReaders.DisplayAnchors(F((KnownAttributes.AnchorKey, new[] { "a", "b" })))
            .Should().Equal("a", "b");

    [Fact]
    public void DisplayAnchors_empty_when_no_anchor_key()
        => AttributeReaders.DisplayAnchors(F(("review", new[] { "x" })))
            .Should().BeEmpty();

    [Fact]
    public void DisplayAnchors_never_null()
        => AttributeReaders.DisplayAnchors(null).Should().BeEmpty();
}
