using FluentAssertions;
using Scrinia.Core.Llm;

namespace Scrinia.Tests.Llm;

/// <summary>
/// Tests for <see cref="LlmPrompts.ParseCombinedJson"/>. The combined-call response
/// parser is intentionally tolerant of common small-model malformations (markdown
/// fences, trailing prose, missing fields) — Tier 2 should never fall over on a
/// recoverable response shape.
/// </summary>
public sealed class LlmPromptsParseCombinedJsonTests
{
    [Fact]
    public void Valid_BothFieldsPopulated()
    {
        string raw = """{"description":"A short description.","facts":["fact one","fact two","fact three"]}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().Be("A short description.");
        result.Facts.Should().NotBeNull();
        result.Facts!.Length.Should().Be(3);
    }

    [Fact]
    public void MarkdownFenced_Strips_AndParses()
    {
        string raw = """
            ```json
            {"description":"D","facts":["fact one"]}
            ```
            """;

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().Be("D");
        result.Facts!.Single().Should().Be("fact one");
    }

    [Fact]
    public void MarkdownFenced_NoLanguageTag_StillStrips()
    {
        string raw = "```\n{\"description\":\"D\",\"facts\":[\"a fact\"]}\n```";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().Be("D");
    }

    [Fact]
    public void LeadingTrailingProse_FindsObject()
    {
        string raw = "Sure, here you go:\n\n{\"description\":\"D\",\"facts\":[\"one fact\"]}\n\nLet me know if this helps.";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().Be("D");
    }

    [Fact]
    public void MissingFactsField_ReturnsDescriptionOnly()
    {
        // Partial-success path: combined returned a description but no facts.
        // Caller's per-op fallback will dispatch the facts call independently.
        string raw = """{"description":"A description"}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().Be("A description");
        result.Facts.Should().BeNull();
    }

    [Fact]
    public void MissingDescriptionField_ReturnsFactsOnly()
    {
        string raw = """{"facts":["alpha bravo","charlie delta echo"]}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().BeNull();
        result.Facts.Should().NotBeNull();
        result.Facts!.Length.Should().Be(2);
    }

    [Fact]
    public void EmptyFactsArray_TreatsAsNull()
    {
        // Matches per-op null semantics: empty facts is "no extractable facts," which
        // the consolidator must treat as null (leave field unscored) not [] (overwrite).
        string raw = """{"description":"D","facts":[]}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().Be("D");
        result.Facts.Should().BeNull();
    }

    [Fact]
    public void EmptyDescriptionString_TreatsAsNull()
    {
        string raw = """{"description":"","facts":["valid fact one"]}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Description.Should().BeNull();
        result.Facts.Should().NotBeNull();
    }

    [Fact]
    public void TooShortFact_Skipped()
    {
        // Reuses ParseFacts' 5-char min so combined and line-mode pipelines accept
        // the same fact shape — a 3-char fact like "yes" is below the floor.
        string raw = """{"description":"D","facts":["yes","valid fact longer than 5 chars"]}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Facts!.Length.Should().Be(1);
        result.Facts[0].Should().Be("valid fact longer than 5 chars");
    }

    [Fact]
    public void Malformed_ReturnsNull()
    {
        string raw = "{description: This is not JSON, just looks-ish";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().BeNull();
    }

    [Fact]
    public void NoBraceAtAll_ReturnsNull()
    {
        string raw = "I apologize, I can't help with that.";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void NullOrEmpty_ReturnsNull(string? raw)
    {
        LlmPrompts.ParseCombinedJson(raw).Should().BeNull();
    }

    [Fact]
    public void NonStringFactItems_AreSkipped()
    {
        // Defensive: model emits a mixed array. We accept the string items, drop the rest.
        string raw = """{"description":"D","facts":["good fact one",42,null,"good fact two"]}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().NotBeNull();
        result!.Facts!.Length.Should().Be(2);
        result.Facts.Should().Contain("good fact one");
        result.Facts.Should().Contain("good fact two");
    }

    [Fact]
    public void BothFieldsEmpty_ReturnsNull()
    {
        // Empty everything = nothing extracted = null. Caller treats as "no enrichment."
        string raw = """{"description":"","facts":[]}""";

        var result = LlmPrompts.ParseCombinedJson(raw);

        result.Should().BeNull();
    }
}
