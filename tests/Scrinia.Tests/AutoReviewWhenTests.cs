using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// Tests for auto-reviewWhen detection in the Store method.
/// When content contains count patterns (e.g. "835 tests", "35 tools"),
/// Store should auto-set ReviewWhen unless an explicit value is provided.
/// </summary>
public sealed class AutoReviewWhenTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task Store_ContentWithCountPattern_AutoSetsReviewWhen()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["We have 835 tests and 35 tools"], "test-counts");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "test-counts").Which;
        entry.ReviewWhen.Should().Be("when counts in this memory change");
    }

    [Fact]
    public async Task Store_ContentWithCountPattern_ExplicitReviewWhenPreserved()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["We have 835 tests"], "test-counts-explicit",
            reviewWhen: "custom condition");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "test-counts-explicit").Which;
        entry.ReviewWhen.Should().Be("custom condition");
    }

    [Fact]
    public async Task Store_ContentWithoutCountPattern_ReviewWhenIsNull()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["This is a normal memory"], "test-normal");

        var entries = ScriniaArtifactStore.LoadIndex("local");
        var entry = entries.Should().ContainSingle(e => e.Name == "test-normal").Which;
        entry.ReviewWhen.Should().BeNull();
    }

    [Fact]
    public async Task Store_EphemeralWithCountPattern_DoesNotError()
    {
        using var scope = new TestHelpers.StoreScope();

        // Ephemeral memories take a different code path and don't have ReviewWhen.
        // This test verifies the ephemeral path doesn't error on content with count patterns.
        string result = await Tools().Store(["We have 835 tests"], "~scratch");

        result.Should().Contain("ephemeral");
    }
}
