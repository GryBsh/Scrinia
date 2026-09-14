using FluentAssertions;
using System.Text;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

public sealed partial class MultiMemoryTransactionContractTests
{
    [Fact]
    public void B4_record_round_trip_uses_canonical_durable_json()
    {
        var timestamp = new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero);
        var participant = new B4ParticipantRecoveryState(
            "tx-1", "op-1", "local-topic:notes", "/notes/a", "participant", 1, "participant-key", "memory",
            ["alpha", "zeta"], new Dictionary<string, string> { ["body"] = "value" },
            new Dictionary<string, string> { ["source"] = "test" },
            TransactionalParticipantLifecycleState.Prepared, "handle/1", "v1", 0, 0, false, false,
            null, null, null, null, TransactionalQuarantineState.None, null);
        var record = new MultiMemoryTransactionRecord(
            MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-1", "local-topic:notes",
            MultiMemoryTransactionState.Prepared,
            [new MultiMemoryParticipantManifest("notes/a", "op-1", "memory")], timestamp, timestamp,
            transactionalParticipants: [participant]);

        var json = MultiMemoryTransactionRecordCodec.Serialize(record);
        var parsed = MultiMemoryTransactionRecordCodec.ParseAndValidate(
            json, "local-topic:notes", "scope/.multi-memory-transaction.json");

