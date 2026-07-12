using System.Globalization;
using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

public sealed class AttributeReadersTests
{
    private static readonly DateTimeOffset Now = new(2026, 07, 12, 0, 0, 0, TimeSpan.Zero);
    private static Dictionary<string, string[]> F(params (string k, string[] v)[] kv)
        => kv.ToDictionary(x => x.k, x => x.v, StringComparer.OrdinalIgnoreCase);

    // ---- GetReviewStatus: sources Fields["review"] ----
    [Fact]
    public void ReviewStatus_stale_from_fields_past_date()
    {
        var past = Now.AddDays(-1).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { past })), null, null, Now);
        s.IsStale.Should().BeTrue();
        s.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public void ReviewStatus_needsReview_from_fields_condition_only()
    {
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { "after refactor X" })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public void ReviewStatus_future_date_with_condition_is_needsReview_not_stale()
    {
        var future = Now.AddDays(30).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { future, "when ready" })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue();
    }

    [Fact]
    public void ReviewStatus_future_date_no_condition_shows_nothing()
    {
        // Mirrors legacy: future ReviewAfter with no ReviewWhen renders no marker.
        var future = Now.AddDays(30).ToString("o", CultureInfo.InvariantCulture);
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { future })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeFalse();
    }

    [Fact]
    public void ReviewStatus_falls_back_to_legacy_when_no_review_key()
    {
        // Ephemeral entry never forward-filled: Fields absent → legacy fields drive.
        var s = AttributeReaders.GetReviewStatus(null, Now.AddDays(-1), null, Now);
        s.IsStale.Should().BeTrue();
        var s2 = AttributeReaders.GetReviewStatus(F(("tag", new[] { "x" })), null, "later", Now);
        s2.NeedsReview.Should().BeTrue(); // fields present but no "review" key → legacy fallback
    }

    [Fact]
    public void ReviewStatus_condition_that_looks_numeric_is_not_a_date()
    {
        var s = AttributeReaders.GetReviewStatus(F(("review", new[] { "2026" })), null, null, Now);
        s.IsStale.Should().BeFalse();
        s.NeedsReview.Should().BeTrue(); // "2026" is not round-trip("o") format → treated as condition
    }

    [Fact]
    public void ReviewStatus_none_when_no_data()
        => AttributeReaders.GetReviewStatus(null, null, null, Now).Should().Be(new AttributeReaders.ReviewStatus(false, false));

    // ---- PreviewText ----
    [Fact]
    public void PreviewText_prefers_contentPreview()
        => AttributeReaders.PreviewText("preview", "desc").Should().Be("preview");

    [Fact]
    public void PreviewText_falls_back_to_description_when_null()
        => AttributeReaders.PreviewText(null, "desc").Should().Be("desc");

    // ---- DisplayTags ----
    [Fact]
    public void DisplayTags_prefers_fields_tag()
        => AttributeReaders.DisplayTags(F(("tag", new[] { "a", "b" })), new[] { "legacy" })
            .Should().Equal("a", "b");

    [Fact]
    public void DisplayTags_falls_back_to_legacy_when_no_tag_key()
        => AttributeReaders.DisplayTags(F(("review", new[] { "x" })), new[] { "legacy" })
            .Should().Equal("legacy");

    [Fact]
    public void DisplayTags_never_null()
        => AttributeReaders.DisplayTags(null, null).Should().BeEmpty();

    // ---- DisplayAnchors (mirrors DisplayTags with KnownFields.AnchorKey / "anchor") ----
    [Fact]
    public void DisplayAnchors_prefers_fields_anchor()
        => AttributeReaders.DisplayAnchors(F((KnownFields.AnchorKey, new[] { "a", "b" })), new[] { "legacy" })
            .Should().Equal("a", "b");

    [Fact]
    public void DisplayAnchors_falls_back_to_legacy_when_no_anchor_key()
        => AttributeReaders.DisplayAnchors(F(("review", new[] { "x" })), new[] { "legacy" })
            .Should().Equal("legacy");

    [Fact]
    public void DisplayAnchors_never_null()
        => AttributeReaders.DisplayAnchors(null, null).Should().BeEmpty();
}
