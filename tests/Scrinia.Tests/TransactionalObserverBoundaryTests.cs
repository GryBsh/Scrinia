using System.Reflection;
using FluentAssertions;
using Scrinia.Commands;
using Scrinia.Core;
using Scrinia.Mcp;
using Scrinia.Services;

namespace Scrinia.Tests;

public sealed class TransactionalObserverBoundaryTests
{
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("warning\rtext")]
    [InlineData("warning\ntext")]
    [InlineData("warning\ttext")]
    [InlineData("warning\u000Btext")]
    [InlineData("warning\u0085text")]
    [InlineData("warning\u00A0text")]
    [InlineData("warning\u2007text")]
    [InlineData("C:\\foreign\\secret.json")]
    [InlineData("../foreign/path")]
    [InlineData("https://example.test/payload")]
    [InlineData("System.InvalidOperationException: secret payload")]
    [InlineData("raw sidecar payload")]
    [InlineData("mailto:user")]
    [InlineData("urn:item")]
    [InlineData("C:relative")]
    public void StableDetailSanitizerRejectsUnsafeCrossingValues(string? unsafeDetail)
    {
        InvokeSanitize(unsafeDetail).Should().BeNull();
    }

    [Fact]
    public void StableDetailSanitizerPreservesOnlyStableDetailText()
    {
        InvokeSanitize("participant cleanup failed: retry-2").Should().Be("participant cleanup failed: retry-2");
    }

    [Theory]
    [InlineData("provider: ready")]
    [InlineData("retry: exhausted")]
    public void StableDetailSanitizerPreservesReadableColonDiagnostics(string detail)
    {
        InvokeSanitize(detail).Should().Be(detail);
    }

    [Theory]
    [InlineData("mailto:user")]
    [InlineData("urn:item")]
    [InlineData("C:relative")]
    public void MemoryAddressRejectsColonOnlyUriAndDriveRelativeForms(string raw)
    {
        MemoryAddress.TryParse(raw, out _, out _).Should().BeFalse();
    }

    [Fact]
    public void FacadesShareTheStableDetailBoundaryAndKeepEvidenceFilteringSeparate()
    {
        MultiMemoryAggregateResult result = Aggregate(
            MultiMemoryOutcome.RecoveryRequired,
            MultiMemoryFailure.Ambiguous,
            "C:\\secret\\payload.json");

        McpResponse mcp = ScriniaMcpTools.MapMultiMemoryResult(result, "multi-memory");
        McpResponse cli = ScriniaCommands.MapMultiMemoryResult(result, "multi-memory");

        mcp.Should().BeEquivalentTo(cli);
        mcp.ToYaml().Should().NotContain("C:\\secret\\payload.json");
        mcp.ToYaml().Should().Contain("reason: ambiguous");
        mcp.ToYaml().Should().Contain("evidence: evidence.json");
    }

    [Fact]
    public async Task ObserverFailureDoesNotBlockTheNextObserver()
    {
        var recorder = new RecordingSink();
        var composite = new CompositeEventSink([new ThrowingSink(), recorder]);

        await composite.OnStoredAsync(MemoryAddress.Parse("/note"), ["content"], null!, default);

        recorder.StoredCount.Should().Be(1);
    }

    [Fact]
    public async Task OperationAwareObserverFailureDoesNotAlterOutcomeOrBlockNextObserver()
    {
        var recorder = new RecordingSink();
        var composite = new CompositeEventSink([new ThrowingOperationAwareSink(), recorder]);

        var act = () => composite.OnAppendedAsync(
            MemoryAddress.Parse("/note"), "content", null!, "operation-1", default);

        await act.Should().NotThrowAsync();
        recorder.AppendedCount.Should().Be(1);
    }

    [Fact]
    public void ObserverDiagnosticsUseStableFallbackForUnsafeChildStderr()
    {
        McpPluginHostTestBridge.FormatStderr("C:\\secret\\payload")
            .Should().EndWith("observer diagnostic omitted");
        McpPluginHostTestBridge.FormatStderr(" ").Should().EndWith("observer diagnostic omitted");
        McpPluginHostTestBridge.FormatStderr("provider ready").Should().EndWith("provider ready");
    }

    private static string? InvokeSanitize(string? detail)
    {
        Type type = typeof(MemoryAddress).Assembly.GetType("Scrinia.Core.MultiMemoryStableDetail")
            ?? throw new InvalidOperationException("Stable-detail sanitizer type is missing.");
        MethodInfo method = type.GetMethod("Sanitize", BindingFlags.Static | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Stable-detail sanitizer method is missing.");
        return (string?)method.Invoke(null, [detail]);
    }

    private static MultiMemoryAggregateResult Aggregate(
        MultiMemoryOutcome outcome, MultiMemoryFailure failure, string warning) => new(
        "tx-1", "local:notes", outcome,
        [new MultiMemoryParticipantOutcome(
            "note-a", "op-a", "memory", "evidence.json", false, false,
            "participant-specific")],
        failure, warning, "nested/evidence.json");

    private sealed class RecordingSink : IMemoryEventSink
    {
        public int StoredCount { get; private set; }
        public int AppendedCount { get; private set; }

        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct)
        {
            StoredCount++;
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            IncrementAppendedCount();

        private Task IncrementAppendedCount()
        {
            AppendedCount++;
            return Task.CompletedTask;
        }

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class ThrowingOperationAwareSink : IMemoryEventSink, IOperationAwareMemoryEventSink
    {
        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store,
            string operationId, CancellationToken ct) =>
            throw new InvalidOperationException("C:\\secret\\payload");

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private static class McpPluginHostTestBridge
    {
        public static string FormatStderr(string? line) =>
            (string?)typeof(McpPluginHost)
                .GetMethod("FormatPluginStderr", BindingFlags.Static | BindingFlags.NonPublic)
                ?.Invoke(null, [line]) ?? throw new InvalidOperationException("stderr formatter missing");
    }

    private sealed class ThrowingSink : IMemoryEventSink
    {
        public Task OnStoredAsync(MemoryAddress address, string[] content, IPluginStore store, CancellationToken ct) =>
            throw new InvalidOperationException("C:\\secret\\payload");

        public Task OnAppendedAsync(MemoryAddress address, string content, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;

        public Task OnForgottenAsync(MemoryAddress address, bool wasDeleted, IPluginStore store, CancellationToken ct) =>
            Task.CompletedTask;
    }
}
