using FluentAssertions;
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Pins the composition contract of <see cref="MemoryEventSinkContext.InstallDefault"/>: the six
/// sink-install sites in <c>WorkspaceSetup.cs</c> now all route through this one method, so a
/// future change to how the default is composed (e.g. wrapping in a logging decorator) only needs
/// to happen here instead of at six call sites.
/// </summary>
public sealed class MemoryEventSinkContextInstallDefaultTests
{
    /// <summary>
    /// The race, pinned deterministically rather than waited for.
    ///
    /// <para><c>InstallDefault_ComposesSinksInOrder</c> failed roughly one full solution run in
    /// five, which is far too rare to develop against. This reproduces the same collision on demand:
    /// several parallel flows churn <see cref="TestHelpers.StoreScope"/> — construct and dispose,
    /// the two places that used to write <c>Default</c> — while this flow repeatedly reads the
    /// <c>Default</c> it just installed. Against the old code the read finds <see langword="null"/>
    /// almost immediately; the fix makes the scopes write the AsyncLocal <c>Current</c> instead, so
    /// nothing they do is visible here at all.</para>
    ///
    /// <para>It also guards the invariant the class doc names: if any test scope ever goes back to
    /// writing <c>Default</c>, this fails on purpose.</para>
    /// </summary>
    [Fact]
    public async Task AParallelScopeLifecycleDoesNotDisturbDefault()
    {
        var prior = MemoryEventSinkContext.Default;
        try
        {
            MemoryEventSinkContext.InstallDefault(new NoOpSink());
            var installed = MemoryEventSinkContext.Default;
            installed.Should().NotBeNull();

            const int churnFlows = 4;
            const int scopesPerFlow = 40;
            var churn = Enumerable.Range(0, churnFlows).Select(_ => Task.Run(() =>
            {
                for (int i = 0; i < scopesPerFlow; i++)
                {
                    using var scope = new TestHelpers.StoreScope();
                }
            })).ToArray();

            while (!churn.All(t => t.IsCompleted))
            {
                MemoryEventSinkContext.Default.Should().BeSameAs(installed,
                    because: "a scope in another flow must not be able to touch the process-wide "
                           + "default this flow installed — that collision is the intermittent "
                           + "failure in InstallDefault_ComposesSinksInOrder");
                await Task.Yield();
            }

            await Task.WhenAll(churn);
            MemoryEventSinkContext.Default.Should().BeSameAs(installed);
        }
        finally
        {
            MemoryEventSinkContext.Default = prior;
        }
    }

    [Fact]
    public async Task InstallDefault_ComposesSinksInOrder()
    {
        // MemoryEventSinkContext.Default is a plain process-wide static, and the suite runs test
        // classes in parallel — so save/restore around the mutation.
        //
        // This test used to fail intermittently (~1 full run in 5) and the save/restore was not the
        // problem: it protects OTHER tests from this one, not this one from them. Every test scope
        // in the suite also wrote Default (`= null`, in both construction and disposal), so a scope
        // in a parallel class could null the value between this test's InstallDefault call and the
        // assertion below. Those scopes now shadow the AsyncLocal `Current` instead, which leaves
        // this class as the ONLY writer of Default in the whole test tree — verified by
        // enumeration. A single writer cannot race, and xUnit runs the facts within one class
        // sequentially, so the three here cannot race each other either.
        //
        // Keep it that way: a new `MemoryEventSinkContext.Default = …` anywhere in tests/ reopens
        // this. Use Current and Scrinia.TestDoubles.SilentEventSink.
        var prior = MemoryEventSinkContext.Default;
        try
        {
            var order = new List<string>();
            var a = new RecordingSink("a", order);
            var b = new RecordingSink("b", order);

            MemoryEventSinkContext.InstallDefault(a, b);

            MemoryEventSinkContext.Default.Should().BeOfType<CompositeEventSink>();

            await MemoryEventSinkContext.Default!.OnStoredAsync(
                "test:topic", ["chunk"], null!, CancellationToken.None);

            order.Should().Equal(
                ["a", "b"],
                because: "sinks must fire in the order passed to InstallDefault — " +
                          "WorkspaceSetup's plugin-host site relies on host-before-maintenance ordering");
        }
        finally
        {
            MemoryEventSinkContext.Default = prior;
        }
    }

    [Fact]
    public void InstallDefault_WithNoSinks_Throws() =>
        FluentActions.Invoking(() => MemoryEventSinkContext.InstallDefault())
            .Should().Throw<ArgumentException>(
                because: "an empty composite silently discards every memory event");

    [Fact]
    public void InstallDefault_WithANullSink_Throws() =>
        FluentActions.Invoking(() => MemoryEventSinkContext.InstallDefault(new NoOpSink(), null!))
            .Should().Throw<ArgumentException>(
                because: "CompositeEventSink's catch handler dereferences the same null sink via "
                       + "sink.GetType(), so the NRE escapes the catch and aborts every later sink");

    private sealed class RecordingSink(string name, List<string> order) : IMemoryEventSink
    {
        public Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct)
        {
            order.Add(name);
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;
    }

    private sealed class NoOpSink : IMemoryEventSink
    {
        public Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;
    }
}
