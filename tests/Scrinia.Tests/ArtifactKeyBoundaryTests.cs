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

            ArtifactEntry persistent = await store.StoreAsync(new MemoryWrite("/topic/note", "local", ["body"]));
            ArtifactEntry ephemeral = await store.StoreAsync(new MemoryWrite("~scratch", "local", ["thought"]));

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
        foreach (string key in new[] { "/rooted", "C:/drive", "\\\\server\\share", "artifacts\\note.nmp2", "file:///note" })
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

    private static void AssertPortableKey(string key)
    {
        key.Should().NotBeNullOrWhiteSpace();
        key.Should().NotContain("\\");
        key.Should().NotContain(":");
        key.Should().NotStartWith("/");
        Uri.TryCreate(key, UriKind.Absolute, out _).Should().BeFalse();
    }
}
