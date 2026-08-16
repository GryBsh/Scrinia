using Scrinia.Core;

namespace Scrinia.TestDoubles;

/// <summary>
/// A do-nothing <see cref="IMemoryEventSink"/> whose only job is to be non-null.
///
/// <para><b>Why a sentinel rather than clearing the process-wide default.</b> Test scopes need a
/// clean room: a real embed/entity-typing/maintenance pipeline left installed by an earlier test
/// fires on every later test's writes and rewrites their entries mid-assertion. Scopes used to get
/// that by setting <c>MemoryEventSinkContext.Default = null</c> — but <c>Default</c> is a
/// process-wide static that PRODUCTION depends on, and xUnit runs test classes in parallel, so one
/// scope's constructor or disposal could null the value another test was mid-way through asserting
/// on. That is the race behind the intermittent
/// <c>MemoryEventSinkContextInstallDefaultTests.InstallDefault_ComposesSinksInOrder</c> failure.</para>
///
/// <para><b>The fix uses the cell that is already per-flow.</b> <c>MemoryEventSinkContext.Current</c>
/// is an <c>AsyncLocal</c> whose getter is <c>_current.Value ?? _default</c>, so assigning this
/// sentinel SHADOWS <c>Default</c> for the assigning async flow alone. Isolation becomes per-test
/// rather than process-wide, and no test writes <c>Default</c> any more — which is what removes the
/// race at its source instead of serialising around it.</para>
///
/// <para><b>Why it cannot be <see langword="null"/>.</b> Assigning <c>Current = null</c> does not
/// shadow anything — the getter falls straight back to <c>Default</c>. Shadowing requires a
/// non-null value, which is the whole reason this type exists.</para>
///
/// <para><b>Do NOT convert <c>Default</c> itself to <c>AsyncLocal</c>.</b> Measured, not assumed:
/// all six <c>InstallDefault</c> call sites sit inside <c>WorkspaceSetup.LoadPluginsAsync</c>
/// (<c>async Task</c>), and an <c>AsyncLocal</c> write inside an async method dies when that method
/// returns — before the MCP host is even constructed. Every CLI event consumer reads
/// <c>Current?.OnStoredAsync(…) ?? Task.CompletedTask</c>, which has no error path, so the result
/// would be embeddings, auto-linking, orphan detection and entity typing all silently ceasing to
/// fire. <c>Default</c> is process-wide on purpose.</para>
/// </summary>
public sealed class SilentEventSink : IMemoryEventSink
{
    /// <summary>The one instance. Stateless, so sharing it across every scope is free and makes
    /// "is the sentinel installed?" answerable by reference equality.</summary>
    public static readonly SilentEventSink Instance = new();

    private SilentEventSink() { }

    public Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct) =>
        Task.CompletedTask;

    public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct) =>
        Task.CompletedTask;
}
