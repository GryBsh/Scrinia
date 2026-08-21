using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests.Store;

/// <summary>
/// Durability of bundle restore — finding C1 of the 2026-08-21 design review.
///
/// <para>The defect: <c>ImportTopicEntries</c> snapshotted the index under a shared lock that
/// released immediately, wrote every body unlocked, then committed through <c>SaveIndex</c>,
/// whose orphan sweep deletes every sidecar absent from a list that was by then seconds old.
/// A memory written during a restore was annihilated after its write reported success.</para>
///
/// <para>The fix is not a wider lock around the sweep. Import is purely additive and never
/// needed one, so the sweep left the import path entirely.</para>
/// </summary>
public sealed class TransactionalRestoreTests
{
    private const string Scope = "local:notes";

    [Fact]
    public async Task SaveIndex_StillSweepsSidecarsAbsentFromTheListItIsHanded()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = (FileMemoryStore)TestHelpers.Store;

        await store.StoreAsync(new MemoryWrite("keeper", Scope, ["kept"]));
        await store.StoreAsync(new MemoryWrite("victim", Scope, ["swept"]));

        var withoutVictim = store.LoadIndex(Scope)
            .Where(e => !e.Name.Equals("victim", StringComparison.OrdinalIgnoreCase))
            .ToList();

        store.SaveIndex(withoutVictim, Scope);

        store.LoadIndex(Scope).Select(e => e.Name).Should()
            .Contain("keeper").And.NotContain("victim",
                because: "SaveIndex's orphan sweep is unchanged by this work — only import "
                       + "stops using it");
    }
}
