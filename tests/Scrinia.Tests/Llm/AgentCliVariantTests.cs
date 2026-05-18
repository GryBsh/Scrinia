using FluentAssertions;
using Scrinia.Core.Llm;

namespace Scrinia.Tests.Llm;

/// <summary>
/// Direct coverage of <see cref="AgentCliVariant.BuildArguments"/> so the override
/// semantics (null = variant default, empty = suppress, value = pass-through) are
/// pinned independently of the <see cref="AgentCliBackgroundLlm"/> pipeline.
/// </summary>
public class AgentCliVariantTests
{
    [Fact]
    public void Claude_DefaultsAppendSonnetAndLow()
    {
        AgentCliVariant.ClaudeCli
            .BuildArguments(modelOverride: null, effortOverride: null)
            .Should().Equal("-p", "--model", "sonnet", "--effort", "low");
    }

    [Fact]
    public void Codex_DefaultAppendsMiniModel_NoEffortFlag()
    {
        AgentCliVariant.CodexCli
            .BuildArguments(modelOverride: null, effortOverride: null)
            .Should().Equal("exec", "-", "-m", "gpt-5.4-mini");
    }

    [Fact]
    public void Copilot_DefaultsAppendMiniModelAndLow()
    {
        AgentCliVariant.CopilotCli
            .BuildArguments(modelOverride: null, effortOverride: null)
            .Should().Equal("--print", "--model", "gpt-5.4-mini", "--effort", "low");
    }

    [Fact]
    public void ExplicitOverride_ReplacesDefault()
    {
        AgentCliVariant.ClaudeCli
            .BuildArguments(modelOverride: "haiku", effortOverride: "max")
            .Should().Equal("-p", "--model", "haiku", "--effort", "max");
    }

    [Fact]
    public void EmptyStringOverride_SuppressesFlag()
    {
        // Empty = "inherit CLI default" — both flags drop.
        AgentCliVariant.ClaudeCli
            .BuildArguments(modelOverride: "", effortOverride: "")
            .Should().Equal("-p");
    }

    [Fact]
    public void MixedOverrides_OneSuppressedOneOverridden()
    {
        AgentCliVariant.ClaudeCli
            .BuildArguments(modelOverride: "", effortOverride: "high")
            .Should().Equal("-p", "--effort", "high");
    }

    [Fact]
    public void Codex_EffortOverride_IsSilentlyIgnored_VariantHasNoFlag()
    {
        // CodexCli has no EffortFlag — passing an override must not produce a bogus argv.
        AgentCliVariant.CodexCli
            .BuildArguments(modelOverride: "gpt-5.5", effortOverride: "high")
            .Should().Equal("exec", "-", "-m", "gpt-5.5");
    }
}
