using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Bundles;
using Scrinia.Core.Formats;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class BundleMemoryIdentityTests
{
    [Theory]
    [InlineData("complete", false)]
    [InlineData("body", false)]
    [InlineData("sidecar", false)]
    [InlineData("complete", true)]
    [InlineData("body", true)]
    [InlineData("sidecar", true)]
    public async Task CaseInsensitiveMemoryIdentityPreservesStoredPairSpelling(string pair, bool overwrite)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        await store.StoreAsync(new MemoryWrite(MemoryAddress.Parse("/notes/Note"), ["before"u8.ToArray()],
            new Dictionary<string, IReadOnlyList<string>>(), []));
        string directory = PathRouter.ToStoreDir("local:notes", scope.WorkspaceDir);
        string body = Path.Combine(directory, "Note.nmp2");
        string metadata = Path.Combine(directory, "Note.meta.json");
        if (pair == "body") File.Delete(metadata);
        if (pair == "sidecar") File.Delete(body);
        var prior = Directory.GetFiles(directory).Where(path => !path.EndsWith(".lock", StringComparison.Ordinal))
            .ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);

        var result = await store.RestoreBundleAsync(Plan(overwrite));
        result.EntryCount.Should().Be(overwrite ? 1 : 0);
        if (!overwrite)
        {
            foreach (var file in prior) File.ReadAllBytes(Path.Combine(directory, file.Key!)).Should().Equal(file.Value);
            Directory.GetFiles(directory).Count(path => path.EndsWith(".nmp2", StringComparison.Ordinal) ||
                path.EndsWith(".meta.json", StringComparison.Ordinal)).Should().Be(prior.Count);
        }
        else
        {
            Directory.GetFiles(directory, "*.nmp2").Select(Path.GetFileName).Should().Equal("Note.nmp2");
            Directory.GetFiles(directory, "*.meta.json").Select(Path.GetFileName).Should().Equal("Note.meta.json");
            store.LoadIndex("local:notes").Should().ContainSingle().Which.Name.Should().Be("Note");
            File.ReadAllText(body).Should().Be("after");
        }
    }

    [SkippableTheory]
    [InlineData("body")]
    [InlineData("sidecar")]
    public async Task AmbiguousCaseOnlyMemoryTwinsAreRefusedBeforeJournal(string twin)
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        string directory = PathRouter.ToStoreDir("local:notes", scope.WorkspaceDir);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Note.nmp2"), "first");
        Skip.If(File.Exists(Path.Combine(directory, "note.nmp2")), "Requires a case-sensitive filesystem.");
        string lower = Path.Combine(directory, twin == "body" ? "note.nmp2" : "note.meta.json");
        File.WriteAllText(lower, "second");
        var prior = Directory.GetFiles(directory).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes);
        Func<Task> restore = () => store.RestoreBundleAsync(Plan(true));
        await restore.Should().ThrowAsync<BundleImportException>();
        foreach (var file in prior) File.ReadAllBytes(Path.Combine(directory, file.Key!)).Should().Equal(file.Value);
        File.Exists(Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json")).Should().BeFalse();
    }

    [Fact]
    public async Task CaseInsensitiveSelectionChecksTheExistingForwardVersionedSidecar()
    {
        using var scope = new TestHelpers.StoreScope();
        using var store = new FileMemoryStore(scope.WorkspaceDir);
        string directory = PathRouter.ToStoreDir("local:notes", scope.WorkspaceDir);
        Directory.CreateDirectory(directory);
        File.WriteAllText(Path.Combine(directory, "Note.nmp2"), "before");
        File.WriteAllText(Path.Combine(directory, "Note.meta.json"), """{"schemaVersion":999}""");
        Func<Task> restore = () => store.RestoreBundleAsync(Plan(true));
        (await restore.Should().ThrowAsync<BundleImportException>()).Which.InnerException.Should().BeOfType<ScopeReadOnlyException>();
        File.ReadAllText(Path.Combine(directory, "Note.nmp2")).Should().Be("before");
        File.ReadAllText(Path.Combine(directory, "Note.meta.json")).Should().Be("""{"schemaVersion":999}""");
    }

    private static BundleRestorePlan Plan(bool overwrite) => new(
        [new("local:notes", "notes", [new("note", "", 5, 1, DateTimeOffset.UtcNow)],
            new Dictionary<string, byte[]> { ["note"] = "after"u8.ToArray() })], [], overwrite);
}
