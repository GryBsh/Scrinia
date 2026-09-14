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
}
