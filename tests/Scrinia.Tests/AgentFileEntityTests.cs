using System.IO.Compression;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
using Scrinia.Core.Encoding;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Agent configs live at .scrinia/agent/, outside the memory store.
///
/// <para>The store-unification work moved them into .scrinia/memories/agent/, which put
/// AgentFileMeta sidecars in a directory FileMemoryStore enumerates. Because
/// WriteSidecarMeta names "profile.md"'s sidecar "profile.meta.json" — the same filename
/// the store writes for an NMP/2 entry named "profile" — SaveIndex's orphan sweep deleted
/// them. Moving agent back out makes that impossible rather than merely unlikely.</para>
/// </summary>
public class AgentFileEntityTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task AgentWrite_LandsAtTopLevelAgentDir_NotInTheStore()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"], description: "you are helpful");

        string expected = Path.Combine(
            PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot), "profile.md");
        File.Exists(expected).Should().BeTrue();

        Directory.Exists(Path.Combine(store.GetStoreDirForScope("local"), "agent"))
            .Should().BeFalse(because: "the store holds NMP/2 content only");
    }

    [Fact]
    public async Task AgentRecall_ReadsFromTopLevelAgentDir()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"], description: "you are helpful");
        string result = await Tools().MemoryRecall(path: "/agent/profile");

        result.Should().Contain("you are helpful");
    }

    [Fact]
    public void Migration_DoesNotSweepTheAgentDirectory()
    {
        string root = Path.Combine(Path.GetTempPath(), $"scrinia_agentsweep_{Guid.NewGuid():N}");
        string agentDir = Path.Combine(root, ".scrinia", "agent");
        Directory.CreateDirectory(agentDir);
        File.WriteAllText(Path.Combine(agentDir, "profile.md"), "you are helpful");
        // A legacy root so the migration actually has work to do and runs to completion.
        string topics = Path.Combine(root, ".scrinia", "topics", "findings");
        Directory.CreateDirectory(topics);
        File.WriteAllText(Path.Combine(topics, "note.nmp2"), "NMP/2\n\nbody");

        try
        {
            StoreLayoutMigration.Result result =
                StoreLayoutMigration.Migrate(root, dryRun: false, backup: false);

            result.Ran.Should().BeTrue(
                because: "if migration short-circuits, the assertions below pass vacuously");

            File.Exists(Path.Combine(agentDir, "profile.md")).Should().BeTrue(
                because: "agent is a hand-authored file entity and is never swept");
            Directory.Exists(Path.Combine(root, ".scrinia", "memories", "agent"))
                .Should().BeFalse();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task BundleImportOfAnAgentTopic_DoesNotDeleteAgentConfigMetadata()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)MemoryStoreContext.Current!;

        // Seed a real NMP/2 topic literally named "agent" on the SOURCE — the collision case
        // this test guards against. BuildScopedTopicScope("agent") resolves to scope
        // "local:agent", a store-side directory (.scrinia/memories/agent/) distinct from the
        // top-level file-entity agent dir (.scrinia/agent/) only by construction, not by any
        // runtime check — see PathRouter's FileEntityDirs doc comment. Deliberately no agent
        // file entity on the SOURCE: if the exported bundle carried one, ImportFileEntities
        // (which runs unconditionally AFTER the topics loop below) would rewrite the sidecar
        // moments after any sweep deleted it, masking exactly the failure this test exists to
        // catch.
        // Task 13: the one scope constructor sanitizes each segment itself. Wrapping it in
        // SanitizeName flattens '/' to '_' and so names a DIFFERENT scope for any nested topic —
        // it is a no-op only for a single-segment alphanumeric like this one, which is what kept
        // the pairing invisible.
        string topicScope = MemoryNaming.BuildScopedTopicScope("agent");
        store.Upsert(
            new ArtifactEntry("note", "", 5, 1, DateTimeOffset.UtcNow, "an agent-topic note"),
            topicScope);
         await store.WriteArtifactAsync("note", topicScope, Nmp2ChunkedEncoder.Encode("agent topic content"));

        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                BundleFormatService.ExportTopicsToZip(zip, store, ["agent"]);
            zipBytes = ms.ToArray();
        }

        // Act — import into a workspace that ALREADY has an agent config sidecar, the realistic
        // case: `scri bundle import` into an existing workspace, not a fresh one. Seeded BEFORE
        // ImportTopicsFromZip runs so the pre-existing sidecar is on disk when the topics loop's
        // SaveIndex orphan sweep executes — that ordering is what lets the sweep actually find
        // it, which importing into an empty workspace could not (Task 1 review, Critical 2).
        string importRoot = Path.Combine(Path.GetTempPath(), $"scrinia_bundleagent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(importRoot);
        try
        {
            using var importStore = new FileMemoryStore(importRoot);

            IMemoryStore? previous = MemoryStoreContext.Current;
            MemoryStoreContext.Current = importStore;
            try
            {
                await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"], description: "you are helpful");
            }
            finally
            {
                MemoryStoreContext.Current = previous;
            }

            string importAgentDir = PathRouter.ToFileEntityDir("agent", importStore.WorkspaceRoot);
            string importSidecar = Path.Combine(importAgentDir, "profile.meta.json");
            File.Exists(importSidecar).Should().BeTrue(
                "precondition: the destination workspace already has agent config metadata");

            using var importMs = new MemoryStream(zipBytes);
            using var importZip = new ZipArchive(importMs, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(importZip, importStore, filterTopics: null, overwrite: true);

            // Assert — the pre-existing agent config sidecar survived, never swept by
            // SaveIndex's orphan sweep against the "agent" topic's own scope directory.
            File.Exists(importSidecar).Should().BeTrue(
                because: "agent metadata lives outside every store scope directory, so importing " +
                         "a bundle containing a topic literally named \"agent\" must not sweep it away");
        }
        finally
        {
            try { Directory.Delete(importRoot, recursive: true); } catch { }
        }
    }
}
