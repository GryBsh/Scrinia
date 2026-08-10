using System.Reflection;
using FluentAssertions;
using Scrinia;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>
/// Pins the store boundary: no member of <see cref="IMemoryStore"/> throws for lack of a local
/// filesystem, so a remote store can implement all of it without refusing a capability. The
/// failure mode this catches is someone adding a local-only member back to the shared interface —
/// which nothing else does.
/// <para>That is narrower than "a remote store honours the whole contract in substance".
/// <c>HttpMemoryStore</c> still implements <c>Upsert</c> and <c>ArchiveVersion</c> as silent
/// no-ops rather than throws — defensible, since the server does that work — which is exactly why
/// a "count the throws" criterion never surfaced them.
/// <c>LoadIndex</c> used to be a third silent no-op (returned <c>[]</c> unconditionally, recorded
/// as a known gap); it now proxies to the listing endpoint instead
/// (remote-write-correctness Task 6). <c>TryUpdateEntry</c> is the one member with a fourth shape:
/// it is neither a throw nor a silent success — <c>HttpMemoryStore</c> overrides the default
/// (LoadIndex + mutate + Upsert) to fail closed, because the fixed <c>LoadIndex</c> would
/// otherwise let it find an entry, discard the mutation into the no-op <c>Upsert</c>, and still
/// report <c>true</c>.</para>
/// </summary>
public sealed class StoreBoundaryTests
{
    /// <summary>The members that moved off <see cref="IMemoryStore"/> onto
    /// <see cref="ILocalMemoryStore"/>. Same list as
    /// <see cref="IMemoryStore_DoesNotDeclareLocalOnlyMembers"/> asserts the absence of, read from
    /// the other side: gone from the shared interface AND present on the local one. A half-finished
    /// split satisfies only one of the two.</summary>
    private static readonly string[] LocalOnlyMembers =
    [
        "ArtifactPath", "FindArtifactPath", "GetStoreDirForScope",
        "WorkspaceRoot", "ListTopicArtifacts", "ImportTopicEntries",
    ];

    [Fact]
    public void ILocalMemoryStore_DeclaresExactlyTheLocalOnlyMembers()
    {
        // Property accessors (get_WorkspaceRoot) are members too; compare on the declared
        // property/method surface an implementer actually writes.
        var names = typeof(ILocalMemoryStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Where(m => m is MethodInfo { IsSpecialName: false } or PropertyInfo)
            .Select(m => m.Name)
            .ToHashSet();

        names.Should().BeEquivalentTo(LocalOnlyMembers,
            because: "the six members that need a filesystem live here and nowhere else — an " +
                     "extra one means a member was moved without being removed upstream, a " +
                     "missing one means it never arrived");
    }

    [Fact]
    public void FileMemoryStore_Implements_ILocalMemoryStore()
    {
        typeof(ILocalMemoryStore).IsAssignableFrom(typeof(FileMemoryStore)).Should().BeTrue(
            because: "FileMemoryStore is the sole implementation; without it the interface is " +
                     "declared but unimplemented and every guard refuses unconditionally");
    }

    [Fact]
    public void HttpMemoryStore_DoesNotImplement_ILocalMemoryStore()
    {
        typeof(ILocalMemoryStore).IsAssignableFrom(typeof(HttpMemoryStore)).Should().BeFalse(
            because: "the whole point of the split is that a remote store fails to compile as a " +
                     "local one; if it implemented the interface, every guard would pass and the " +
                     "throws would simply move back inside the members");
    }

    [Fact]
    public void HttpMemoryStore_ThrowsNotSupported_ForExactlyOneReason()
    {
        // The one legitimate NotSupportedException in remote mode is the file:// rejection
        // inside ResolveArtifactAsync — a rejection of an input value, not of a capability.
        var sourcePath = Path.Combine(RepoRoot(), "src", "Scrinia", "HttpMemoryStore.cs");
        var throwLines = File.ReadAllLines(sourcePath)
            .Select((line, i) => (Line: line, Number: i + 1))
            .Where(x => x.Line.Contains("throw new NotSupportedException"))
            .ToList();

        throwLines.Should().ContainSingle(
            because: "every other NotSupportedException marked a member that belongs on " +
                     "ILocalMemoryStore, not on the interface a remote store implements");
        throwLines[0].Line.Should().Contain("file://");
    }

    [Fact]
    public void IMemoryStore_DoesNotDeclareLocalOnlyMembers()
    {
        var names = typeof(IMemoryStore)
            .GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Select(m => m.Name)
            .ToHashSet();

        names.Should().NotContain("WorkspaceRoot");
        names.Should().NotContain("ArtifactPath");
        names.Should().NotContain("FindArtifactPath");
        names.Should().NotContain("GetStoreDirForScope");
        names.Should().NotContain("ListTopicArtifacts");
        names.Should().NotContain("ImportTopicEntries");
        names.Should().NotContain("SaveIndex");
        names.Should().NotContain("GenerateContentPreview");

        names.Should().Contain("ArtifactUri",
            because: "HttpMemoryStore implements it for real, returning a remote:// URI");
    }

    [Fact]
    public void IMemoryStore_LoadIndex_ReturnsAReadOnlyList()
    {
        typeof(IMemoryStore).GetMethod(nameof(IMemoryStore.LoadIndex))!.ReturnType
            .Should().Be<IReadOnlyList<ArtifactEntry>>(
                because: "LoadIndex hands out a live view of the index cache. The read-only type "
                       + "is what makes mutating it a compile error instead of silent cache "
                       + "corruption — widening it back would reopen that hole with no other "
                       + "test failing");
    }

    private static string RepoRoot()
    {
        // Same walk-up as tests/Scrinia.Merge.Tests/GitMergeHarness.cs:33-38. Copy its
        // failure message too — it names the directory it started from, which is the only
        // thing worth knowing when this fails. That project is outside this branch's test
        // scope, so the helper is duplicated rather than referenced.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Scrinia.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException(
            $"Could not find Scrinia.sln walking up from {AppContext.BaseDirectory}.");
    }
}
