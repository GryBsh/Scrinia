using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Wires <see cref="Scrinia.Core.Search.AttributeExtractor.ExtractMentionSpans"/> into the
/// MCP write path (<c>remember</c>) and verifies typed agent attributes are authoritative —
/// an entity the agent explicitly typed is not ALSO left as an untyped <c>mention:</c> value.
/// </summary>
public class MentionSpanWiringTests
{
    private static ScriniaMcpTools Tools() => new();

    private static string Dump(IReadOnlyDictionary<string, string[]>? attrs) =>
        attrs is null
            ? "(none)"
            : string.Join("; ", attrs.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                .Select(kv => $"{kv.Key}=[{string.Join(",", kv.Value)}]"));

    [Fact]
    public async Task Store_ExtractsMentionSpans_AgentAttributeOverrides()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().MemoryRemember(path: "/findings/trip",
            content: ["We saw Jolene and Seraphim in France."]);

        var entry = MemoryStoreContext.Current!.LoadIndex("local:findings").Single(e => e.Name == "trip");
        // Dump the whole attribute set on failure. This assertion was seen to fail once with
        // mention={seraphim}, which the write path alone does not explain: with no agent
        // attributes and no `key: value` prose, DropMentionsShadowedByTypedKeys has nothing to
        // shadow jolene with. Knowing which typed key captured it identifies the cause.
        string dump = Dump(entry.Attributes);
        entry.Attributes!["mention"].Should().Contain("jolene", "attributes were {0}", dump);
        entry.Attributes!["mention"].Should().Contain("seraphim", "attributes were {0}", dump);

        // Agent-supplied typed attribute wins: a value the agent typed is NOT duplicated under mention.
        await Tools().MemoryRemember(path: "/findings/typed",
            content: ["We saw Jolene."],
            attributes: new Dictionary<string, string[]> { ["person"] = ["jolene"] });

        var typed = MemoryStoreContext.Current!.LoadIndex("local:findings").Single(e => e.Name == "typed");
        typed.Attributes!["person"].Should().Contain("jolene");
        // mention may still contain other spans, but must not shadow the agent's person:jolene as a conflict.
        typed.Attributes.TryGetValue("mention", out var m);
        (m ?? []).Should().NotContain("jolene", "agent-typed value must not also appear as an untyped mention");
    }
}
