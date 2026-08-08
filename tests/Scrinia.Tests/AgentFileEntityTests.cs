using FluentAssertions;
using Scrinia.Core;
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
            StoreLayoutMigration.Migrate(root, dryRun: false, backup: false);

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

        await Tools().MemoryRemember(path: "/agent/profile", content: ["you are helpful"]);
        string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        string sidecar = Path.Combine(agentDir, "profile.meta.json");
        File.Exists(sidecar).Should().BeTrue("precondition: the agent write produced a sidecar");

        // SaveIndex's orphan sweep deletes every *.meta.json it does not recognise in the
        // scope directory. Before this change agent configs lived there and were destroyed.
        store.SaveIndex(
            [new ArtifactEntry("unrelated", "file:///x.nmp2", 10, 1, DateTimeOffset.UtcNow)],
            "local:agent");

        File.Exists(sidecar).Should().BeTrue(
            because: "agent metadata lives outside every store scope directory");
    }
}
