using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public sealed class TransactionalParticipantRegistryTests
{
    [Fact]
    public void Transactional_request_copies_and_orders_its_durable_input()
    {
        var payload = new Dictionary<string, string> { ["body"] = "value" };
        var metadata = new Dictionary<string, string> { ["source"] = "test" };
        var participant = new MultiMemoryTransactionalParticipantRequest(
            MemoryAddress.Parse("/notes/a"), "participant", 1, "participant-key", "op-1", "memory",
            payload, metadata);

        payload["body"] = "changed";
        metadata.Clear();

        var request = new MultiMemoryTransactionRequest("local-topic:notes",
            [new MultiMemoryParticipantRequest(MemoryAddress.Parse("/notes/b"), "op-b", "memory")], [participant]);
        request.TransactionalParticipants.Should().ContainSingle();
        request.TransactionalParticipants[0].Payload["body"].Should().Be("value");
        request.TransactionalParticipants[0].Metadata["source"].Should().Be("test");
    }
}
