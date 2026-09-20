using Scrinia.Core;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

public class StoreFactoryTests
{
    [Fact]
    public void Create_WithoutParticipantFactory_BuildsStandardFileStore()
    {
        using var scope = new TestHelpers.StoreScope();

        var rankerOptions = RankerOptions.Default;

        var store = StoreFactory.Create(scope.WorkspaceDir, rankerOptions);

        Assert.NotNull(store);
        Assert.IsType<FileMemoryStore>(store);

        // Mirrors WorkspaceSetup.Configure wiring: the factory's store becomes the
        // ambient (AsyncLocal) store. Current is test-scoped, unlike Default which is
        // process-wide, so we only assert against it here.
        MemoryStoreContext.Current = store;
        Assert.Same(store, MemoryStoreContext.Current);

        // Sanity: the store is actually usable.
        var entries = store.ListScoped([]);
        Assert.NotNull(entries);
    }

    [Fact]
    public void Create_WithParticipantFactory_BuildsParticipantFileStore()
    {
        using var scope = new TestHelpers.StoreScope();

        var rankerOptions = RankerOptions.Default;
        IMultiMemoryParticipantAdapterFactory? participantFactory = null; // null adapter still constructs

        var store = StoreFactory.Create(scope.WorkspaceDir, rankerOptions, participantFactory);

        Assert.NotNull(store);
        Assert.IsType<FileMemoryStore>(store);

        MemoryStoreContext.Current = store;
        Assert.Same(store, MemoryStoreContext.Current);
    }
}
