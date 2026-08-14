using System.Text.Json;
using FluentAssertions;
using Scrinia.Server.Models;

namespace Scrinia.Server.Tests;

/// <summary>
/// The request shape carries no field the server derives or recomputes. Asserted as a property of
/// the TYPE, not of one instance: a later edit that adds <c>ChunkCount</c> or <c>Name</c> back —
/// the shape <c>ShowResponse</c> exists to prevent — fails here rather than in review.
/// </summary>
public sealed class CopyFromClientContractTests
{
    [Theory]
    [InlineData("Name")]
    [InlineData("QualifiedName")]
    [InlineData("Scope")]
    [InlineData("ChunkCount")]
    [InlineData("CreatedAt")]
    public void CarriedEntry_DoesNotCarryADerivableOrDestinationOwnedField(string forbidden)
    {
        typeof(CarriedEntry).GetProperty(forbidden).Should().BeNull(
            $"'{forbidden}' is computed from the destination or the artifact; carrying it would let "
            + "the request disagree with itself");
    }

    /// <summary>Round-trips through the SERVER's source-generated context — the one
    /// <c>Program.cs:311</c> installs — so an unregistered type fails here rather than at runtime
    /// under trimming.</summary>
    [Fact]
    public void CopyFromClientRequest_RoundTripsThroughServerJsonContext()
    {
        var original = new CopyFromClientRequest(
            Content: ["chunk one", "chunk two"],
            Entry: new CarriedEntry(
                OriginalBytes: 17,
                UpdatedAt: new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero),
                Description: "a preview",
                Attributes: new Dictionary<string, string[]> { ["file"] = ["src/A.cs"] },
                Keywords: ["kw"],
                TermFrequencies: new Dictionary<string, int> { ["kw"] = 2 },
                ChunkEntries: [new ChunkEntryDto(0, "preview", ["kw"], null)],
                FileHashes: new Dictionary<string, string> { ["src/A.cs"] = "deadbeef" },
                AgentKeywords: ["kw"]),
            Overwrite: true);

        string json = JsonSerializer.Serialize(original, ServerJsonContext.Default.CopyFromClientRequest);
        var back = JsonSerializer.Deserialize(json, ServerJsonContext.Default.CopyFromClientRequest);

        back.Should().BeEquivalentTo(original);
    }

    /// <summary>Overwrite defaults to false: a body that omits it must not silently replace an
    /// existing memory.</summary>
    [Fact]
    public void CopyFromClientRequest_DefaultsOverwriteToFalse()
    {
        var back = JsonSerializer.Deserialize(
            """{"content":["c"],"entry":{"originalBytes":1}}""",
            ServerJsonContext.Default.CopyFromClientRequest);

        back!.Overwrite.Should().BeFalse();
    }
}
