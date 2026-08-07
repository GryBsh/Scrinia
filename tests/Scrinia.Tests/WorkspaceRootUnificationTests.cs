using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// IMemoryStore.WorkspaceRoot is the single authority for "the directory containing .scrinia/".
/// Callers previously reverse-engineered it from GetStoreDirForScope with two different
/// hand-rolled algorithms that agreed only by coincidence. This pins the property against the
/// derivation it replaces, so a future layout change cannot silently desynchronise them.
/// </summary>
public class WorkspaceRootUnificationTests
{
    [Fact]
    public void WorkspaceRoot_MatchesTwoHopDerivationFromLocalStoreDir()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string storeDir = store.GetStoreDirForScope("local");
        string expected = Path.GetDirectoryName(Path.GetDirectoryName(storeDir)!)!;

        store.WorkspaceRoot.Should().Be(expected,
            "WorkspaceRoot must equal the derivation the seven call sites used to hand-roll");
    }

    [Fact]
    public void ScriniaDir_IsOneHopFromLocalStoreDir()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = MemoryStoreContext.Current!;

        string storeDir = store.GetStoreDirForScope("local");
        string scriniaDir = Path.GetDirectoryName(storeDir)!;

        Path.Combine(store.WorkspaceRoot, ".scrinia").Should().Be(scriniaDir,
            "the two Lifecycle sites want .scrinia/, which is one hop, not the workspace root");
    }
}
