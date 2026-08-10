using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Mcp;

namespace Scrinia.Tests;

/// <summary>
/// remember() must hand the agent's attributes to the write seam, not assemble them afterwards.
/// Before the reorder they were built after WriteArtifactAsync and delivered via Upsert — which
/// works locally and silently discarded everything against a remote store.
/// </summary>
public class RememberWriteMetadataTests
{
    /// <summary>
    /// Records what WriteArtifactAsync was handed. Subclasses the existing FakeMemoryStore
    /// (tests/Scrinia.Tests/FakeMemoryStore.cs) rather than writing a fresh 23-member delegator:
    /// it is internal to this same assembly and every member is virtual.
    /// </summary>
    private sealed class RecordingStore : FakeMemoryStore
    {
        public ArtifactWriteMetadata? LastMeta { get; private set; }
        public bool WriteHappened { get; private set; }

        public override Task WriteArtifactAsync(string subject, string scope, string artifactText,
            ArtifactWriteMetadata? meta = null, CancellationToken ct = default)
        {
            LastMeta = meta;
            WriteHappened = true;
            return base.WriteArtifactAsync(subject, scope, artifactText, meta, ct);
        }
    }

    [Fact]
    public async Task Remember_HandsTheAttributesMapToTheWriteSeam()
    {
        using var scope = new TestHelpers.StoreScope();
        var recording = new RecordingStore();
        MemoryStoreContext.Current = recording;

        await new ScriniaMcpTools().MemoryRemember(
            path: "/findings/note",
            content: ["a body"],
            attributes: new Dictionary<string, string[]> { ["person"] = ["maria"] });

        recording.WriteHappened.Should().BeTrue("precondition: the write ran");
        recording.LastMeta.Should().NotBeNull(
            because: "the metadata must cross the seam, not arrive later via Upsert");
        recording.LastMeta!.Attributes.Should().ContainKey("person");
        recording.LastMeta.Attributes!["person"].Should().Contain("maria");
    }
}
