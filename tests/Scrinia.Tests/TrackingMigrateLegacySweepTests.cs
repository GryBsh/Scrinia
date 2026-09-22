using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The `tracking migrate-legacy` bulk sweep reuses the same <c>local:agent</c> promotion primitive
/// the <c>/agent/</c> recall handler uses (Task 4); these lock in that the CLI reaches into the MCP
/// helper and that the sweep is fault-isolated per entry.
/// </summary>
public sealed class TrackingMigrateLegacySweepTests
{
    [Fact]
    public async Task Sweep_PromotesEveryLegacyLocalAgentEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.WriteArtifactAsync("ghost", "local:agent", Nmp2ChunkedEncoder.Encode("legacy agent body"), CancellationToken.None);
        await store.WriteArtifactAsync("profile", "local:agent", Nmp2ChunkedEncoder.Encode("second legacy body"), CancellationToken.None);

        await Scrinia.Commands.TrackingCommands.SweepLegacyAgentsAsync(store, CancellationToken.None);

        string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        File.Exists(Path.Combine(agentDir, "ghost.md")).Should().BeTrue();
        File.Exists(Path.Combine(agentDir, "profile.md")).Should().BeTrue();
        (await File.ReadAllTextAsync(Path.Combine(agentDir, "ghost.md"))).Should().Contain("legacy agent body");

        await FluentActions.Invoking(() =>
                store.ReadArtifactAsync(MemoryAddress.Parse("/agent/ghost"), CancellationToken.None))
            .Should().ThrowAsync<FileNotFoundException>(
                because: "the sweep deletes the legacy entry it just promoted, exactly like recall");
    }

    [Fact]
    public async Task Sweep_IsFaultIsolatedAgainstAMalformedEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.Store;

        await store.WriteArtifactAsync("broken", "local:agent", "not-a-valid-nmp2-artifact", CancellationToken.None);
        await store.WriteArtifactAsync("good", "local:agent", Nmp2ChunkedEncoder.Encode("valid legacy body"), CancellationToken.None);

        await Scrinia.Commands.TrackingCommands.SweepLegacyAgentsAsync(store, CancellationToken.None);

        string agentDir = PathRouter.ToFileEntityDir("agent", store.WorkspaceRoot);
        File.Exists(Path.Combine(agentDir, "good.md")).Should().BeTrue(
            because: "one malformed legacy entry must not abort the sweep");
        File.Exists(Path.Combine(agentDir, "broken.md")).Should().BeFalse(
            because: "the entry that failed to decode was never promoted");
    }
}
