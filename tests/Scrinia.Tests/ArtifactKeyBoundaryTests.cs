using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

public sealed class ArtifactKeyBoundaryTests
{
    [Fact]
    public async Task StoreEntriesUseCanonicalAddressesAndPortableKeys()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-key-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new FileMemoryStore(root);

            ArtifactEntry persistent = await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/topic/note".StartsWith("/") || "/topic/note".StartsWith("~") ? "/topic/note" : "/" + "/topic/note"), "local", ["body"]));
            ArtifactEntry ephemeral = await store.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("~scratch".StartsWith("/") || "~scratch".StartsWith("~") ? "~scratch" : "/" + "~scratch"), "local", ["thought"]));

            persistent.Address.Should().Be(MemoryAddress.Parse("/topic/note"));
            ephemeral.Address.Should().Be(MemoryAddress.Parse("~scratch"));
            AssertPortableKey(persistent.ArtifactKey);
            AssertPortableKey(ephemeral.ArtifactKey);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public void ArtifactKeyRejectsPhysicalAndTraversalForms()
    {
        foreach (string key in new[]
        {
            "", "/rooted", "C:/drive", "\\\\server\\share", "artifacts\\note.nmp2",
            "artifacts/./note.nmp2", "artifacts/../note.nmp2", "file:///note", "https://host/note"
        })
        {
            Action construct = () => _ = new ArtifactEntry(MemoryAddress.Parse("/note"), key, 1, 1, DateTimeOffset.UtcNow);
            construct.Should().Throw<ArgumentException>(key);
        }

        string root = Path.Combine(Path.GetTempPath(), "scrinia-key-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var store = new FileMemoryStore(root);
            Action traversal = () => store.ArtifactPath("note", "local:../outside");
            traversal.Should().Throw<ArgumentException>();
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    [Fact]
    public async Task ImportedEntryRegeneratesAWorkspaceLocalPhysicalPath()
    {
        string sourceRoot = CreateRoot();
        string targetRoot = CreateRoot();
        try
        {
            using var source = new FileMemoryStore(sourceRoot);
            using var target = new FileMemoryStore(targetRoot);
            ArtifactEntry sourceEntry = await source.StoreAsync(MemoryWrite.Create(MemoryAddress.Parse("/topic/note".StartsWith("/") || "/topic/note".StartsWith("~") ? "/topic/note" : "/" + "/topic/note"), "local", ["body"]));
            string artifact = await source.ReadArtifactAsync(sourceEntry.Address);

            target.ImportTopicEntries("local:topic", [sourceEntry],
                new Dictionary<string, byte[]> { [sourceEntry.Name] = System.Text.Encoding.UTF8.GetBytes(artifact) },
                overwrite: true);

            ArtifactEntry targetEntry = target.LoadIndex("local:topic").Should().ContainSingle().Subject;
            targetEntry.Address.Should().Be(sourceEntry.Address);
            targetEntry.ArtifactKey.Should().Be(sourceEntry.ArtifactKey);
            string physicalPath = Path.GetFullPath(target.ArtifactPath(targetEntry.Name, "local:topic"));
            physicalPath.StartsWith(Path.GetFullPath(targetRoot), StringComparison.OrdinalIgnoreCase)
                .Should().BeTrue();
            (await target.ReadArtifactAsync(targetEntry.Address)).Should().Be(artifact);
        }
        finally
        {
            DeleteRoot(sourceRoot);
            DeleteRoot(targetRoot);
        }
    }

    private static string CreateRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-key-boundary-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void DeleteRoot(string root)
    {
        try { Directory.Delete(root, recursive: true); } catch { }
    }

    private static void AssertPortableKey(string key)
    {
        key.Should().NotBeNullOrWhiteSpace();
        key.Should().NotContain("\\");
        key.Should().NotContain(":");
        key.Should().NotStartWith("/");
        Uri.TryCreate(key, UriKind.Absolute, out _).Should().BeFalse();
    }
}
