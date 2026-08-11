using System.IO.Compression;
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Models;
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
        var store = TestHelpers.LocalStore;

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"]);

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

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"]);
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

        // Agent config lives outside the store, at .scrinia/agent/, with a *.meta.json sidecar.
        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"]);
        string sourceAgentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        File.Exists(Path.Combine(sourceAgentDir, "profile.meta.json")).Should().BeTrue(
            "precondition: the agent write produced a sidecar");

        // Seed a real NMP/2 topic literally named "agent" too — the collision case this test
        // guards against. BuildScopedTopicScope("agent") resolves to scope "local:agent", a
        // store-side directory (.scrinia/memories/agent/) distinct from the top-level
        // file-entity agent dir (.scrinia/agent/) only by construction, not by any runtime
        // check — see PathRouter's FileEntityDirs doc comment.
        string topicScope = MemoryNaming.BuildScopedTopicScope(store.SanitizeName("agent"));
        store.Upsert(
            new ArtifactEntry("note", "", 5, 1, DateTimeOffset.UtcNow, "an agent-topic note"),
            topicScope);
        await store.WriteArtifactAsync("note", topicScope, "agent topic content");

        // Act — export both the "agent" topic and the agent file entity, then import the
        // bundle into a fresh workspace through the same BundleFormatService path
        // `scri bundle import` uses (BundleCommands.Import -> ScriniaCommands.Import ->
        // BundleFormatService.ImportTopicsFromZip).
        byte[] zipBytes;
        using (var ms = new MemoryStream())
        {
            using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
                BundleFormatService.ExportTopicsToZip(zip, store, ["agent"]);
            zipBytes = ms.ToArray();
        }

        string importRoot = Path.Combine(Path.GetTempPath(), $"scrinia_bundleagent_{Guid.NewGuid():N}");
        Directory.CreateDirectory(importRoot);
        try
        {
            using var importStore = new FileMemoryStore(importRoot);
            using var importMs = new MemoryStream(zipBytes);
            using var importZip = new ZipArchive(importMs, ZipArchiveMode.Read);
            BundleFormatService.ImportTopicsFromZip(importZip, importStore, filterTopics: null, overwrite: true);

            // Assert — the agent config sidecar was restored at the top-level file-entity
            // dir, never swept by SaveIndex's orphan sweep against the "agent" topic's own
            // scope directory.
            string importAgentDir = PathRouter.ToFileEntityDir("agent", importStore.WorkspaceRoot);
            File.Exists(Path.Combine(importAgentDir, "profile.meta.json")).Should().BeTrue(
                because: "agent metadata lives outside every store scope directory");
        }
        finally
        {
            try { Directory.Delete(importRoot, recursive: true); } catch { }
        }
    }
}
