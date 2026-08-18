using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Encoding;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Plugin.Embeddings.Tests;

/// <summary>
/// Regression coverage for the qualifiedName round-trip loss in
/// <see cref="EmbeddingsPlugin.ReindexStoreAsync"/> (docs task "F", part 2 — the "duplicated
/// reindex logic" half of the bug already fixed for <c>EmbeddingReindexer</c> in commit 1e81666
/// and covered by <c>EmbeddingReindexerNamespacedScopeTests</c>, and mirrored for the built-in
/// service by <c>BuiltInEmbeddingsServiceReindexTests</c>).
///
/// <see cref="IMemoryStore.FormatQualifiedName"/> intentionally strips the "memory/"/"entity/"
/// namespace prefix for human-friendly display. Re-parsing that display string via
/// <see cref="IPluginStore.ParseQualifiedName"/> reconstructs a scope MISSING the namespace
/// prefix, so <c>ResolveArtifactAsync</c> can't find the artifact and the memory is silently
/// skipped (caught, logged, dropped from the reindex count).
///
/// This plugin has no Vulkan model available in the test environment (no GGUF at the expected
/// path), so <c>EnsureInitialized</c> falls back to a <c>NullEmbeddingProvider</c> that never
/// throws and always returns null vectors — meaning the reindexed *count* can't distinguish
/// "resolved fine, embedding skipped" from "resolution threw and was skipped" (both are 0). The
/// resolution step runs and can fail BEFORE the embed call, so this test instead captures the
/// plugin's logger (swapped in via reflection before <c>EnsureInitialized</c> runs, exploiting
/// its <c>_logger ??= ...</c> lazy-init guard) and asserts no "Failed to reindex" warning was
/// logged for the seeded namespaced-scope memory — which only happens if <c>ResolveArtifactAsync</c>
/// threw <see cref="FileNotFoundException"/> resolving the mis-derived scope.
/// </summary>
public sealed class EmbeddingsPluginReindexTests : IDisposable
{
    private readonly string _root;
    private readonly FileMemoryStore _store;

    public EmbeddingsPluginReindexTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"scrinia_vulkan_reindex_ns_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _store = new FileMemoryStore(_root);
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void SeedEntry(string scope, string name, string content)
    {
        var entry = new ArtifactEntry(name, "", content.Length, 1, DateTimeOffset.UtcNow);
        _store.Upsert(entry, scope);
        string path = _store.ArtifactPath(name, scope);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, Nmp2ChunkedEncoder.Encode(content));
    }

    [Fact]
    public async Task ReindexStoreAsync_NamespacedTopicScope_ResolvesWithoutLoggingFailure()
    {
        const string scope = "local:memory/api";
        SeedEntry(scope, "notes", "auth flow notes");

        using var plugin = new EmbeddingsPlugin();
        var capturingLogger = new CapturingLogger();

        // Pre-set the private logger field so EnsureInitialized's `_logger ??= ...` guard
        // keeps it instead of building its own console logger — the only seam available
        // without wiring a full DI container through the plugin's internal init path.
        typeof(EmbeddingsPlugin)
            .GetField("_logger", BindingFlags.NonPublic | BindingFlags.Instance)!
            .SetValue(plugin, capturingLogger);

        // Trigger EnsureInitialized via the public entry point (no GPU/model present, so the
        // provider falls back to NullEmbeddingProvider — _vectorStore is still constructed).
        await plugin.ComputeScoresAsync("warm-up", Array.Empty<ScopedArtifact>(), _store, CancellationToken.None);

        int count = await plugin.ReindexStoreAsync(_store, CancellationToken.None);

        count.Should().Be(0, because: "NullEmbeddingProvider (no GPU model in test env) never returns a vector, so nothing gets upserted");
        capturingLogger.Messages.Should().NotContain(
            m => m.Contains("Failed to reindex", StringComparison.Ordinal),
            because: "resolving the artifact by (scope, subject) must not throw FileNotFoundException for a namespaced topic scope");
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Messages { get; } = [];

        IDisposable? ILogger.BeginScope<TState>(TState state) => null;
        bool ILogger.IsEnabled(LogLevel logLevel) => true;

        void ILogger.Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
