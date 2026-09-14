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

    [Fact]
    public void B4_enum_wire_values_are_case_sensitive()
    {
        var json = Encoding.UTF8.GetString(MultiMemoryTransactionRecordCodec.Serialize(NewRecord()));
        json = json.Replace("\"prepared\"", "\"Prepared\"", StringComparison.Ordinal);
        FluentActions.Invoking(() => MultiMemoryTransactionRecordCodec.ParseAndValidate(
                Encoding.UTF8.GetBytes(json), "local-topic:notes", "scope/.multi-memory-transaction.json"))
            .Should().Throw<MultiMemoryTransactionRecordException>();
    }

    private static MultiMemoryTransactionRecord NewRecord() => new(
        MultiMemoryTransactionRecordCodec.FormatName, 1, "tx-1", "local-topic:notes",
        MultiMemoryTransactionState.Prepared,
        [new MultiMemoryParticipantManifest("notes/a", "op-1", "memory")],
        new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
        new DateTimeOffset(2026, 9, 12, 0, 0, 0, TimeSpan.Zero),
        transactionalParticipants: [NewParticipant(TransactionalParticipantLifecycleState.Prepared, "handle/1", "v1", false, false)]);

    private static B4ParticipantRecoveryState NewParticipant(
        TransactionalParticipantLifecycleState state, string? handle, string? version, bool changed, bool ambiguous) =>
        new("tx-1", "op-1", "local-topic:notes", "/notes/a", "participant", 1, "participant-key", "memory",
            ["alpha", "zeta"], new Dictionary<string, string> { ["body"] = "value" },
            new Dictionary<string, string> { ["source"] = "test" }, state, handle, version, 0, 0, changed, ambiguous,
            null, null, null, null, TransactionalQuarantineState.None, null);
}