        parsed.TransactionalParticipants.Should().ContainSingle();
        Encoding.UTF8.GetString(json).Should().Contain("transactionalParticipants");
        Encoding.UTF8.GetString(json).Should().Contain("participant-key");
    }

    [Fact]
    public void B4_lifecycle_requires_truth_table_consistency()
    {
        FluentActions.Invoking(() => NewParticipant(
            TransactionalParticipantLifecycleState.Prepared, null, null, false, false))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => NewParticipant(
            TransactionalParticipantLifecycleState.Committed, null, null, false, false))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => NewParticipant(
            TransactionalParticipantLifecycleState.Ambiguous, "handle/1", "v1", false, true))
            .Should().Throw<ArgumentException>();
    }

    [Theory]
    [InlineData(0, true, false, false)]
    [InlineData(1, true, false, false)]
    [InlineData(2, false, false, false)]
    [InlineData(3, false, true, false)]
    [InlineData(4, false, true, true)]
    public void B4_lifecycle_accepts_representative_valid_matrix_rows(
        int state, bool hasHandle, bool changed, bool ambiguous)
    {
        FluentActions.Invoking(() => NewParticipant((TransactionalParticipantLifecycleState)state, hasHandle ? "handle/1" : null,
            hasHandle ? "v1" : null, changed, ambiguous)).Should().NotThrow();
    }

    [Fact]
    public void B4_enum_wire_values_are_case_sensitive()
    {
        var json = Encoding.UTF8.GetString(MultiMemoryTransactionRecordCodec.Serialize(NewRecord()));
        json = json.Replace("\"prepared\"", "\"Prepared\"", StringComparison.Ordinal);
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseAndValidate(
                Encoding.UTF8.GetBytes(json), "local-topic:notes", "scope/.multi-memory-transaction.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>();
    }

    [Fact]
    public void B4_write_expected_rejects_every_mutated_admitted_snapshot_field()
    {
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b4-snapshot-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var expected = NewRecord();
            MultiMemoryTransactionRecordCodec.Write(root, expected);
            var original = expected.TransactionalParticipants![0];
            var mutations = new[]
            {
                NewParticipantWith(original, address: "/notes/other"),
                NewParticipantWith(original, operationId: "op-other"),
                NewParticipantWith(original, kind: "other-kind"),
                NewParticipantWith(original, participantIdentity: "participant-other"),
                NewParticipantWith(original, orderingKey: "other-key"),
                NewParticipantWith(original, capabilities: ["beta"]),
                NewParticipantWith(original, payload: new Dictionary<string, string> { ["body"] = "other" }),
                NewParticipantWith(original, metadata: new Dictionary<string, string> { ["source"] = "other" })
            };

            foreach (var mutation in mutations.Skip(1))
            {
                var replacement = new MultiMemoryTransactionRecord(
                    expected.Format, expected.Version, expected.TransactionId, expected.Scope, expected.State,
                    expected.Participants, expected.CreatedAt, expected.UpdatedAt,
                    transactionalParticipants: [mutation]);
                FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.WriteExpected(root, expected, replacement))
                    .Should().Throw<MultiMemoryTransactionRecordException>()
                    .Which.Failure.Should().Be(MultiMemoryFailure.Inconsistent);
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void B4_write_expected_rejects_cross_list_transactional_collisions()
    {
        var expected = NewRecord();
        var rejected = new B4ParticipantRequestMetadata("tx-1", "local-topic:notes", "/notes/a", "participant",
            1, "participant-key", "op-1", "memory", TransactionalFailureCode.Veto, "rejected");
        var replacement = new MultiMemoryTransactionRecord(expected.Format, expected.Version, expected.TransactionId,
            expected.Scope, expected.State, expected.Participants, expected.CreatedAt, expected.UpdatedAt,
            transactionalParticipants: expected.TransactionalParticipants, rejectedTransactionalParticipants: [rejected]);

        string root = Path.Combine(Path.GetTempPath(), "scrinia-b4-collision-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            MultiMemoryTransactionRecordCodec.Write(root, expected);
            FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.WriteExpected(root, expected, replacement))
                .Should().Throw<MultiMemoryTransactionRecordException>();
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void B4_write_expected_rejects_mutated_rejected_snapshot_fields()
    {
        var expected = NewRecordWithRejected();
        var original = expected.RejectedTransactionalParticipants![0];
        var mutations = new[]
        {
            NewRejectedWith(original, address: "/notes/other"),
            NewRejectedWith(original, participantIdentity: "other"),
            NewRejectedWith(original, orderingKey: "other-key"),
            NewRejectedWith(original, operationId: "other-op"),
            NewRejectedWith(original, kind: "other-kind"),
            NewRejectedWith(original, rejectionCode: TransactionalFailureCode.Permanent),
            NewRejectedWith(original, rejectionDetail: "other")
        };
        string root = Path.Combine(Path.GetTempPath(), "scrinia-b4-rejected-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            MultiMemoryTransactionRecordCodec.Write(root, expected);
            foreach (var mutation in mutations)
            {
                var replacement = new MultiMemoryTransactionRecord(expected.Format, expected.Version, expected.TransactionId,
                    expected.Scope, expected.State, expected.Participants, expected.CreatedAt, expected.UpdatedAt,
                    transactionalParticipants: expected.TransactionalParticipants, rejectedTransactionalParticipants: [mutation]);
                FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.WriteExpected(root, expected, replacement))
                    .Should().Throw<MultiMemoryTransactionRecordException>();
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static MultiMemoryTransactionRecord NewRecord() => new(
        MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-1", "local-topic:notes",
        MultiMemoryTransactionState.Prepared,
        [new MultiMemoryParticipantManifest("notes/a", "op-1", "memory")],
        new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
        transactionalParticipants: [NewParticipant(TransactionalParticipantLifecycleState.Prepared, "handle/1", "v1", false, false)]);

    private static MultiMemoryTransactionRecord NewRecordWithRejected()
    {
        var record = NewRecord();
        return new MultiMemoryTransactionRecord(record.Format, record.Version, record.TransactionId, record.Scope, record.State,
            record.Participants, record.CreatedAt, record.UpdatedAt, transactionalParticipants: record.TransactionalParticipants,
            rejectedTransactionalParticipants: [new B4ParticipantRequestMetadata("tx-1", "local-topic:notes", "/notes/b",
                "rejected", 1, "rejected-key", "op-2", "memory", TransactionalFailureCode.Veto, "rejected")]);
    }

    private static B4ParticipantRecoveryState NewParticipant(
        TransactionalParticipantLifecycleState state, string? handle, string? version, bool changed, bool ambiguous) =>
        new("tx-1", "op-1", "local-topic:notes", "/notes/a", "participant", 1, "participant-key", "memory",
            ["alpha", "zeta"], new Dictionary<string, string> { ["body"] = "value" },
            new Dictionary<string, string> { ["source"] = "test" }, state, handle, version, 0, 0, changed, ambiguous,
            null, null, null, null, TransactionalQuarantineState.None, null);

    private static B4ParticipantRecoveryState NewParticipantWith(B4ParticipantRecoveryState value,
        string? address = null, string? participantIdentity = null, string? orderingKey = null,
        string? operationId = null, string? kind = null, int? contractVersion = null,
        IReadOnlyList<string>? capabilities = null, IReadOnlyDictionary<string, string>? payload = null,
        IReadOnlyDictionary<string, string>? metadata = null) =>
        new(value.TransactionId, operationId ?? value.OperationId, value.Scope, address ?? value.Address,
            participantIdentity ?? value.ParticipantIdentity, contractVersion ?? value.ContractVersion, orderingKey ?? value.OrderingKey, kind ?? value.Kind,
            capabilities ?? value.Capabilities, payload ?? value.Payload, metadata ?? value.Metadata, value.LifecycleState,
            value.PreparedHandleReference, value.PreparedHandleVersion, value.RetryAttempt, value.RetryElapsedMilliseconds,
            value.StateMayHaveChanged, value.Ambiguous, value.LastFailureCode, value.EvidenceReference, value.EvidenceKind,
            value.EvidenceVersion, value.QuarantineState, value.QuarantineReason);

    private static B4ParticipantRequestMetadata NewRejectedWith(B4ParticipantRequestMetadata value,
        string? address = null, string? participantIdentity = null, string? orderingKey = null,
        string? operationId = null, string? kind = null, TransactionalFailureCode? rejectionCode = null,
        string? rejectionDetail = null) =>
        new(value.TransactionId, value.Scope, address ?? value.Address, participantIdentity ?? value.ParticipantIdentity,
            value.ContractVersion, orderingKey ?? value.OrderingKey, operationId ?? value.OperationId, kind ?? value.Kind,
            rejectionCode ?? value.RejectionCode, rejectionDetail ?? value.RejectionDetail);
}
