using FluentAssertions;
using Scrinia.Core.Llm;
using Scrinia.Core.Process;

namespace Scrinia.Tests.Llm;

/// <summary>
/// Tests for <see cref="AgentCliBackgroundLlm"/>. Uses a fake <see cref="IProcessRunner"/>
/// so no actual CLI is launched and the invocation shape (exe, args, stdin) can be
/// asserted directly.
/// </summary>
public class AgentCliBackgroundLlmTests
{
    private static LlmOptions Options(int timeoutSeconds = 30) => new()
    {
        Model = "irrelevant-for-cli",
        Temperature = 0.1,
        RequestTimeoutSeconds = timeoutSeconds,
    };

    [Fact]
    public async Task IsAvailable_True_WhenCliResolvesOnPath()
    {
        var runner = new FakeProcessRunner(resolved: "/usr/local/bin/claude");
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeTrue();
        runner.ResolveCalls.Should().ContainSingle().Which.Should().Be("claude");
    }

    [Fact]
    public async Task IsAvailable_False_WhenCliMissing()
    {
        var runner = new FakeProcessRunner(resolved: null);
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        (await llm.IsAvailableAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task GenerateDescription_ShellsOutWithPrintFlag_AndReturnsCleanedStdout()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/local/bin/claude",
            result: new ProcessResult(0, "A concise description of the memory.\n", "", false));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        string? desc = await llm.GenerateDescriptionAsync("any content", CancellationToken.None);

        desc.Should().Be("A concise description of the memory.");
        runner.RunCalls.Should().ContainSingle();
        var (exe, args, stdin, _) = runner.RunCalls[0];
        exe.Should().Be("/usr/local/bin/claude");
        // Defaults pin Tier 2 to the cheap tier (sonnet + low effort).
        args.Should().Equal("-p", "--model", "sonnet", "--effort", "low");
        stdin.Should().NotBeNullOrEmpty();
        // System prompt content leaks into stdin — assert the prompt's content marker
        // rather than a specific phrase, since the prompt wording is tuned over time.
        stdin.Should().Contain("Content:");
    }

    [Fact]
    public async Task Codex_VariantUsesExecArgs()
    {
        var runner = new FakeProcessRunner(
            resolved: "/opt/codex",
            result: new ProcessResult(0, "ok response\n", "", false));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.CodexCli, runner, Options());

        await llm.GenerateDescriptionAsync("anything", CancellationToken.None);

