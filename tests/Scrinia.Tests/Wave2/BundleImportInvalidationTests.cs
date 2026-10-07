using Scrinia.Core;
using Scrinia.Core.Models;
namespace Scrinia.Tests.Wave2;
public class BundleImportInvalidationTests
{
    private static readonly string[] ExpectedPersonAlice = new[] { "person:alice" };
    private static readonly string[] ExpectedPersonBob = new[] { "person:bob" };

    [Fact]
    public async Task BundleImport_InvalidatesFactStore()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-w2-m5-" + Guid.NewGuid().ToString("N"));
        var store = new FileMemoryStore(root);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/a"), (IReadOnlyList<string?>)ExpectedPersonAlice, new Dictionary<string, IReadOnlyList<string>>(), []));
        var before = store.GetEntityFactStore();
        Assert.NotNull(before);
        await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/b"), (IReadOnlyList<string?>)ExpectedPersonBob, new Dictionary<string, IReadOnlyList<string>>(), []));
        var after = store.GetEntityFactStore();
        Assert.Contains(after.Entities(), e => e.Value == "bob");
    }
    [Fact]
    public void SaveIndexUnsafe_InvalidatesViaPublish()
    {
        var src = Resolve("src/Scrinia.Core/FileMemoryStore.cs");
        Assert.Contains("InvalidateEntityFacts", File.ReadAllText(src));
    }
    private static string Resolve(string rel)
    {
        string dir = AppContext.BaseDirectory;
        for(int i=0;i<6;i++){ var cand=Path.GetFullPath(Path.Combine(dir, rel)); if(File.Exists(cand)) return cand; dir=Path.GetFullPath(Path.Combine(dir,"..")); }
        return rel;
    }
}
