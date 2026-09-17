using FluentAssertions;
using System.Text;
using System.Text.Json;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed partial class MultiMemoryTransactionContractTests
{
    [Fact]
    public void Request_RejectsMissingParticipantsAndBlankParticipantIdentity()
    {
        FluentActions.Invoking(() => new MultiMemoryTransactionRequest("local-topic:notes", null!))
            .Should().Throw<ArgumentNullException>();
        FluentActions.Invoking(() => new MultiMemoryTransactionRequest("local-topic:notes", []))
            .Should().Throw<ArgumentException>();

        var address = MemoryAddress.Parse("/notes/a");
        FluentActions.Invoking(() => new MultiMemoryParticipantRequest(address, " ", "memory"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => new MultiMemoryParticipantRequest(address, "op-1", " "))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Request_RejectsDuplicateCanonicalSubjects()
    {
        var participant = new MultiMemoryParticipantRequest(MemoryAddress.Parse("/notes/a"), "op-1", "memory");
        var duplicate = new MultiMemoryParticipantRequest(MemoryAddress.Parse("/notes/a"), "op-2", "memory");

        FluentActions.Invoking(() => new MultiMemoryTransactionRequest(
                "local-topic:notes", [participant, duplicate]))
            .Should().Throw<ArgumentException>();
    }

    [Fact]
    public void ManifestAndParticipantOutcome_PreserveTypedIdentityFields()
    {
        var manifest = new MultiMemoryParticipantManifest("note-a", "op-1", "memory");
        var outcome = new MultiMemoryParticipantOutcome(
            manifest.Subject, manifest.OperationId, manifest.Kind, "notes/a/.append-journal.json", true, false, null);

        manifest.Subject.Should().Be(outcome.Subject);
        manifest.OperationId.Should().Be(outcome.OperationId);
        manifest.Kind.Should().Be(outcome.Kind);
        outcome.Committed.Should().BeTrue();
        outcome.RolledBack.Should().BeFalse();
    }

    [Fact]
    public void Aggregate_DefensivelyCopiesParticipantOutcomesAndPreservesTheirOrder()
    {
        var outcomes = new List<MultiMemoryParticipantOutcome>
        {
            new("note-a", "op-a", "memory", "notes/a", true, false, null),
            new("note-b", "op-b", "memory", "notes/b", false, true, "rolled back")
        };

        var result = new MultiMemoryAggregateResult(
            "tx-1", "local-topic:notes", MultiMemoryOutcome.RolledBack, outcomes, null, null, null);
        outcomes[0] = outcomes[1];

        result.Participants.Select(outcome => outcome.Subject)
            .Should().Equal("note-a", "note-b");
        FluentActions.Invoking(() => ((IList<MultiMemoryParticipantOutcome>)result.Participants)[0] = outcomes[1])
            .Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void Outcome_DistinguishesCommittedCleanupPendingFromCommitted()
    {
        MultiMemoryOutcome.CommittedCleanupPending.Should().NotBe(MultiMemoryOutcome.Committed);
    }

    [Fact]
    public void RecoveryRequiredAggregate_RequiresSafeEvidencePath()
    {
        FluentActions.Invoking(() => new MultiMemoryAggregateResult(
                "tx-1", "local-topic:notes", MultiMemoryOutcome.RecoveryRequired, [],
                MultiMemoryFailure.Rollback, null, null))
            .Should().Throw<ArgumentException>();

        FluentActions.Invoking(() => new MultiMemoryAggregateResult(
                "tx-1", "local-topic:notes", MultiMemoryOutcome.RecoveryRequired, [],
                MultiMemoryFailure.Rollback, null, "C:\\foreign\\evidence"))
            .Should().Throw<ArgumentException>();

        var result = new MultiMemoryAggregateResult(
            "tx-1", "local-topic:notes", MultiMemoryOutcome.RecoveryRequired, [],
            MultiMemoryFailure.Rollback, null, "notes/.recovery");

        result.EvidencePath.Should().Be("notes/.recovery");
    }

    [Fact]
    public void CoordinatorRecord_SerializesExactVersionOneShapeAndRoundTrips()
    {
        var created = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var record = new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-1", "local-topic:notes",
            MultiMemoryTransactionState.Prepared,
            [new MultiMemoryParticipantManifest("notes/a", "op-1", "memory")], created, created);

        string json = Encoding.UTF8.GetString(MultiMemoryTransactionRecordCodec.Serialize(record));
        using JsonDocument document = JsonDocument.Parse(json);
        document.RootElement.EnumerateObject().Select(property => property.Name)
            .Should().Equal("format", "version", "transactionId", "scope", "state", "participants", "createdAt", "updatedAt");
        document.RootElement.GetProperty("state").GetString().Should().Be("prepared");
        document.RootElement.GetProperty("participants")[0].EnumerateObject().Select(property => property.Name)
            .Should().Equal("subject", "operationId", "kind");
        json.Should().NotContain("payload").And.NotContain("artifact").And.NotContain("sidecar").And.NotContain("hash");

        MultiMemoryTransactionRecord parsed = MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
            Encoding.UTF8.GetBytes(json), "local-topic:notes", "scope/.multi-memory-transaction.json");
        parsed.Should().BeEquivalentTo(record);
    }

    [Fact]
    public void CoordinatorRecord_RejectsMalformedIdentityOrderingAndScope()
    {
        var created = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var record = new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-1", "local-topic:notes",
            MultiMemoryTransactionState.Prepared,
            [new MultiMemoryParticipantManifest("notes/b", "op-1", "memory"),
             new MultiMemoryParticipantManifest("notes/a", "op-2", "memory")], created, created);

        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.Serialize(record))
            .Should().Throw<MultiMemoryTransactionRecordException>();
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes("{}"), "local-topic:notes", "record.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>();
    }

    [Fact]
    public void CoordinatorRecord_NewerVersionIsRejectedWithoutRewrite()
    {
        const string json = "{\"format\":\"scrinia.multi-memory-transaction\",\"version\":2,\"transactionId\":\"tx-1\",\"scope\":\"local-topic:notes\",\"state\":\"prepared\",\"participants\":[{\"subject\":\"notes/a\",\"operationId\":\"op-1\",\"kind\":\"memory\"}],\"createdAt\":\"2026-09-12T00:00:00Z\",\"updatedAt\":\"2026-09-12T00:00:00Z\"}";
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(json), "local-topic:notes", "record.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>()
            .Which.Failure.Should().Be(MultiMemoryFailure.UnsupportedVersion);
        json.Should().NotBeEmpty();
    }

    [Fact]
    public void CoordinatorRecord_PathIsContainedAndHasPrivateScopeName()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b3-record-tests", Guid.NewGuid().ToString("N"));
        MultiMemoryTransactionRecordCodec.GetPath(root).Should().Be(Path.Combine(root, ".multi-memory-transaction.json"));
    }

    [Fact]
    public void CoordinatorRecord_AtomicWriteRetainsPreviousCompleteBytesWhenReplacementFails()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b3-atomic-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var timestamp = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
            var first = new MultiMemoryTransactionRecord(
                MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-1", "local-topic:notes",
                MultiMemoryTransactionState.Prepared,
                [new MultiMemoryParticipantManifest("notes/a", "op-1", "memory")], timestamp, timestamp);
            var second = MultiMemoryTransactionRecordCodec.WithState(first,
                MultiMemoryTransactionState.Committed, timestamp.AddSeconds(1));
            MultiMemoryTransactionRecordCodec.Write(root, first);
            byte[] original = File.ReadAllBytes(MultiMemoryTransactionRecordCodec.GetPath(root));

            DurableFile.FailureInjector = (_, _) => new IOException("injected replacement failure");
            FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.Write(root, second))
                .Should().Throw<DurableWriteException>();
            DurableFile.FailureInjector = null;

            File.ReadAllBytes(MultiMemoryTransactionRecordCodec.GetPath(root)).Should().Equal(original);
        }
        finally
        {
            DurableFile.FailureInjector = null;
            Directory.Delete(root, recursive: true);
        }
    }

    [Theory]
    [InlineData("version", "0", 8)]
    [InlineData("format", "wrong", 7)]
    [InlineData("scope", "local-topic:other", 10)]
    [InlineData("unknown", "true", 7)]
    public void CoordinatorRecord_RejectsInvalidWireMembers(string member, string value, int failure)
    {
        string json = RecordJson();
        json = member switch
        {
            "version" => json.Replace("\"version\":1", "\"version\":0", StringComparison.Ordinal),
            "format" => json.Replace(MultiMemoryTransactionRecordCodec.FormatName, value, StringComparison.Ordinal),
            "scope" => json.Replace("local-topic:notes", value, StringComparison.Ordinal),
            _ => json.Replace("\"kind\":\"memory\"", "\"kind\":\"memory\",\"unknown\":true", StringComparison.Ordinal)
        };

        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(json), "local-topic:notes", "record.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>()
            .Which.Failure.Should().Be((MultiMemoryFailure)failure);
    }

    [Fact]
    public void CoordinatorRecord_RejectsDuplicateBlankOversizedAndInvalidTimestampClaims()
    {
        string duplicate = RecordJson().Replace(
            "{\"subject\":\"notes/a\",\"operationId\":\"op-1\",\"kind\":\"memory\"}",
            "{\"subject\":\"notes/a\",\"operationId\":\"op-1\",\"kind\":\"memory\"},{\"subject\":\"notes/b\",\"operationId\":\"op-1\",\"kind\":\"memory\"}",
            StringComparison.Ordinal);
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(duplicate), "local-topic:notes", "record.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>();

        string blank = RecordJson().Replace("\"tx-1\"", "\" \"", StringComparison.Ordinal);
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(blank), "local-topic:notes", "record.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>();

        string oversized = RecordJson().Replace("\"tx-1\"", $"\"{new string('x', 513)}\"", StringComparison.Ordinal);
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(oversized), "local-topic:notes", "record.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>();

        string invalidTimestamp = RecordJson().Replace("2026-09-12T00:00:00Z", "2026-09-12T00:00:01Z", StringComparison.Ordinal);
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(invalidTimestamp), "local-topic:notes", "record.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>();
    }

    [Fact]
    public void CoordinatorRecord_RejectsTraversalAndOutOfScopeRecordPaths()
    {
        string json = RecordJson();
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(json), "local-topic:notes", "scope/../.multi-memory-transaction.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>()
            .Which.Failure.Should().Be(MultiMemoryFailure.Unsafe);

        string root = Path.Combine(Path.GetTempPath(), "scrinia-b3-containment", Guid.NewGuid().ToString("N"));
        string foreign = Path.Combine(Path.GetTempPath(), "scrinia-b3-foreign", Guid.NewGuid().ToString("N"),
            MultiMemoryTransactionRecordCodec.FileName);
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(json), "local-topic:notes", foreign, root))
            .Should().Throw<MultiMemoryTransactionRecordException>()
            .Which.Failure.Should().Be(MultiMemoryFailure.Unsafe);

        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(json), "local-topic:notes",
                Path.Combine(Path.GetTempPath(), MultiMemoryTransactionRecordCodec.FileName)))
            .Should().Throw<MultiMemoryTransactionRecordException>()
            .Which.Failure.Should().Be(MultiMemoryFailure.Unsafe);

        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseVerifiedRecord(
                Encoding.UTF8.GetBytes(json), "local-topic:notes", "scope/not-the-coordinator.json", root))
            .Should().Throw<MultiMemoryTransactionRecordException>()
            .Which.Failure.Should().Be(MultiMemoryFailure.Unsafe);
    }

    [Fact]
    public void CoordinatorDiscovery_UsesOneMiBBoundAndClassifiesOversizedRecordAsMalformed()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b3-discovery-bound", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            byte[] oversized = new byte[1024 * 1024 + 1];
            File.WriteAllBytes(MultiMemoryTransactionRecordCodec.GetPath(root), oversized);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator().Discover("local-topic:notes", root);
            result.BlockingEvidence.Should().NotBeNull();
            result.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.Malformed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CoordinatorRead_MissingRecordIsTypedReplayIoFailureWithScope()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b3-read-io", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.Read(root, "local-topic:notes"))
                .Should().Throw<MultiMemoryTransactionRecordException>()
                .Which.Should().Match<MultiMemoryTransactionRecordException>(exception =>
                    exception.Failure == MultiMemoryFailure.ReplayIoFailed &&
                    exception.Scope == "local-topic:notes");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void CoordinatorDiscovery_ClassifiesReadIoFailureAsReplayIoFailed()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b3-discovery-io", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(MultiMemoryTransactionRecordCodec.GetPath(root), Encoding.UTF8.GetBytes(RecordJson()));
            using FileStream exclusive = new(MultiMemoryTransactionRecordCodec.GetPath(root), FileMode.Open,
                FileAccess.Read, FileShare.None);

            RecoveryDiscoveryResult result = new RecoveryDiscoveryCoordinator().Discover("local-topic:notes", root);
            result.BlockingEvidence.Should().NotBeNull();
            result.BlockingEvidence!.Reason.Should().Be(RecoveryBlockReason.ReplayIoFailed);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string RecordJson()
    {
        var timestamp = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        return Encoding.UTF8.GetString(MultiMemoryTransactionRecordCodec.Serialize(new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-1", "local-topic:notes",
            MultiMemoryTransactionState.Prepared,
            [new MultiMemoryParticipantManifest("notes/a", "op-1", "memory")], timestamp, timestamp)));
    }
}
