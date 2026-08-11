using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// The structural guarantee this workstream exists to establish: .scrinia/memories/ holds
/// NMP/2 store content and nothing else, so a store scope directory can never contain a
/// foreign .meta.json for SaveIndex's orphan sweep to delete.
/// </summary>
public class StorePurityInvariantTests
{
    private static ScriniaMcpTools Tools() => new();

    [Fact]
    public async Task MemoriesDirectory_NeverContainsFileEntities()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        await Tools().MemoryRemember(path: "/skill/qa", content: ["skill body"]);
        await Tools().MemoryRemember(path: "/agent/profile", content: ["agent body"]);
        await Tools().MemoryRemember(path: "/findings/note", content: ["ordinary memory"]);

        string memories = store.GetStoreDirForScope("local");
        Directory.EnumerateFiles(memories, "*.md", SearchOption.AllDirectories)
            .Should().BeEmpty(because: "hand-authored Markdown never enters the store");

        // The other half of the class doc's invariant: every .meta.json under a store scope
        // directory must be traceable to a real NMP/2 index entry for that scope. This mirrors
        // the exact expected-names set SaveIndex's orphan sweep computes for itself
        // (FileMemoryStore.SaveIndexUnsafe: SanitizeName(entry.Name) + ".meta.json" for each
        // entry currently in the scope's index, deleting any *.meta.json not in that set). A
        // blanket "no .meta.json" check would be wrong: legitimate NMP/2 sidecars (like
        // "note.meta.json" below) belong here.
        //
        // Cross-checked through a SECOND FileMemoryStore instance ("coldStore"), deliberately not
        // through `store`: the MemoryRemember calls above already warmed `store`'s in-process
        // _indexCache for "local:findings" (FileMemoryStore.cs LoadIndex, fast path at :616), and
        // a cache hit skips the directory scan entirely — checking through `store` would silently
        // validate against a stale in-memory snapshot, not what is actually on disk. `coldStore`
        // has never touched this scope, so every LoadIndex call below is a genuine cold read.
        //
        // What this can and cannot prove: it catches a sidecar present on disk but absent from
        // (or unparseable into) the scope's index — SaveIndex's orphan-sweep condition exactly.
        // It CANNOT catch a well-formed foreign sidecar: LoadEntriesFromSidecars (FileMemoryStore
        // .cs :685-709) derives the index from whatever is parseable in the directory itself, so
        // a foreign-but-schema-valid ArtifactEntry-shaped file is indistinguishable from a
        // legitimate entry to both the production sweep and this check on a cold load — there is
        // no independent source of truth to cross-check against. That gap is real and unclosed
        // by this assertion: it narrows the invariant to "no orphaned or corrupt sidecar
        // survives", not "no foreign sidecar of any shape survives".
        var coldStore = new FileMemoryStore(store.WorkspaceRoot);
        foreach (string metaFile in Directory.EnumerateFiles(memories, "*.meta.json", SearchOption.AllDirectories))
        {
            string relativeDir = Path.GetRelativePath(memories, Path.GetDirectoryName(metaFile)!);
            string scopeName = relativeDir == "."
                ? "local"
                : "local:" + relativeDir.Replace(Path.DirectorySeparatorChar, '/');

            // A cold load tolerates a schema-valid-but-fieldless sidecar (e.g. "{}" — no
            // `required` members on ArtifactEntry's constructor, so System.Text.Json fills
            // missing fields silently) by producing an entry with Name == null rather than
            // rejecting the file. SanitizeName has no null guard, so an unfiltered Select here
            // would throw NullReferenceException while building expectedSidecars instead of
            // failing the assertion below — confirmed by forcing exactly that path during this
            // check's development. Filtering null-named entries out here converts that crash
            // into the clean, readable failure this loop exists to produce: the malformed file's
            // own name still won't be in expectedSidecars, since nothing legitimately claims it.
            var expectedSidecars = new HashSet<string>(
                coldStore.LoadIndex(scopeName)
                    .Where(e => e.Name is not null)
                    .Select(e => coldStore.SanitizeName(e.Name) + ".meta.json"),
                StringComparer.OrdinalIgnoreCase);

            expectedSidecars.Should().Contain(Path.GetFileName(metaFile),
                because: $"a foreign or corrupt sidecar in scope \"{scopeName}\" is exactly what " +
                         "SaveIndex's orphan sweep would delete");
        }
    }

    [Theory]
    [InlineData("skill")]
    [InlineData("agent")]
    public void FileEntityDirectories_SitOutsideTheStore(string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        string dir = PathRouter.ToFileEntityDir(kind, store.WorkspaceRoot);
        string memories = store.GetStoreDirForScope("local");

        PathContainment.IsWithin(memories, dir).Should().BeFalse(
            because: $"{kind} is a hand-authored file entity, not store content");
    }

    [Fact]
    public async Task SkillWriteAndRecallResolveToThePathRouterDirectory()
    {
        // The DRY guarantee made executable for the skill kind: ".scrinia/skills/" was
        // previously named at five sites behind two divergent GetScriniaBaseDir
        // implementations that agreed only by coincidence. Writing through the MCP surface
        // and finding the file where PathRouter says it should be proves writer and
        // authority agree; the listing and the load prove the two read paths agree too.
        //
        // Named for the skill kind specifically (not "every reader"): the agent kind's
        // write/recall round-trip is already covered by AgentFileEntityTests, which also
        // exercises the orphan-sweep regression agent's move was made to prevent — adding it
        // here would duplicate that coverage rather than extend it.
        using var scope = new TestHelpers.StoreScope();
        var store = TestHelpers.LocalStore;

        await Tools().MemoryRemember(path: "/skill/qa", content: ["skill body"]);

        string authoritative = Path.Combine(
            PathRouter.ToFileEntityDir("skill", store.WorkspaceRoot), "qa.md");
        File.Exists(authoritative).Should().BeTrue();

        string listing = await Tools().MemoryRecall(path: "/skill/");
        listing.Should().Contain("/skill/qa [file]");

        string loaded = await Tools().MemoryRecall(path: "/skill/qa");
        loaded.Should().Contain("skill body");
    }
}
