using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Agent;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class AgenticIngestPromptsTests
{
    [Fact]
    public void SystemPrompt_IncludesGuideAndRefInstruction()
    {
        string sys = AgenticIngestPrompts.SystemPrompt();
        sys.Should().Contain("ref:", "memories must cite source doc ids for provenance scoring");
        sys.Length.Should().BeGreaterThan(200, "the real guide text is folded in");
    }

    [Fact]
    public void SessionUserMessage_LabelsEachTurnWithDocId()
    {
        var docs = new List<HaystackDoc>
        {
            new("conv5_sess3_msg1", "Maria: We booked Lisbon.", Timestamp: null, SessionId: "conv5_sess3"),
            new("conv5_sess3_msg2", "Sam: Nice, when?", Timestamp: null, SessionId: "conv5_sess3"),
        };
        string msg = AgenticIngestPrompts.SessionUserMessage(docs);
        msg.Should().Contain("[conv5_sess3_msg1]").And.Contain("[conv5_sess3_msg2]");
        msg.Should().Contain("We booked Lisbon");
    }

    [Fact]
    public void Tools_ExposeRememberAndSearch_WithValidJsonSchemas()
    {
        var tools = AgenticIngestPrompts.Tools();
        tools.Select(t => t.Name).Should().Contain(new[] { "memory_remember", "memory_search" });
        foreach (var t in tools)
        {
            var act = () => System.Text.Json.JsonDocument.Parse(t.JsonSchemaParameters);
            act.Should().NotThrow($"{t.Name} parameters must be valid JSON schema");
        }
    }
}
