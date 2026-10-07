using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using Scrinia.Core.Configuration;
using Scrinia.Core.Migration;

namespace Scrinia.Tests;

public sealed class TrackingRecoveryJournalTests
{
    private static readonly string[] FixedNames = ["config.json", ".gitignore", ".gitattributes", "merge.config"];
    private static TrackingState Present(byte[] bytes) => new(true, bytes.Length,
        Convert.ToHexString(SHA256.HashData(bytes)), bytes);

    private static TrackingJournal Valid() => new(1, "test-op", "set", "prepared",
        FixedNames
            .Select(name => new TrackingMember(name, new(false, 0, null, null), Present([]))).ToArray());

    [Fact]
    public void ExistingEmptyStateIsNotAbsence()
    {
        TrackingJournal journal = Valid();
        TrackingRecoveryJournal.Validate(journal);
        Assert.False(journal.Members[0].Before.Exists);
        Assert.True(journal.Members[0].After.Exists);
    }

    [Fact]
    public void DuplicateMembersAreRefused()
    {
        TrackingJournal journal = Valid();
        journal.Members[1] = journal.Members[0];
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Validate(journal));
    }

    [Theory]
    [InlineData("set", "prepared")]
    [InlineData("migrate", "committed")]
    public void RoundTripPreservesEveryState(string operation, string decision)
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = Valid() with { Operation = operation, Decision = decision, OperationId = new string('é', 32) };
        journal.Members[3] = journal.Members[3] with { Before = Present([0, 1, 255]), After = new(false, 0, null, null) };
        string path = JournalPath(scope.WorkspaceDir);
        byte[] serialized = TrackingRecoveryJournal.Serialize(journal);
        File.WriteAllBytes(path, serialized);
        var read = TrackingRecoveryJournal.Read(path, scope.WorkspaceDir);
        Assert.Equal(serialized, TrackingRecoveryJournal.Serialize(read));
        Assert.Null(read.Members[0].Before.Bytes);
        Assert.Empty(read.Members[0].After.Bytes!);
        Assert.Equal(new byte[] { 0, 1, 255 }, read.Members[3].Before.Bytes);
        Assert.Equal(operation, read.Operation);
        Assert.Equal(decision, read.Decision);
    }

    [Theory]
    [InlineData("version0")]
    [InlineData("version2")]
    [InlineData("operation")]
    [InlineData("decision")]
    [InlineData("empty-id")]
    [InlineData("long-id")]
    [InlineData("missing-member")]
    [InlineData("extra-member")]
    [InlineData("traversal")]
    [InlineData("case")]
    [InlineData("negative-length")]
    [InlineData("wrong-length")]
    [InlineData("sha")]
    [InlineData("null-bytes")]
    [InlineData("absent-bytes")]
    [InlineData("absent-sha")]
    [InlineData("absent-length")]
    [InlineData("null-member")]
    [InlineData("null-state")]
    public void InvalidRecordsAreRejectedByValidationAndSerialization(string change)
    {
        var j = Valid();
        j = change switch
        {
            "version0" => j with { Version = 0 },
            "version2" => j with { Version = 2 },
            "operation" => j with { Operation = "delete" },
            "decision" => j with { Decision = "complete" },
            "empty-id" => j with { OperationId = "" },
            "long-id" => j with { OperationId = new string('é', 33) },
            "missing-member" => j with { Members = j.Members[..3] },
            "extra-member" => j with { Members = [.. j.Members, j.Members[0]] },
            _ => j
        };
        var member = j.Members[0];
        j.Members[0] = change switch
        {
            "traversal" => member with { Path = "../config.json" },
            "case" => member with { Path = "Config.json" },
            "negative-length" => member with { After = member.After with { Length = -1 } },
            "wrong-length" => member with { After = member.After with { Length = 1 } },
            "sha" => member with { After = member.After with { Sha256 = new string('0', 64) } },
            "null-bytes" => member with { After = member.After with { Bytes = null } },
            "absent-bytes" => member with { Before = member.Before with { Bytes = [] } },
            "absent-sha" => member with { Before = member.Before with { Sha256 = member.After.Sha256 } },
            "absent-length" => member with { Before = member.Before with { Length = 1 } },
            "null-member" => null!,
            "null-state" => member with { Before = null! },
            _ => member
        };
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Validate(j));
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Serialize(j));
        AssertRefused(JsonSerializer.Serialize(j, ConfigJsonContext.Default.TrackingJournal));
    }

    public static IEnumerable<object[]> RequiredProperties()
    {
        foreach (string name in new[] { "version", "operationId", "operation", "decision", "members" })
            yield return new object[] { "journal", name };
        foreach (string name in new[] { "path", "before", "after" })
            yield return new object[] { "member", name };
        foreach (string name in new[] { "exists", "length", "sha256", "bytes" })
            yield return new object[] { "state", name };
    }

    [Theory]
    [MemberData(nameof(RequiredProperties))]
    public void MissingPropertiesAreRefused(string level, string property)
    {
        var json = JsonNode.Parse(TrackingRecoveryJournal.Serialize(Valid()))!.AsObject();
        Target(json, level).Remove(property);
        AssertRefused(json.ToJsonString());
    }

    [Theory]
    [InlineData("journal", "version", "1")]
    [InlineData("member", "path", "\"config.json\"")]
    [InlineData("state", "exists", "false")]
    [InlineData("journal", "unknown", "null")]
    [InlineData("member", "unknown", "null")]
    [InlineData("state", "unknown", "null")]
    public void DuplicateAndUnknownPropertiesAreRefused(string level, string property, string value)
    {
        var json = JsonNode.Parse(TrackingRecoveryJournal.Serialize(Valid()))!.AsObject();
        string target = Target(json, level).ToJsonString();
        string changed = target.Insert(1, $"\"{property}\":{value},");
        AssertRefused(json.ToJsonString().Replace(target, changed, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("AB==", 1)]
    [InlineData("AAB=", 2)]
    [InlineData("AA== ", 1)]
    [InlineData(" AA==", 1)]
    [InlineData("AA\n==", 1)]
    public void NoncanonicalBase64IsRefused(string payload, int decodedLength)
    {
        // A permissive decoder produces these exact zero bytes, so length/hash validation
        // cannot conceal a missing canonical alphabet, whitespace or padding-bit check.
        var journal = Valid();
        journal.Members[0] = journal.Members[0] with { After = Present(new byte[decodedLength]) };
        var json = JsonNode.Parse(TrackingRecoveryJournal.Serialize(journal))!.AsObject();
        json["members"]![0]!["after"]!["bytes"] = payload;
        AssertRefused(json.ToJsonString());
    }

    [Fact]
    public void OversizedStateIsRefusedEvenWithSmallDeclaredLength()
    {
        var j = Valid();
        byte[] oversized = new byte[16 * 1024 * 1024 + 1];
        j.Members[0] = j.Members[0] with { After = Present(oversized) };
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Validate(j));
        var json = JsonNode.Parse(TrackingRecoveryJournal.Serialize(Valid()))!.AsObject();
        json["members"]![0]!["after"]!["bytes"] = Convert.ToBase64String(oversized);
        AssertRefused(json.ToJsonString());
    }

    [Fact]
    public void EightMaximumStatesReachTheTotalLimit()
    {
        var j = Valid();
        var state = Present(new byte[16 * 1024 * 1024]);
        for (int i = 0; i < j.Members.Length; i++)
            j.Members[i] = j.Members[i] with { Before = state, After = state };
        TrackingRecoveryJournal.Validate(j);
        // With exactly eight states, exceeding the total necessarily also exceeds a state limit.
        j.Members[3] = j.Members[3] with { After = state with { Length = 16L * 1024 * 1024 + 1 } };
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Validate(j));
    }

    [Fact]
    public void OversizedSerializedInputIsRefusedWithoutAllocatingItsContents()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        using (var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
            stream.SetLength(192L * 1024 * 1024 + 1);
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
        Assert.Equal(192L * 1024 * 1024 + 1, new FileInfo(path).Length);
    }

    [Fact]
    public void PresenceGateAcceptsOnlyPositiveAbsenceAndNeverReplays()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        TrackingRecoveryJournal.ThrowIfPresent(scope.WorkspaceDir);
        File.WriteAllText(path, "complete\n");
        var error = Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(scope.WorkspaceDir));
        Assert.Equal(path, error.JournalPath);
        Assert.Equal("complete\n", File.ReadAllText(path));
        File.Delete(path);
        Directory.CreateDirectory(path);
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(scope.WorkspaceDir));
    }

    [Fact]
    public void MissingReadAndExclusiveFileAreRecoveryFailures()
    {
        using var scope = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
        File.WriteAllBytes(path, TrackingRecoveryJournal.Serialize(Valid()));
        using var locked = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(scope.WorkspaceDir));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("dangling-journal")]
    [InlineData("target")]
    [InlineData("parent")]
    public void ActualLinksAreRefused(string location)
    {
        using var scope = new TestHelpers.StoreScope();
        using var outside = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        File.WriteAllBytes(path, TrackingRecoveryJournal.Serialize(Valid()));
        string link;
        if (location == "parent")
        {
            string linkedRoot = Path.Combine(outside.WorkspaceDir, "workspace");
            Directory.CreateDirectory(linkedRoot);
            link = Path.Combine(linkedRoot, ".scrinia");
            Directory.CreateSymbolicLink(link, Path.Combine(scope.WorkspaceDir, ".scrinia"));
            Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(Path.Combine(link, ".tracking-recovery.json"), linkedRoot));
            Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(linkedRoot));
        }
        else
        {
            link = location == "target" ? Path.Combine(scope.WorkspaceDir, ".scrinia", "merge.config") : path;
            if (link == path) File.Delete(path);
            string target = Path.Combine(outside.WorkspaceDir, "evidence");
            if (location != "dangling-journal") File.WriteAllBytes(target, TrackingRecoveryJournal.Serialize(Valid()));
            File.CreateSymbolicLink(link, target);
            Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
            if (location != "target")
                Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(scope.WorkspaceDir));
            File.Delete(link);
        }
        if (location == "parent") Directory.Delete(link);
    }

    [Fact]
    public void JournalOutsideWorkspaceIsRefused()
    {
        using var scope = new TestHelpers.StoreScope();
        using var other = new TestHelpers.StoreScope();
        string path = JournalPath(other.WorkspaceDir);
        File.WriteAllBytes(path, TrackingRecoveryJournal.Serialize(Valid()));
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
    }

    [Theory]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("complete")]
    [InlineData("complete\n")]
    public void NonJournalEvidenceIsRefused(string json) => AssertRefused(json);

    [Fact]
    public void EscapedPayloadAndReorderedPropertiesRemainValid()
    {
        using var scope = new TestHelpers.StoreScope();
        var journal = Valid();
        journal.Members[0] = journal.Members[0] with { After = Present([255]) };
        string json = Encoding.UTF8.GetString(TrackingRecoveryJournal.Serialize(journal));
        json = json.Replace("/w==", "\\u002Fw==", StringComparison.Ordinal);
        string path = JournalPath(scope.WorkspaceDir);
        File.WriteAllText(path, json);
        Assert.Equal(new byte[] { 255 }, TrackingRecoveryJournal.Read(path, scope.WorkspaceDir).Members[0].After.Bytes);
        var node = JsonNode.Parse(json)!.AsObject();
        var after = node["members"]![0]!["after"]!.AsObject();
        var reordered = new JsonObject
        {
            ["bytes"] = after["bytes"]!.DeepClone(),
            ["sha256"] = after["sha256"]!.DeepClone(),
            ["length"] = after["length"]!.DeepClone(),
            ["exists"] = after["exists"]!.DeepClone()
        };
        node["members"]![0]!["after"] = reordered;
        File.WriteAllText(path, node.ToJsonString());
        Assert.Equal(new byte[] { 255 }, TrackingRecoveryJournal.Read(path, scope.WorkspaceDir).Members[0].After.Bytes);
    }

    [Theory]
    [InlineData("config.json")]
    [InlineData(".gitignore")]
    [InlineData(".gitattributes")]
    [InlineData("merge.config")]
    public void DirectoryTargetsAreRefused(string name)
    {
        using var scope = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        File.WriteAllBytes(path, TrackingRecoveryJournal.Serialize(Valid()));
        Directory.CreateDirectory(Path.Combine(scope.WorkspaceDir, ".scrinia", name));
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
    }

    [Fact]
    public void InvalidUnicodeIsARecoveryFailure()
    {
        string json = Encoding.UTF8.GetString(TrackingRecoveryJournal.Serialize(Valid()))
            .Replace("test-op", "\\uD800", StringComparison.Ordinal);
        AssertRefused(json);
    }


    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void WorkspaceAndAncestorAliasesUseCanonicalOwnership(bool ancestor)
    {
        using var scope = new TestHelpers.StoreScope();
        using var outside = new TestHelpers.StoreScope();
        string realRoot = ancestor ? Path.Combine(scope.WorkspaceDir, "workspace") : scope.WorkspaceDir;
        Directory.CreateDirectory(Path.Combine(realRoot, ".scrinia"));
        string alias = Path.Combine(outside.WorkspaceDir, "alias");
        Directory.CreateSymbolicLink(alias, scope.WorkspaceDir);
        string root = ancestor ? Path.Combine(alias, "workspace") : alias;
        try
        {
            TrackingRecoveryJournal.ThrowIfPresent(root);
            byte[] evidence = TrackingRecoveryJournal.Serialize(Valid());
            File.WriteAllBytes(JournalPath(realRoot), evidence);
            var read = TrackingRecoveryJournal.Read(JournalPath(root), root);
            Assert.Equal(evidence, TrackingRecoveryJournal.Serialize(read));
            Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(root));
        }
        finally { Directory.Delete(alias); }
    }

    [Fact]
    public void MissingWorkspaceAncestryIsPositiveAbsence()
    {
        using var scope = new TestHelpers.StoreScope();
        string root = Path.Combine(scope.WorkspaceDir, "absent", "workspace");
        TrackingRecoveryJournal.ThrowIfPresent(root);
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(JournalPath(root), root));
    }

    [SkippableFact]
    public void UnreadableAncestryIsNotAbsence()
    {
        if (OperatingSystem.IsWindows())
        {
            Skip.If(true, "Unix permissions regression requires Unix.");
            return;
        }
        using var scope = new TestHelpers.StoreScope();
        string parent = Path.Combine(scope.WorkspaceDir, ".scrinia");
        UnixFileMode prior = File.GetUnixFileMode(parent);
        try
        {
            File.SetUnixFileMode(parent, UnixFileMode.None);
            Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(scope.WorkspaceDir));
            Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(JournalPath(scope.WorkspaceDir), scope.WorkspaceDir));
        }
        finally { File.SetUnixFileMode(parent, prior); }
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void LinuxFifoCarriersAreClassifiedBeforeContentAccess(bool journalCarrier)
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The nonblocking guard relies on Linux FIFO read/write-open behavior.");
        using var scope = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        byte[] evidence = TrackingRecoveryJournal.Serialize(Valid());
        string fifo = journalCarrier ? path : Path.Combine(scope.WorkspaceDir, ".scrinia", "merge.config");
        if (!journalCarrier) File.WriteAllBytes(path, evidence);
        Assert.Equal(0, MakeFifo(fifo, 0x180));
        using var guard = new FileStream(fifo, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        // Bound the RED case too: an unfixed reader gets EOF when the guard closes.
        using var release = new Timer(_ => guard.Dispose(), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        var error = Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.ThrowIfPresent(scope.WorkspaceDir));
        if (!journalCarrier) Assert.Equal(evidence, File.ReadAllBytes(path));
        Assert.True(File.Exists(fifo));
    }

    [SkippableTheory]
    [InlineData(true)]
    [InlineData(false)]
    public void UnixSocketCarriersAreClassifiedBeforeContentAccess(bool journalCarrier)
    {
        Skip.If(OperatingSystem.IsWindows(), "Unix socket carrier regression requires Unix.");
        using var scope = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        string socketPath = journalCarrier ? path : Path.Combine(scope.WorkspaceDir, ".scrinia", "merge.config");
        if (!journalCarrier) File.WriteAllBytes(path, TrackingRecoveryJournal.Serialize(Valid()));
        using var socket = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.Unix,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Unspecified);
        socket.Bind(new System.Net.Sockets.UnixDomainSocketEndPoint(socketPath));
        var error = Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.True(File.Exists(socketPath));
    }

    [System.Runtime.InteropServices.DllImport("libc", EntryPoint = "mkfifo", SetLastError = true)]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Globalization", "CA2101",
        Justification = "Unix paths use explicitly marshalled UTF-8, not UTF-16.")]
    private static extern int MakeFifo(
        [System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.LPUTF8Str)] string path, uint mode);

    private static JsonObject Target(JsonObject json, string level) => level switch
    {
        "member" => json["members"]![0]!.AsObject(),
        "state" => json["members"]![0]!["before"]!.AsObject(),
        _ => json
    };

    private static string JournalPath(string root) => Path.Combine(root, ".scrinia", ".tracking-recovery.json");

    private static void AssertRefused(string json)
    {
        using var scope = new TestHelpers.StoreScope();
        string path = JournalPath(scope.WorkspaceDir);
        File.WriteAllText(path, json);
        var exception = Assert.Throws<TrackingRecoveryRequiredException>(() => TrackingRecoveryJournal.Read(path, scope.WorkspaceDir));
        Assert.Equal(path, exception.JournalPath);
        Assert.Equal(json, File.ReadAllText(path));
    }
}

