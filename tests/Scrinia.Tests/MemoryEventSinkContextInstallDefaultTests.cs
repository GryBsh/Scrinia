using FluentAssertions;
using Scrinia.Core;
using Xunit;

namespace Scrinia.Tests;

/// <summary>
/// Pins the composition contract of <see cref="MemoryEventSinkContext.InstallDefault"/>: the six
/// sink-install sites in <c>WorkspaceSetup.cs</c> now all route through this one method, so a
/// future change to how the default is composed (e.g. wrapping in a logging decorator) only needs
/// to happen here instead of at six call sites.
/// </summary>
public sealed class MemoryEventSinkContextInstallDefaultTests
{
    [Fact]
    public async Task InstallDefault_ComposesSinksInOrder()
    {
        // MemoryEventSinkContext.Default is a plain process-wide static (see TestHelpers.StoreScope),
        // and the suite runs test classes in parallel, so save/restore around the mutation.
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

    private sealed class RecordingSink(string name, List<string> order) : IMemoryEventSink
    {
        public Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
        {
            order.Add(name);
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
            => Task.CompletedTask;
    }
}