        var (exe, args, _, _) = runner.RunCalls[0];
        exe.Should().Be("/opt/codex");
        // Codex has no reasoning-effort knob, so only the model flag is appended.
        args.Should().Equal("exec", "-", "-m", "gpt-5.4-mini");
    }

    [Fact]
    public async Task Copilot_VariantUsesPrintArg()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/bin/copilot",
            result: new ProcessResult(0, "hello\n", "", false));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.CopilotCli, runner, Options());

        await llm.GenerateDescriptionAsync("x", CancellationToken.None);

        runner.RunCalls[0].Args.Should().Equal("--print", "--model", "gpt-5.4-mini", "--effort", "low");
    }

    [Fact]
    public async Task LlmOptions_ModelOverride_ReplacesVariantDefault()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/local/bin/claude",
            result: new ProcessResult(0, "ok\n", "", false));
        var opts = Options();
        opts.ClaudeCliModel = "haiku";
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, opts);

        await llm.GenerateDescriptionAsync("x", CancellationToken.None);

        runner.RunCalls[0].Args.Should().Equal("-p", "--model", "haiku", "--effort", "low");
    }

    [Fact]
    public async Task LlmOptions_EffortOverride_ReplacesVariantDefault()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/local/bin/claude",
            result: new ProcessResult(0, "ok\n", "", false));
        var opts = Options();
        opts.ClaudeCliEffort = "high";
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, opts);

        await llm.GenerateDescriptionAsync("x", CancellationToken.None);

        runner.RunCalls[0].Args.Should().Equal("-p", "--model", "sonnet", "--effort", "high");
    }

    [Fact]
    public async Task LlmOptions_EmptyModelOverride_SuppressesFlag_InheritsCliDefault()
    {
        // Empty-string override is the explicit way to say "leave model selection to the
        // host CLI" — distinct from null, which means "use the variant's cost-cap default".
        var runner = new FakeProcessRunner(
            resolved: "/usr/local/bin/claude",
            result: new ProcessResult(0, "ok\n", "", false));
        var opts = Options();
        opts.ClaudeCliModel = "";
        opts.ClaudeCliEffort = "";
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, opts);

        await llm.GenerateDescriptionAsync("x", CancellationToken.None);

        runner.RunCalls[0].Args.Should().Equal("-p");
    }

    [Fact]
    public async Task Codex_PerCliEffortOptionsAreIgnored_NoSuchFlag()
    {
        // Codex has no effort knob. Even if other CLIs' effort overrides happen to be set
        // in the same LlmOptions instance, Codex's argv must not pick them up.
        var runner = new FakeProcessRunner(
            resolved: "/opt/codex",
            result: new ProcessResult(0, "ok\n", "", false));
        var opts = Options();
        opts.ClaudeCliEffort = "max";    // belongs to a different variant
        opts.CopilotCliEffort = "high";  // belongs to a different variant
        opts.CodexCliModel = "gpt-5.5";  // legit codex override
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.CodexCli, runner, opts);

        await llm.GenerateDescriptionAsync("x", CancellationToken.None);

        runner.RunCalls[0].Args.Should().Equal("exec", "-", "-m", "gpt-5.5");
    }

    [Fact]
    public async Task NonZeroExit_ReturnsNull()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/bin/claude",
            result: new ProcessResult(1, "", "Error: rate limited\n", false));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task Timeout_ReturnsNull()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/bin/claude",
            result: new ProcessResult(-1, "partial...", "", TimedOut: true));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task EmptyStdout_ReturnsNull()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/bin/claude",
            result: new ProcessResult(0, "   \n   ", "", false));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task StripsAnsiEscapes_FromCliWithColorization()
    {
        // Some local CLI configs leak ANSI escapes into print mode (notably claude when
        // attached to a TTY in some shells). The cleaner must drop them.
        var runner = new FakeProcessRunner(
            resolved: "/usr/bin/claude",
            result: new ProcessResult(0, "\x1B[32mGreen text\x1B[0m response", "", false));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        string? result = await llm.GenerateDescriptionAsync("x", CancellationToken.None);

        result.Should().Be("Green text response");
    }

    [Fact]
    public async Task ExtractFacts_ParsesPlainList()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/bin/claude",
            result: new ProcessResult(0,
                "- First atomic fact.\n- Second atomic fact.\n- Third atomic fact.\n",
                "", false));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        string[]? facts = await llm.ExtractFactsAsync("x", CancellationToken.None);

        facts.Should().NotBeNull();
        facts!.Should().HaveCount(3);
        facts.Should().Contain("First atomic fact.");
    }

    [Fact]
    public async Task ProcessFailsToStart_ReturnsNull()
    {
        var runner = new FakeProcessRunner(
            resolved: "/usr/bin/claude",
            throwOnRun: new InvalidOperationException("cannot start"));
        var llm = new AgentCliBackgroundLlm(AgentCliVariant.ClaudeCli, runner, Options());

        (await llm.GenerateDescriptionAsync("x", CancellationToken.None)).Should().BeNull();
    }

    /// <summary>
    /// Fake <see cref="IProcessRunner"/> capturing invocations for assertion.
    /// </summary>
    private sealed class FakeProcessRunner : IProcessRunner
    {
        private readonly string? _resolved;
        private readonly ProcessResult? _result;
        private readonly Exception? _throwOnRun;

        public List<string> ResolveCalls { get; } = [];
        public List<(string Exe, IReadOnlyList<string> Args, string? Stdin, CancellationToken Ct)> RunCalls { get; } = [];

        public FakeProcessRunner(string? resolved = null, ProcessResult? result = null, Exception? throwOnRun = null)
        {
            _resolved = resolved;
            _result = result;
            _throwOnRun = throwOnRun;
        }

        public Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> arguments, string? stdin, CancellationToken ct)
        {
            RunCalls.Add((executable, arguments, stdin, ct));
            if (_throwOnRun is not null) throw _throwOnRun;
            return Task.FromResult(_result ?? new ProcessResult(0, "", "", false));
        }

        public bool IsExecutableOnPath(string executable) => ResolveExecutable(executable) is not null;

        public string? ResolveExecutable(string executable)
        {
            ResolveCalls.Add(executable);
            return _resolved;
        }
    }
}
