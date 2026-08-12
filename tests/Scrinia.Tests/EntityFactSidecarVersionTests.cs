using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// <see cref="EntityFactSidecar"/> is a SECOND route into <see cref="EntityFactStore"/>, and it
/// bypasses derivation entirely: <see cref="EntityFactSidecar.TryLoad"/> reconstructs
/// <c>MemoryEntities</c>/<c>EntityRef</c> straight from <c>facts.json</c> without ever calling
/// <c>EntityGraph.ParseEntityAttributes</c>. So a filter added there governs what is DERIVED and
/// nothing else.
///
/// <para>That matters because the validity token is content-only — id, effective date and entity
/// signature — and says nothing about which RULES produced the facts. When state-marker attributes
/// stopped being entities, a sidecar written by the previous build still validated against a scope
/// whose entries had not changed, and handed <c>orphan:true</c> back as a graph node on the
/// unfiltered <c>SearchPipeline</c> path — the hub reinstated, persisting until something in that
/// scope mutated. Verified by execution before the bump: <c>entityToDocs.Keys</c> came back
/// carrying <c>orphan:true</c>.</para>
///
/// <para><b>The general shape is worth more than this instance: a cache that predates a rule change
/// is a path around the new rule.</b> These tests exist to make the version constant's contract
/// explicit, so the next derivation-rule change is met with a bump rather than a stale cache.</para>
/// </summary>
public sealed class EntityFactSidecarVersionTests
{
    /// <summary>A sidecar in the pre-fix format, carrying the state marker as a fact — exactly what
    /// the previous build wrote. The token is irrelevant here: version is checked first, and the
    /// whole point is that a matching token would otherwise let this through.</summary>
    private const string PreFixSidecarJson = """
        {
          "version": 2,
          "token": "anytokenatall",
          "memories": [
            {
              "s": "local",
              "j": "some-orphan",
              "date": "2026-01-01T00:00:00+00:00",
              "entities": [
                { "k": "orphan", "v": "true" },
                { "k": "topic", "v": "apricots" }
              ]
            }
          ]
        }
        """;

    [Fact]
    public void PreFixSidecar_IsRejected_RatherThanTrusted()
    {
        EntityFactSidecar.TryLoad(PreFixSidecarJson).Should().BeNull(
            because: "its facts were derived under a rule that has since changed, and nothing in " +
                     "the content-only validity token can notice that — so the version is the only " +
                     "thing standing between a rule change and a cache that predates it");
    }

    /// <summary>The bump is only meaningful if the current version round-trips, and only honest if
    /// the constant actually moved. Both are asserted so "reject everything" cannot pass for a fix.
    /// </summary>
    [Fact]
    public void CurrentVersion_RoundTripsAndIsPastThePreFixOne()
    {
        EntityFactSidecar.Version.Should().BeGreaterThan(2,
            because: "state-marker attributes stopped being entities, which is a derivation-rule " +
                     "change — the exact case this constant exists for");

        var memories = new[]
        {
            new MemoryEntities("local", "some-orphan", DateTimeOffset.UnixEpoch,
                [new EntityRef("topic", "apricots")]),
        };
        string json = EntityFactSidecar.Serialize("tok", memories);

        var loaded = EntityFactSidecar.TryLoad(json);
        loaded.Should().NotBeNull(
            because: "a sidecar this build wrote must load, or the cache is dead weight");
        loaded!.Value.Token.Should().Be("tok");
        loaded.Value.Memories.Should().ContainSingle()
            .Which.Entities.Should().ContainSingle()
            .Which.Should().BeEquivalentTo(new EntityRef("topic", "apricots"));
    }

    /// <summary>A future rule change gets a version it has not yet seen; that must be refused too,
    /// not read as "close enough". Pins the check as equality rather than a lower bound.</summary>
    [Fact]
    public void SidecarFromAFutureVersion_IsAlsoRejected()
    {
        string future = PreFixSidecarJson.Replace(
            "\"version\": 2", $"\"version\": {EntityFactSidecar.Version + 1}", StringComparison.Ordinal);

        EntityFactSidecar.TryLoad(future).Should().BeNull();
    }
}
