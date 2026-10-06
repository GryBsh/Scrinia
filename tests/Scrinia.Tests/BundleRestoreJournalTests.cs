using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using FluentAssertions;
using Scrinia.Core.Bundles;

namespace Scrinia.Tests;

public sealed class BundleRestoreJournalTests
{
    private static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    private static BundleJournalMember Member(string name = "one", byte[]? before = null) =>
        new($".scrinia/memories/{name}.nmp2", "memory-body", before is not null, before,
            before is null ? null : Hash(before), true, Hash("after"u8.ToArray()));

    [Fact]
    public void RoundTripPreservesEmptyAndAbsentBeforeStates()
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = BundleRestoreJournalCodec.CreatePrepared("operation-1", scope.WorkspaceDir,
            [Member("empty", []), Member("absent"), Member("prior", "prior"u8.ToArray())], BundleReadLimits.Default);
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json");
        File.WriteAllBytes(path, BundleRestoreJournalCodec.SerializeValidated(journal, scope.WorkspaceDir, BundleReadLimits.Default));
        var read = BundleRestoreJournalCodec.ReadValidated(path, scope.WorkspaceDir, BundleReadLimits.Default);
        read.Members.Single(m => m.RelativePath.EndsWith("empty.nmp2")).BeforeBytes.Should().BeEmpty();
        read.Members.Single(m => !m.BeforeExists).BeforeBytes.Should().BeNull();
        read.Members.Single(m => m.RelativePath.EndsWith("prior.nmp2")).BeforeBytes.Should().Equal("prior"u8.ToArray());
        var json = JsonNode.Parse(File.ReadAllBytes(path))!;
        json["members"]!.AsArray().Single(m => m!["beforeExists"]!.GetValue<bool>() == false)!["beforeBytes"].Should().BeNull();
        read.ManifestSha256.Should().Be(journal.ManifestSha256);
    }

    [Theory]
    [InlineData("version", "missing")]
    [InlineData("version", "newer")]
    [InlineData("operationId", "blank")]
    [InlineData("operationId", "oversized")]
    [InlineData("state", "unknown")]
    [InlineData("workspaceBinding", "mismatch")]
    [InlineData("manifestSha256", "wrong")]
    [InlineData("beforeBytes", "invalid")]
    [InlineData("beforeSha256", "wrong")]
    [InlineData("beforeBytes", "missing")]
    [InlineData("beforeExists", "missing")]
    [InlineData("afterExists", "missing")]
    [InlineData("afterSha256", "wrong")]
    [InlineData("relativePath", "traversal")]
    [InlineData("relativePath", "config")]
    [InlineData("relativePath", "long")]
    [InlineData("kind", "unknown")]
    public void InvalidEvidenceIsRetainedWithoutReplay(string property, string change)
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = BundleRestoreJournalCodec.CreatePrepared("operation-1", scope.WorkspaceDir,
            [Member("one", "prior"u8.ToArray()), Member("last", "prior"u8.ToArray())], BundleReadLimits.Default);
        var json = JsonNode.Parse(BundleRestoreJournalCodec.SerializeValidated(journal, scope.WorkspaceDir, BundleReadLimits.Default))!.AsObject();
        JsonObject target = property is "beforeBytes" or "beforeSha256" or "relativePath" or "kind" or "beforeExists" or "afterExists" or "afterSha256"
            ? json["members"]!.AsArray()[1]!.AsObject() : json;
        if (change == "missing") target.Remove(property);
        else target[property] = change switch
        {
            "newer" => JsonValue.Create(2),
            "blank" => JsonValue.Create(" "),
            "oversized" => JsonValue.Create(new string('a', 65)),
            "wrong" or "mismatch" => JsonValue.Create(new string('0', 64)),
            "traversal" => JsonValue.Create(".scrinia/memories/../config.json"),
            "config" => JsonValue.Create(".scrinia/config.json"),
            "long" => JsonValue.Create(".scrinia/memories/" + new string('a', 4096) + ".nmp2"),
            "invalid" => JsonValue.Create("!!!"),
            _ => JsonValue.Create("unknown")
        };
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json");
        byte[] evidence = Encoding.UTF8.GetBytes(json.ToJsonString());
        File.WriteAllBytes(path, evidence);
        string sentinel = Path.Combine(scope.WorkspaceDir, ".scrinia", "sentinel.txt");
        File.WriteAllText(sentinel, "keep");
        Action read = () => BundleRestoreJournalCodec.ReadValidated(path, scope.WorkspaceDir, BundleReadLimits.Default);
        read.Should().Throw<BundleImportException>().Where(e => e.Failure == BundleImportFailure.RecoveryRequired);
        File.ReadAllBytes(path).Should().Equal(evidence);
        File.ReadAllText(sentinel).Should().Be("keep");
    }

    [Fact]
    public void RejectsAliasesAbsentPayloadAndLimits()
    {
        using var scope = new TestHelpers.StoreScope();
        BundleJournalMember member = Member("one", "prior"u8.ToArray());
        foreach (BundleJournalMember[] members in new[]
        {
            new[] { member, member }, new[] { member, member with { RelativePath = ".scrinia/memories/ONE.nmp2" } },
            new[] { member with { BeforeExists = false } }
        })
        {
            Action create = () => BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, members, BundleReadLimits.Default);
            create.Should().Throw<BundleImportException>();
        }
        Action count = () => BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [member], BundleReadLimits.Default with { JournalMembers = 0 });
        count.Should().Throw<BundleImportException>();
        Action bytes = () => BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [member], BundleReadLimits.Default with { PriorBytes = 4 });
        bytes.Should().Throw<BundleImportException>();
    }

    [Theory]
    [InlineData("prepared", "before", true)]
    [InlineData("prepared", "after", true)]
    [InlineData("prepared", "third", false)]
    [InlineData("committed", "before", false)]
    [InlineData("committed", "after", true)]
    [InlineData("rolled-back", "before", true)]
    [InlineData("rolled-back", "after", false)]
    public void PreflightEnforcesLifecycleWithoutChangingMembers(string state, string current, bool allowed)
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir,
            [Member("one", "before"u8.ToArray()), Member("two")], BundleReadLimits.Default) with { State = state };
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "one.nmp2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, current);
        Action preflight = () => BundleRestoreJournalCodec.PreflightCurrentState(journal, scope.WorkspaceDir, default);
        if (allowed && state != "committed") preflight.Should().NotThrow();
        else preflight.Should().Throw<BundleImportException>(); // committed also requires member two's after state
        File.ReadAllText(path).Should().Be(current);
        File.Exists(Path.Combine(Path.GetDirectoryName(path)!, "two.nmp2")).Should().BeFalse();
    }

    [Fact]
    public void RejectsDirectoryAndSymlinkTargets()
    {
        using var scope = new TestHelpers.StoreScope();
        string target = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "one.nmp2");
        Directory.CreateDirectory(target);
        Action directory = () => BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [Member()], BundleReadLimits.Default);
        directory.Should().Throw<BundleImportException>();
        Directory.Delete(target);
        File.CreateSymbolicLink(target, Path.Combine(scope.WorkspaceDir, "outside.nmp2"));
        directory.Should().Throw<BundleImportException>();
    }

    [Fact]
    public void AllowsCacheNamedTopicButRejectsArchiveDirectories()
    {
        using var scope = new TestHelpers.StoreScope();
        var cache = Member() with { RelativePath = ".scrinia/memories/cache/one.nmp2" };
        var journal = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [cache], BundleReadLimits.Default);
        journal.Members.Single().RelativePath.Should().Be(".scrinia/memories/cache/one.nmp2");
        foreach (string path in new[] { ".scrinia/memories/versions/one.nmp2", ".scrinia/memories/topic/Versions/one.meta.json" })
        {
            Action create = () => BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir,
                [cache with { RelativePath = path, Kind = path.EndsWith(".json") ? "memory-meta" : "memory-body" }], BundleReadLimits.Default);
            create.Should().Throw<BundleImportException>();
        }
    }

    [Theory]
    [InlineData(".scrinia/skills/example.md", "skill-body")]
    [InlineData(".scrinia/skills/example.meta.json", "skill-meta")]
    [InlineData(".scrinia/agent/example.md", "agent-body")]
    [InlineData(".scrinia/agent/example.meta.json", "agent-meta")]
    [InlineData(".scrinia/memories/topic/one.meta.json", "memory-meta")]
    public void AllowsEachLiveMemberKind(string path, string kind)
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir,
            [Member() with { RelativePath = path, Kind = kind }], BundleReadLimits.Default);
        BundleRestoreJournalCodec.SerializeValidated(journal, scope.WorkspaceDir, BundleReadLimits.Default).Should().NotBeEmpty();
    }

    [Fact]
    public void ManifestIsOrdinalAndSerializedOutputAndInputAreBounded()
    {
        using var scope = new TestHelpers.StoreScope();
        var limits = BundleReadLimits.Default;
        var first = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [Member("z"), Member("a")], limits);
        var second = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [Member("a"), Member("z")], limits);
        first.ManifestSha256.Should().Be(second.ManifestSha256);
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", ".bundle-restore-journal.json");
        File.WriteAllBytes(path, new byte[4097]);
        Action read = () => BundleRestoreJournalCodec.ReadValidated(path, scope.WorkspaceDir, limits with { PriorBytes = 0, JournalMembers = 0 });
        read.Should().Throw<BundleImportException>();
        new FileInfo(path).Length.Should().Be(4097);
    }

    [Fact]
    public void PreflightCommittedRequiresAfterAndCancellationDoesNotMutate()
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [Member()], BundleReadLimits.Default) with { State = "committed" };
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "one.nmp2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "after");
        Action preflight = () => BundleRestoreJournalCodec.PreflightCurrentState(journal, scope.WorkspaceDir, default);
        preflight.Should().NotThrow();
        Action cancel = () => BundleRestoreJournalCodec.PreflightCurrentState(journal, scope.WorkspaceDir, new CancellationToken(true));
        cancel.Should().Throw<OperationCanceledException>();
        File.ReadAllText(path).Should().Be("after");
    }

    [Fact]
    public void PreflightDoesNotConfuseAbsentWithEmptyFile()
    {
        using var scope = new TestHelpers.StoreScope();
        var member = Member("one", []) with { AfterExists = false, AfterSha256 = null };
        var journal = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [member], BundleReadLimits.Default) with { State = "rolled-back" };
        Action preflight = () => BundleRestoreJournalCodec.PreflightCurrentState(journal, scope.WorkspaceDir, default);
        preflight.Should().Throw<BundleImportException>();
        string path = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories", "one.nmp2");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, []);
        preflight.Should().NotThrow();
        preflight = () => BundleRestoreJournalCodec.PreflightCurrentState(journal with { State = "committed" }, scope.WorkspaceDir, default);
        preflight.Should().Throw<BundleImportException>();
        new FileInfo(path).Length.Should().Be(0);
    }

    [Fact]
    public void WorkspaceBindingRejectsAnotherCanonicalRoot()
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir, [Member()], BundleReadLimits.Default);
        string otherRoot = Path.Combine(scope.WorkspaceDir, "another-root");
        Directory.CreateDirectory(otherRoot);
        Action serialize = () => BundleRestoreJournalCodec.SerializeValidated(journal, otherRoot, BundleReadLimits.Default);
        serialize.Should().Throw<BundleImportException>();
    }

    [Fact]
    public void RejectsSymlinkAncestorEscapingWorkspace()
    {
        using var scope = new TestHelpers.StoreScope();
        using var outside = new TestHelpers.StoreScope();
        string memories = Path.Combine(scope.WorkspaceDir, ".scrinia", "memories");
        Directory.CreateDirectory(memories);
        string link = Path.Combine(memories, "escape");
        Directory.CreateSymbolicLink(link, outside.WorkspaceDir);
        try
        {
            Action create = () => BundleRestoreJournalCodec.CreatePrepared("op", scope.WorkspaceDir,
                [Member() with { RelativePath = ".scrinia/memories/escape/one.nmp2" }], BundleReadLimits.Default);
            create.Should().Throw<BundleImportException>();
            File.Exists(Path.Combine(outside.WorkspaceDir, "one.nmp2")).Should().BeFalse();
        }
        finally { Directory.Delete(link); }
    }
}
