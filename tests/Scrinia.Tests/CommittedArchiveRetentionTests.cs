using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed class CommittedArchiveRetentionTests
{
    [Theory]
    [InlineData("append")]
    [InlineData("replace-archive")]
    [InlineData("replace-evidence")]
    public async Task NextPublicWrite_PreservesCommittedArchiveAndPrunesOnlyStaleVersions(string operation)
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        var target = MemoryAddress.Parse("/target");
        for (int version = 0; version < 13; version++)
        {
            var write = MemoryWrite.Create(target, "local", [$"old-{version}"]);
            if (operation == "append")
                await store.AppendAsync(write);
            else
                await store.StoreAsync(write);
        }
        byte[] oldArtifact = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] oldSidecar = File.ReadAllBytes(store.MetaPath("target"));

        await CommitWithCleanupFault(store, operation);

        string storeDirectory = store.GetStoreDirForScope("local");
        string versions = Path.Combine(storeDirectory, "versions");
        string evidencePath = Path.Combine(versions, AppendJournal.FileName);
        JsonNode body = JsonNode.Parse(File.ReadAllText(evidencePath))!["body"]!;
        string archiveArtifact = Resolve(body["archiveArtifactPath"]!.GetValue<string>());
        string archiveSidecar = Resolve(body["archiveSidecarPath"]!.GetValue<string>());
        string[] stalePaths = body["pruneCandidates"]!.AsArray()
            .Select(candidate => Resolve(candidate!.GetValue<string>())).ToArray();
        stalePaths.Should().NotBeEmpty();
        byte[] liveArtifact = File.ReadAllBytes(store.ArtifactPath("target"));
        byte[] liveSidecar = File.ReadAllBytes(store.MetaPath("target"));
        File.ReadAllBytes(archiveArtifact).Should().Equal(oldArtifact);
        File.ReadAllBytes(archiveSidecar).Should().Equal(oldSidecar);

        // An unrelated write exercises workspace admission's committed-cleanup adapter.
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/other"), "local", ["other"]));

        File.Exists(archiveArtifact).Should().BeTrue("the previous version remains a retained archive");
        File.Exists(archiveSidecar).Should().BeTrue("the retained archive needs its original sidecar");
        File.ReadAllBytes(archiveArtifact).Should().Equal(oldArtifact);
        File.ReadAllBytes(archiveSidecar).Should().Equal(oldSidecar);
        File.ReadAllBytes(store.ArtifactPath("target")).Should().Equal(liveArtifact);
        File.ReadAllBytes(store.MetaPath("target")).Should().Equal(liveSidecar);
        stalePaths.Should().OnlyContain(path => !File.Exists(path));
        Directory.GetFiles(versions, "target_*.nmp2").Should().HaveCount(operation == "append" ? 11 : 10);
        File.Exists(evidencePath).Should().BeFalse();

        string Resolve(string path) => Path.IsPathRooted(path) ? path : Path.Combine(storeDirectory, path);
    }

    [Theory]
    [InlineData("archiveArtifactPath")]
    [InlineData("archiveSidecarPath")]
    public async Task NextPublicWrite_RefusesPruningTheCurrentArchiveAndRetainsEvidence(string archiveMember)
    {
        using var workspace = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["old"]));
        await CommitWithCleanupFault(store, "append");
        string storeDirectory = store.GetStoreDirForScope("local");
        string evidencePath = Path.Combine(storeDirectory, "versions", AppendJournal.FileName);
        JsonNode evidence = JsonNode.Parse(File.ReadAllText(evidencePath))!;
        string currentPath = evidence["body"]![archiveMember]!.GetValue<string>();
        string absolutePath = Path.IsPathRooted(currentPath) ? currentPath : Path.Combine(storeDirectory, currentPath);
        byte[] archivedBytes = File.ReadAllBytes(absolutePath);
        evidence["body"]!["pruneCandidates"] = new JsonArray(currentPath);
        File.WriteAllText(evidencePath, evidence.ToJsonString());

        await FluentActions.Invoking(() => store.StoreAsync(
                MemoryWrite.Create(MemoryAddress.Parse("/other"), "local", ["other"])))
            .Should().ThrowAsync<AppendStoreException>();

        File.ReadAllBytes(absolutePath).Should().Equal(archivedBytes);
        File.Exists(evidencePath).Should().BeTrue("unsafe pruning must remain recoverable");
        File.Exists(store.ArtifactPath("other")).Should().BeFalse();
    }

    private static async Task CommitWithCleanupFault(FileMemoryStore store, string operation)
    {
        var write = MemoryWrite.Create(MemoryAddress.Parse("/target"), "local", ["new"]);
        if (operation == "append")
        {
            store.FailOnceAt(AppendFaultPoint.ArchiveCleanup);
            (await store.AppendAsync(write)).Outcome.Should().Be(AppendStoreOutcome.SuccessWithWarning);
        }
        else
        {
            store.FailOnceAt(operation == "replace-archive"
                ? WholeMemoryFaultPoint.ArchiveCleanup : WholeMemoryFaultPoint.EvidenceCleanup);
            (await store.StoreWithTransactionResultAsync(write)).Transaction!.Outcome
                .Should().Be(WholeMemoryOutcome.SuccessWithWarning);
        }
        store.ClearFaults();
    }
}
