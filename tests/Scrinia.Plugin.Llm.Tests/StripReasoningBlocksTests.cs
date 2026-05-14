using FluentAssertions;
using Scrinia.Plugin.Llm;

namespace Scrinia.Plugin.Llm.Tests;

/// <summary>
/// Verifies <see cref="VulkanLlmProvider.StripReasoningBlocks"/> handles the common
/// chain-of-thought wrappers emitted by LFM2.5-Thinking, DeepSeek-R1, Qwen-QwQ, etc.
/// </summary>
public class StripReasoningBlocksTests
{
    [Fact]
    public void StripsThinkBlock_AndKeepsAnswer()
    {
        string raw = "<think>The user wants a sentence.</think>A short note about auth.";
        VulkanLlmProvider.StripReasoningBlocks(raw).Trim()
            .Should().Be("A short note about auth.");
    }

    [Fact]
    public void StripsReasoningBlock_WithSpecialTokens()
    {
        string raw = "<|reasoning|>Step 1, step 2.<|/reasoning|>Final answer here.";
        VulkanLlmProvider.StripReasoningBlocks(raw).Trim()
            .Should().Be("Final answer here.");
    }

    [Fact]
    public void StripsMultilineThinking()
    {
        string raw = "<think>\nLong\nreasoning\nover\nmany\nlines\n</think>\nAnswer.";
        VulkanLlmProvider.StripReasoningBlocks(raw).Trim().Should().Be("Answer.");
    }

    [Fact]
    public void DropsUnclosedThinkingBlock_FromOpenTagOnward()
    {
        // Max-tokens truncation: model started thinking and ran out. Drop the partial reasoning
        // so it doesn't pollute the description; caller treats the empty result as skip-and-continue.
        string raw = "<think>The user is asking about authentic";
        VulkanLlmProvider.StripReasoningBlocks(raw).Should().BeEmpty();
    }

    [Fact]
    public void DropsUnclosedReasoningBlock_FromOpenTagOnward()
    {
        string raw = "Pre-text <|reasoning|>truncated mid-thought";
        VulkanLlmProvider.StripReasoningBlocks(raw).Trim().Should().Be("Pre-text");
    }

    [Fact]
    public void NoOp_WhenInputHasNoReasoningMarkers()
    {
        string raw = "Just a plain completion with no thinking blocks.";
        VulkanLlmProvider.StripReasoningBlocks(raw).Should().Be(raw);
    }

    [Fact]
    public void HandlesMultipleThinkBlocks()
    {
        string raw = "<think>first</think>between<think>second</think>final";
        VulkanLlmProvider.StripReasoningBlocks(raw).Trim().Should().Be("betweenfinal");
    }
}
