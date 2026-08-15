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
/// (LoadIndex + mutate + Upsert), because the no-op <c>Upsert</c> would otherwise let it find an
/// entry, discard the mutation, and still report <c>true</c>. That override used to correct the
/// contract by failing closed; it persists for real now, over
/// <c>GET /memories/{name}/entry</c> + <c>PATCH /memories/{name}</c>, so the MCP tool that needs a
/// metadata-only write — <c>link()</c>, via <c>MemoryTools.UpdateMeta</c> (no caller outside it
/// and the tests) — reports a remote success it can back up. It still returns <c>false</c> rather than
/// pretending when the server refuses one: a keyword change is rejected there, because keywords are
/// derived from content the metadata endpoint has none of. Recorded here rather than in a planning
/// document because <c>.superpowers/</c> is gitignored: a path under it does not exist for anyone
/// who clones this repo.</para>
/// </summary>
public sealed class StoreBoundaryTests
{
    /// <summary>The members declared on <see cref="ILocalMemoryStore"/> and nowhere else. Six —
    /// <c>ArtifactPath</c>, <c>FindArtifactPath</c>, <c>GetStoreDirForScope</c>,
    /// <c>WorkspaceRoot</c>, <c>ListTopicArtifacts</c>, <c>ImportTopicEntries</c> — moved off
    /// <see cref="IMemoryStore"/> when the split was made; <c>WriteCarriedCopy</c> never lived on
    /// <see cref="IMemoryStore"/> at all; it was declared local-only from the start.
    /// <see cref="IMemoryStore_DoesNotDeclareLocalOnlyMembers"/> asserts the absence of every name
    /// here from the shared interface — read from the other side — but it is a superset, not
    /// "the same list": it also covers <c>SaveIndex</c> and <c>GenerateContentPreview</c>, which
    /// are on neither interface. A half-finished split satisfies only one direction of the
    /// check.</summary>
    private static readonly string[] LocalOnlyMembers =
    [
        "ArtifactPath", "FindArtifactPath", "GetStoreDirForScope",
        "WorkspaceRoot", "ListTopicArtifacts", "ImportTopicEntries",
        "WriteCarriedCopy",
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
            because: "the seven members that need a filesystem live here and nowhere else — an " +
                     "extra one means a member was moved without being removed upstream, a " +
                     "missing one means it never arrived. Six are path accessors or import/export " +
                     "helpers that need a filesystem to answer at all; WriteCarriedCopy is not a " +
                     "path accessor but a write that only a filesystem-backed store can perform — a " +
                     "remote store forwards it rather than performing it");
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

    /// <summary>
    /// The criterion this test enforces is unchanged: a remote store may not refuse a capability
    /// for lack of a local filesystem. The only such refusal that was ever legitimate is the
    /// <c>file://</c> rejection inside <c>ResolveArtifactAsync</c> — a rejection of an input value,
    /// not of a capability — and it is once again the only one.
    /// <para>The store write surface (<c>StoreAsync</c> / <c>AppendAsync</c>) briefly added two
    /// more, of a different kind: not "this needs files I do not have" but "the wire cannot yet
    /// carry a derived entry back". That allowance was written to expire on its own — it pinned the
    /// count at exactly three and named the two members, so the day the wire landed this test would
    /// fail rather than let the refusals become permanent. It did, and this is the tightening: the
    /// write responses carry the complete entry now, both members POST and return what the server
    /// derived, and the count is back to one.</para>
    /// <para>Left at an exact count rather than an upper bound on purpose: a new refusal anywhere
    /// in this file must fail this test and be argued for, not silently absorbed. The
    /// owner assertion is what keeps the count honest — three throws would also be "exactly one
    /// non-<c>file://</c> refusal" if the surviving one moved to an unrelated member.</para>
    /// </summary>
    [Fact]
    public void HttpMemoryStore_ThrowsNotSupported_OnlyToRejectAnInputValue()
    {
        var sourcePath = Path.Combine(RepoRoot(), "src", "Scrinia", "HttpMemoryStore.cs");
        var lines = File.ReadAllLines(sourcePath);
        var throwLines = lines
            .Select((line, i) => (Line: line, Index: i))
            .Where(x => x.Line.Contains("throw new NotSupportedException"))
            .ToList();

        throwLines.Should().HaveCount(1,
            because: "every NotSupportedException beyond the file:// input rejection marked either " +
                     "a member that belongs on ILocalMemoryStore or a capability the wire could " +
                     "not yet serve — and the wire serves all of them now");

        throwLines[0].Line.Should().Contain("file://",
            because: "the one surviving throw rejects an input VALUE the remote mode has no meaning " +
                     "for; a refusal for any other reason is a capability this store is dropping");

        // The member it sits on, read as the nearest preceding public signature. Anchored on the
        // declaration, not merely on the substring: an unanchored match would be satisfied by a
        // future member whose name happens to CONTAIN this one.
        string owner = Enumerable.Range(0, throwLines[0].Index).Reverse()
            .Select(i => lines[i])
            .First(l => l.Contains("    public "));
        owner.Should().MatchRegex(@"\bResolveArtifactAsync\(",
            because: "the file:// rejection belongs to name resolution and nowhere else — the same " +
                     "throw sitting on a write or a read member would mean something entirely " +
                     "different and must not inherit this allowance");
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
        names.Should().NotContain("WriteCarriedCopy");
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
