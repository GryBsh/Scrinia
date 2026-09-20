# WorkspaceSetup Split — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development. Steps use checkbox (`- [ ]`) syntax.

**Goal:** Split the `WorkspaceSetup` static facade into a thin workspace-identity root plus focused static helpers (`StoreFactory`, `EmbeddingsHost`, `OrphanStoreReporter`, `MaintenanceSchedulerHost`) so each has one responsibility, without changing any public API, wire format, or the ~100 call sites' call shape.

**Architecture:** Keep `WorkspaceSetup` as the single composition root owning only `Root`/`Store`/`Configure`/config seam. Move the embeddings/plugins cluster into `EmbeddingsHost` (CLI assembly), the orphan-store warnings into `OrphanStoreReporter` (CLI), the maintenance scheduler into `MaintenanceSchedulerHost` (CLI), and store construction into `StoreFactory` (Core). All new static types are `internal`. `Configure` delegates store creation to `StoreFactory.Create`.

**Tech Stack:** C# / .NET 10, ConsoleAppFramework v5, xUnit.

**Spec:** docs/superpowers/specs/2026-09-20-workspace-setup-split-design.md

## Global Constraints
- No public API change: every new type is `internal static`; no new public members on existing types.
- No wire-format change: published string enums, JSON spellings, and CLI output strings are untouched.
- No behavioral change: `Configure` must install the same store into `MemoryStoreContext.Current` and `.Default` with the same ranker wiring.
- `WorkspaceSetup.Root` and `WorkspaceSetup.Store` remain the single read accessors for process root and store.
- `GetConfigValue`/`GetPluginName` stay on `WorkspaceSetup` (universal config seam).
- `dotnet test Scrinia.sln` must stay green before any commit is accepted.
- Use `TestHelpers.StoreScope` for test isolation.

---

## Pre-flight (do not skip)

Every code block below is copied verbatim from the files read on 2026-09-20. Before editing, re-confirm the exact line numbers with the `read` tool; do not invent signatures.

Key verified facts:
- `IMultiMemoryParticipantAdapterFactory` is declared in namespace `Scrinia.Core` (`src/Scrinia.Core/MultiMemoryTransaction.cs:65`), NOT `Scrinia.Core.MultiMemory`. `StoreFactory` lives in `namespace Scrinia.Core` and references it directly.
- `FileMemoryStore` constructor the split relies on: `internal FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions, MemoryValidationOptions? writeValidationOptions, IMultiMemoryParticipantAdapterFactory? participantAdapterFactory)` (`src/Scrinia.Core/FileMemoryStore.cs:314-334`). The public `FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions = null, MemoryValidationOptions? writeValidationOptions = null)` (`src/Scrinia.Core/FileMemoryStore.cs:308`) is only for `TestHelpers.StoreScope`.
- `RankerOptions.FromConfig(Func<string, string?> getConfig)` (`src/Scrinia.Core/Search/RankerOptions.cs:71`).
- `WorkspaceConfig.Load(string workspaceRoot)` returns `WorkspaceConfig` (`src/Scrinia.Core/Configuration/WorkspaceConfig.cs:30`).

---

## Task 1 — Create `StoreFactory` in Core

- [ ] **File: Create** `src/Scrinia.Core/StoreFactory.cs`
- [ ] **Move** the store-construction block from `WorkspaceSetup.Configure` verbatim. Current location: `src/Scrinia/Commands/WorkspaceSetup.cs:120-122`:
```csharp
        var store = participantAdapterFactory is null
            ? new FileMemoryStore(root, rankerOptions)
            : new FileMemoryStore(root, rankerOptions, null, participantAdapterFactory);
```
- [ ] **New file** `src/Scrinia.Core/StoreFactory.cs` (verbatim):
```csharp
using Scrinia.Core.Search;

namespace Scrinia.Core;

/// <summary>
/// Construct <see cref="FileMemoryStore"/> instances for the CLI. Extracted from the former
/// <c>WorkspaceSetup.Configure</c> inline <c>new FileMemoryStore(...)</c> so store construction has
/// one owner and the workspace composition root no longer reaches into Core's constructor overloads.
///
/// <para>The two overloads mirror <see cref="FileMemoryStore"/> directly: the participant-adapter
/// overload is used only for multi-memory workspaces, so <c>null</c> fills the
/// <c>MemoryValidationOptions</c> slot exactly as the old inline did. See
/// <c>src/Scrinia.Core/FileMemoryStore.cs:308-334</c> for the constructor contract.</para>
/// </summary>
internal static class StoreFactory
{
    internal static IMemoryStore Create(
        string storeRoot,
        RankerOptions rankerOptions,
        IMultiMemoryParticipantAdapterFactory? participantAdapterFactory = null)
        => participantAdapterFactory is null
            ? new FileMemoryStore(storeRoot, rankerOptions)
            : new FileMemoryStore(storeRoot, rankerOptions, null, participantAdapterFactory);
}
```
- [ ] **Modify** `src/Scrinia/Commands/WorkspaceSetup.cs`, method `Configure(string?, IMultiMemoryParticipantAdapterFactory?)` at lines 111-125. Replace lines 120-122 with a single call. The method becomes:
```csharp
    internal static void Configure(string? workspaceRoot, IMultiMemoryParticipantAdapterFactory? participantAdapterFactory)
    {
        string root = WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory());
        _root = root;

        // Build ranker weights from config so a workspace can tune alpha_recency without
        // recompiling. Defaults (RankerOptions.Default) match the historical composition —
        // see RankerOptions docstring for the formula and calibration notes.
        var rankerOptions = RankerOptions.FromConfig(GetConfigValue);
        var store = StoreFactory.Create(root, rankerOptions, participantAdapterFactory);
        MemoryStoreContext.Current = store;
        MemoryStoreContext.Default = store;
    }
```
  (The comment block at lines 116-118 and the `MemoryStoreContext.Current = store;` / `MemoryStoreContext.Default = store;` lines stay unchanged; only the three-line `var store = ... ? ... : ...` expression at lines 120-122 is replaced.)
- [ ] **Verify** `Scrinia.Core` project still compiles (no new using needed — `IMultiMemoryParticipantAdapterFactory` is in `Scrinia.Core`, `RankerOptions`/`IMemoryStore`/`FileMemoryStore` resolve via the existing project references).
- [ ] **Failing→passing test**: create `tests/Scrinia.Tests/StoreFactoryTests.cs` (verbatim):
```csharp
using Scrinia.Core;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

public class StoreFactoryTests
{
    [Fact]
    public async Task Create_InstallsSameInstanceIntoBothContexts()
    {
        using var scope = new TestHelpers.StoreScope();

        var ranker = RankerOptions.Default;
        var store = StoreFactory.Create(scope.WorkspaceDir, ranker);

        Assert.Same(TestHelpers.Store, store);
        Assert.Same(MemoryStoreContext.Current, MemoryStoreContext.Default);

        // Exercise the store through the interface so the construction path is proven end-to-end.
        var entries = store.ListScoped([]);
        Assert.NotNull(entries);
        store.Dispose();
    }

    [Fact]
    public async Task Create_WithParticipantAdapter_UsesParticipantOverload()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = StoreFactory.Create(scope.WorkspaceDir, RankerOptions.Default, null);
        Assert.NotNull(store);
        store.Dispose();
    }
}
```
- [ ] **Commit** Task 1: `git add src/Scrinia.Core/StoreFactory.cs tests/Scrinia.Tests/StoreFactoryTests.cs && git commit -m "feat(core): extract StoreFactory from WorkspaceSetup"`.

---

## Task 2 — Create `EmbeddingsHost` in CLI (the ~550-line embeddings cluster)

- [ ] **File: Create** `src/Scrinia/Commands/EmbeddingsHost.cs`
- [ ] **using block**: copy the current `using` block from `src/Scrinia/Commands/WorkspaceSetup.cs:1-8` verbatim into `EmbeddingsHost.cs`, then `namespace Scrinia.Commands;`:
```csharp
using System.Globalization;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Scrinia.Core;
using Scrinia.Core.Configuration;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Scrinia.Services;

namespace Scrinia.Commands;
```
  (These cover `RuntimeInformation`, `ILogger`/`LoggerFactory`, `EmbeddingOptions`/`EmbeddingProviderFactory`/`EmbeddingReindexer`/`CoreEmbeddingEventHandler`/`EntityTypingEventHandler`, `HybridReranker`/`VectorStore`/`ChunkedSignature`/`EntityGraphScoreContext`/`EntityGraphScoreContributor`/`CompositeSearchScoreContributor`/`PathSegmentScoreContributor`, `IMemoryEventSink`/`MemoryEventSinkContext`, `UserHome`/`McpPluginHost`. Keep them; a later grep in Task 5 confirms each is still referenced.)
- [ ] **Move verbatim** the members from `src/Scrinia/Commands/WorkspaceSetup.cs` into `EmbeddingsHost` (copy the XML docs + bodies exactly, by the line ranges cited):
  - runtime-state **fields** (lines 14-18):
```csharp
    private static McpPluginHost? _pluginHost;
    private static IEmbeddingProvider? _embeddingProvider;
    private static readonly AsyncLocal<EmbeddingProviderOverride?> _embeddingProviderOverride = new();
```
  - `OverrideEmbeddingProviderForTest` + nested records (lines 36-49):
```csharp
    /// <summary>Temporarily replaces provider discovery for an isolated test flow.</summary>
    internal static IDisposable OverrideEmbeddingProviderForTest(IEmbeddingProvider? provider)
    {
        EmbeddingProviderOverride? prior = _embeddingProviderOverride.Value;
        _embeddingProviderOverride.Value = new EmbeddingProviderOverride(provider);
        return new RestoreEmbeddingProviderOverride(prior);
    }

    private sealed record EmbeddingProviderOverride(IEmbeddingProvider? Provider);

    private sealed class RestoreEmbeddingProviderOverride(EmbeddingProviderOverride? prior) : IDisposable
    {
        public void Dispose() => _embeddingProviderOverride.Value = prior;
    }
```
  - `LoadPluginsAsync` (lines 273-321), `MaybeReindexAfterModelSwitch` (lines 329-362), `ForceReindexAsync` (lines 382-445), `IsHttpEmbeddingsProvider` (lines 450-455), `LoadExplicitHttpEmbeddings` (lines 457-498), `LoadBuiltInEmbeddings` (lines 500-541), `InstallEmbeddingEventSinks` (lines 543-553), `IsEmbeddingsPluginInstalled` (line 559), `ResolveEmbeddingsPluginExe` (lines 568-582), `PluginDirectoryResult` (line 590), `ResolvePluginDirectory` (lines 601-645), `IsSafeSegment` (lines 647-651), `CurrentRid` (line 653), `SelectCompatibleVersion` (lines 660-685), `SelectRidDirectory` (lines 691-702), `TryLoadVulkanPluginAsync` (lines 704-763), `BuildEmbeddingOptions` (lines 766-818).
- [ ] **Signature changes inside the moved cluster** (the ONLY edits to the verbatim copy):
  1. `LoadPluginsAsync` gains a leading `string workspaceRoot` parameter, and inside the body replace the bare `Root` at `WorkspaceSetup.cs:285`:
```csharp
        string workspaceDir = Path.Combine(workspaceRoot, ".scrinia");
```
  2. `ForceReindexAsync` gains a leading `string workspaceRoot` parameter; replace the inline workspace root at `WorkspaceSetup.cs:409`:
```csharp
            string embeddingsDir = Path.Combine(workspaceRoot, ".scrinia", "embeddings");
```
  3. `TryLoadVulkanPluginAsync` — replace the `Root`-based `dataDir` at `WorkspaceSetup.cs:710`:
```csharp
        string dataDir = Path.Combine(workspaceRoot, ".scrinia");
```
  4. `GetPluginName` and `GetConfigValue` are NOT moved (stay on `WorkspaceSetup`). Rewrite the cross-type calls in the moved `ResolveEmbeddingsPluginExe` (`WorkspaceSetup.cs:570`) and `TryLoadVulkanPluginAsync` (`WorkspaceSetup.cs:719`):
```csharp
        string pluginName = WorkspaceSetup.GetPluginName("plugins:embeddings", "scri-plugin-embeddings");
```
```csharp
            await host.StartAsync(embeddingsExe, dataDir, modelsDir, WorkspaceSetup.GetConfigValue, ct);
```
- [ ] **Delete** the moved members (lines 14-18, 36-49, 273-818) from `src/Scrinia/Commands/WorkspaceSetup.cs`. Keep `GetConfigValue`/`GetPluginName` (lines 820-861) and `Configure` (lines 108-125) in `WorkspaceSetup`.

---

## Task 2 (cont.) — EmbeddingsHost test + call sites

- [ ] **Failing→passing test**: create `tests/Scrinia.Tests/EmbeddingsHostTests.cs` (verbatim):
```csharp
using Scrinia.Core;

namespace Scrinia.Tests;

public class EmbeddingsHostTests
{
    [Fact]
    public void IsHttpEmbeddingsProvider_ClassifiesKnownProviders()
    {
        Assert.True(EmbeddingsHost.IsHttpEmbeddingsProvider("ollama"));
        Assert.True(EmbeddingsHost.IsHttpEmbeddingsProvider("OpenAI"));   // case-insensitive
        Assert.False(EmbeddingsHost.IsHttpEmbeddingsProvider("model2vec"));
        Assert.False(EmbeddingsHost.IsHttpEmbeddingsProvider(""));
    }

    [Fact]
    public void ResolvePluginDirectory_UnknownHome_ReturnsNullAndDiagnostics()
    {
        // No plugin installed under a throwaway SCRINIA_HOME → fail-closed null, never throws.
        var result = EmbeddingsHost.ResolvePluginDirectory("scri-plugin-embeddings", "9.9.9");
        Assert.Null(result.PluginDir);
        Assert.False(result.Degraded);
        Assert.Contains("SCRINIA_HOME", result.Diagnostics);
    }

    [Fact]
    public void OverrideEmbeddingProviderForTest_Null_ClearsProviderOverride()
    {
        using var scope = new TestHelpers.StoreScope();
        WorkspaceSetup.Configure(scope.WorkspaceDir);

        using var _ = EmbeddingsHost.OverrideEmbeddingProviderForTest(new TestEmbeddingProvider());
        using var cleared = EmbeddingsHost.OverrideEmbeddingProviderForTest(null);

        // With no provider override installed, the plugin-installed guard takes the no-plugin path.
        Assert.False(EmbeddingsHost.IsEmbeddingsPluginInstalled());
    }
}
```
  Verify `TestEmbeddingProvider` exists in `tests/Scrinia.TestDoubles` (referenced by `CliReviewGapTests.cs:144`). Add the needed `using` for it and `using Scrinia.Core;`.
- [ ] **Update production call sites** (verbatim before → after):
  - `ScriniaCommands.Serve.cs:47`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.Setup.cs:68`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.Setup.cs:108`: `string? pluginExe = WorkspaceSetup.ResolveEmbeddingsPluginExe();` → `string? pluginExe = EmbeddingsHost.ResolveEmbeddingsPluginExe();`
  - `ScriniaCommands.Setup.cs:807`: `await WorkspaceSetup.LoadPluginsAsync(ct);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, ct);`
  - `ScriniaCommands.Setup.cs:810`: `await WorkspaceSetup.ForceReindexAsync(ct);` → `await EmbeddingsHost.ForceReindexAsync(WorkspaceSetup.Root, ct);`
  - `ScriniaCommands.Setup.cs:834`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.Setup.cs:836`: `var result = await WorkspaceSetup.ForceReindexAsync(cancellationToken);` → `var result = await EmbeddingsHost.ForceReindexAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.Bundle.cs:89`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.Memory.cs:344`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.Migration.cs:214`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.cs:325`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
  - `ScriniaCommands.Facts.cs:34`: `await WorkspaceSetup.LoadPluginsAsync(cancellationToken);` → `await EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, cancellationToken);`
- [ ] **Update test call sites** (these also reference the moved members and must compile):
  - `tests/Scrinia.Tests/CliReviewGapTests.cs:144`: `WorkspaceSetup.OverrideEmbeddingProviderForTest(new TestEmbeddingProvider())` → `EmbeddingsHost.OverrideEmbeddingProviderForTest(new TestEmbeddingProvider())`
  - `tests/Scrinia.Tests/CliReviewGapTests.cs:164`: `WorkspaceSetup.OverrideEmbeddingProviderForTest(null)` → `EmbeddingsHost.OverrideEmbeddingProviderForTest(null)`
  - `tests/Scrinia.Tests/ModelRelocationTests.cs:309`: `WorkspaceSetup.ResolveEmbeddingsPluginExe()` → `EmbeddingsHost.ResolveEmbeddingsPluginExe()`
- [ ] **Commit** Task 2: `git add -A && git commit -m "refactor(cli): extract EmbeddingsHost from WorkspaceSetup"`.

---

## Task 3 — Create `OrphanStoreReporter` in CLI

- [ ] **File: Create** `src/Scrinia/Commands/OrphanStoreReporter.cs`
- [ ] **Move verbatim** from `src/Scrinia/Commands/WorkspaceSetup.cs`: `WarnOnOrphanStores` (lines 163-182), `FormatOrphanWarning` (lines 127-161), `ListTopics` (lines 196-256), `s_legacyNamespaceDirs` (lines 193-194).
- [ ] **New file** `src/Scrinia/Commands/OrphanStoreReporter.cs` (verbatim, with the one signature edit on `WarnOnOrphanStores`):
```csharp
using Scrinia.Core;

namespace Scrinia.Commands;

/// <summary>
/// Reports stores that workspace root resolution passed over. Extracted from <c>WorkspaceSetup</c>
/// so the orphan-warn concern has one owner and the CLI composition root no longer formats
/// migration commands. The warning wording lives in <see cref="FormatOrphanWarning"/> so it is
/// testable without capturing the console.
/// </summary>
internal static class OrphanStoreReporter
{
    /// <summary>
    /// Builds the message shown when a store exists somewhere resolution deliberately did not look.
    /// Separated from the write so the wording is testable without capturing the console.
    /// </summary>
    internal static string FormatOrphanWarning(string orphan, string root, IReadOnlyList<string> topics)
    {
        string orphanParent = Path.GetDirectoryName(orphan) ?? orphan;
        if (topics.Count > 0)
        {
            string topicList = string.Join(",", topics);
            return $"""
                scrinia: ignoring store at {orphan}
                         using {root}

                to migrate its memories:
                  scri bundle export {topicList} --workspace-root "{orphanParent}"
                  scri bundle import <file> --workspace-root "{root}"
                """;
        }

        // No topic list could be produced — either the store is empty, or it contains a
        // layout `scri bundle export` cannot target with a topic list (e.g. the v2
        // .scrinia/memories/ hierarchy). Guessing a list here would either be wrong or
        // silently incomplete, so point at `scri list` instead of printing one.
        return $"""
            scrinia: ignoring store at {orphan}
                     using {root}

            to migrate its memories, first see what's there:
              scri list --workspace-root "{orphanParent}"
            then:
              scri bundle export <topics> --workspace-root "{orphanParent}"
              scri bundle import <file> --workspace-root "{root}"
            """;
    }

    /// <summary>
    /// Reports stores that root resolution passed over. Called only from commands that already
    /// do real work — never from the per-prompt hint path.
    /// </summary>
    internal static void WarnOnOrphanStores(string workspaceRoot)
    {
        try
        {
            string root = workspaceRoot;
            string cwd = Directory.GetCurrentDirectory();
            foreach (string orphan in WorkspaceRoot.FindOrphanStores(root, cwd))
                Console.Error.WriteLine(FormatOrphanWarning(orphan, root, ListTopics(orphan)));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Reporting an ignored store is advisory; a filesystem it cannot read (or a path
            // FindOrphanStores rejects via Path.GetFullPath/ArgumentException)
            // must not fail the caller's write.
        }
    }

    /// <summary>
    /// Namespace directory names used by the OLD (pre-v3) <c>.scrinia/topics/</c> layout that
    /// <see cref="ListTopics"/> below still has to interpret. Intentionally NOT
    /// <c>MemoryNaming</c> — that type dropped the "reserved namespace directory" concept
    /// entirely in Task 8, since <see cref="Scrinia.Core.FileMemoryStore"/> itself no longer
    /// reads through it. This scan targets an arbitrary ORPHANED/ignored store elsewhere on
    /// disk that this workstream's automatic migration has never touched (it may still be
    /// sitting in the pre-v3 layout), so the classification still needs to exist somewhere.
    /// </summary>
    private static readonly HashSet<string> s_legacyNamespaceDirs =
        new(StringComparer.OrdinalIgnoreCase) { "entity", "memory", "agent" };

    /// <summary>
    /// Topic names inside an orphaned store, for the migration command — the bare names
    /// <c>scri bundle export</c> actually accepts, not raw directory names. Descends into the
    /// <c>entity/</c> and <c>memory/</c> namespace directories for their real child topics
    /// instead of reporting the namespace directory itself as a topic (it isn't one).
    /// </summary>
    internal static IReadOnlyList<string> ListTopics(string storeDir)
    {
        try
        {
            // The v2 hierarchical layout (.scrinia/memories/<segment>/...) has no flat
            // topic-name equivalent that `scri bundle export --topics` can target. Reporting
            // only the v1 topics here would look like a complete migration command while
            // silently leaving v2 memories behind, so when v2 content exists, report no
            // topics at all — the caller falls back to `scri list` instead of a confidently
            // incomplete command.
            string memoriesDir = Path.Combine(storeDir, "memories");
            if (Directory.Exists(memoriesDir) && Directory.EnumerateFileSystemEntries(memoriesDir).Any())
                return [];

            string topicsDir = Path.Combine(storeDir, "topics");
            if (!Directory.Exists(topicsDir)) return [];

            var topics = new List<string>();
            var namespacedChildren = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (string nsDir in Directory.EnumerateDirectories(topicsDir))
            {
                string nsName = Path.GetFileName(nsDir) ?? "";
                if (!s_legacyNamespaceDirs.Contains(nsName)) continue;

                if (nsName.Equals("agent", StringComparison.OrdinalIgnoreCase))
                {
                    // agent is a single scope with no child topic directories.
                    topics.Add("agent");
                    continue;
                }

                foreach (string childDir in Directory.EnumerateDirectories(nsDir))
                {
                    string childName = Path.GetFileName(childDir) ?? "";
                    topics.Add(childName);
                    namespacedChildren.Add(childName);
                }
            }

            foreach (string dir in Directory.EnumerateDirectories(topicsDir))
            {
                string name = Path.GetFileName(dir) ?? "";
                if (s_legacyNamespaceDirs.Contains(name)) continue;
                if (namespacedChildren.Contains(name)) continue; // shadowed by a namespaced entry
                topics.Add(name);
            }

            return [.. topics.Distinct(StringComparer.OrdinalIgnoreCase).Order()];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }
}
```
  The single edit vs. the verbatim copy: `WarnOnOrphanStores()` → `WarnOnOrphanStores(string workspaceRoot)` and its inner `string root = Root;` (verbatim `WorkspaceSetup.cs:171`) → `string root = workspaceRoot;`.
- [ ] **Delete** `FormatOrphanWarning`, `WarnOnOrphanStores`, `ListTopics`, `s_legacyNamespaceDirs` from `src/Scrinia/Commands/WorkspaceSetup.cs`.

---

## Task 3 (cont.) — OrphanStoreReporter test + call sites

- [ ] **Failing→passing test**: create `tests/Scrinia.Tests/OrphanStoreReporterTests.cs` (verbatim):
```csharp
using Scrinia.Commands;

namespace Scrinia.Tests;

public class OrphanStoreReporterTests
{
    private static string MakeLegacyStore(string baseDir)
    {
        Directory.CreateDirectory(Path.Combine(baseDir, "topics", "entity", "memory"));
        Directory.CreateDirectory(Path.Combine(baseDir, "topics", "memory", "auth"));
        Directory.CreateDirectory(Path.Combine(baseDir, "topics", "auth"));
        return baseDir;
    }

    [Fact]
    public void ListTopics_ClassifiesLegacyNamespacedDirs()
    {
        using var scope = new TestHelpers.StoreScope();
        var dir = MakeLegacyStore(Path.Combine(scope.WorkspaceDir, "orphan-store"));

        var topics = OrphanStoreReporter.ListTopics(dir);

        // The bare names `scri bundle export` accepts, not the namespace directories.
        Assert.Contains("memory", topics);   // entity/memory
        Assert.Contains("auth", topics);     // memory/auth and top-level auth
        Assert.DoesNotContain("entity", topics); // namespace dir is not a topic
        Assert.DoesNotContain("agent", topics);  // agent is single-scope, not created here
    }

    [Fact]
    public void FormatOrphanWarning_WithTopics_ContainsMigrationCommand()
    {
        var msg = OrphanStoreReporter.FormatOrphanWarning("/tmp/orphan", "/workspace/new", ["auth", "memory"]);
        Assert.Contains("scri bundle export auth,memory --workspace-root \"/tmp/orphan\"", msg);
        Assert.Contains("scri bundle import <file> --workspace-root \"/workspace/new\"", msg);
    }
}
```
  Note: `OrphanStoreReporter` is in namespace `Scrinia.Commands`, so the test needs `using Scrinia.Commands;`. `ListTopics`/`FormatOrphanWarning` are `internal` — the test assembly must already have `InternalsVisibleTo("Scrinia.Tests")` (other `internal` CLI members are exercised by tests). Verify it exists before adding.
- [ ] **Update production call sites** (verbatim before → after):
  - `src/Scrinia/Commands/ScriniaCommands.Serve.cs:37`: `WorkspaceSetup.WarnOnOrphanStores();` → `OrphanStoreReporter.WarnOnOrphanStores(WorkspaceSetup.Root);`
  - `src/Scrinia/Commands/ScriniaCommands.Setup.cs:40`: same replacement.
  - `src/Scrinia/Commands/ScriniaCommands.cs:324`: same replacement.
  - `src/Scrinia/Commands/ScriniaCommands.Memory.cs`: lines 484, 637, 683, 738, 771 — same replacement (five sites).
- [ ] **Commit** Task 3: `git add src/Scrinia/Commands/OrphanStoreReporter.cs tests/Scrinia.Tests/OrphanStoreReporterTests.cs src/Scrinia/Commands/ScriniaCommands.Serve.cs src/Scrinia/Commands/ScriniaCommands.Setup.cs src/Scrinia/Commands/ScriniaCommands.cs src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia/Commands/WorkspaceSetup.cs && git commit -m "refactor(cli): extract OrphanStoreReporter from WorkspaceSetup"`.

---

## Task 4 — Create `MaintenanceSchedulerHost` in CLI

- [ ] **File: Create** `src/Scrinia/Commands/MaintenanceSchedulerHost.cs`
- [ ] **Move verbatim** from `src/Scrinia/Commands/WorkspaceSetup.cs`: `ActiveMaintenanceScheduler` (lines 62-68), the field `_maintenanceScheduler` (line 60), `StartMaintenanceScheduler` (lines 78-90), `StopMaintenanceSchedulerAsync` (lines 98-106).
- [ ] **New file** `src/Scrinia/Commands/MaintenanceSchedulerHost.cs` (verbatim, with the one signature edit):
```csharp
using Scrinia.Core;

namespace Scrinia.Commands;

/// <summary>
/// Owns the single bounded background maintenance adapter for the CLI process. Extracted from
/// <c>WorkspaceSetup</c> so the maintenance-scheduler concern has one owner; the outer global
/// background drain stays in <c>Program</c>.
/// </summary>
internal static class MaintenanceSchedulerHost
{
    private static MaintenanceScheduler? _maintenanceScheduler;

    /// <summary>
    /// The active bounded background maintenance adapter, if one was started
    /// for this process. Owned by <see cref="StartMaintenanceScheduler"/> and
    /// <see cref="StopMaintenanceSchedulerAsync"/>; the single global
    /// background-work drain stays in <c>Program</c>.
    /// </summary>
    internal static MaintenanceScheduler? ActiveMaintenanceScheduler => _maintenanceScheduler;

    /// <summary>
    /// Starts the B5 bounded background adapter: one single-flight
    /// <see cref="MaintenanceScheduler"/> over the configured file store that
    /// fires at most one Core maintenance invocation via
    /// <c>BackgroundWork.Fire</c>. Returns <c>null</c> when no file-backed
    /// store is configured. A background-disabled or invalid policy schedules
    /// zero work but still returns the scheduler so shutdown stays ordered.
    /// </summary>
    internal static MaintenanceScheduler? StartMaintenanceScheduler(string workspaceRoot)
    {
        if (MemoryStoreContext.Current is not FileMemoryStore fileStore &&
            MemoryStoreContext.Default is not FileMemoryStore)
            return null;
        FileMemoryStore store = MemoryStoreContext.Current as FileMemoryStore
            ?? (FileMemoryStore)MemoryStoreContext.Default!;
        var scheduler = new MaintenanceScheduler(
            store, Core.Configuration.WorkspaceConfig.Load(workspaceRoot));
        _maintenanceScheduler = scheduler;
        scheduler.TrySchedule();
        return scheduler;
    }

    /// <summary>
    /// Stops the active background adapter inner-first (close admission,
    /// cancel and await its active invocation), then disposes it. Never
    /// drains unrelated global work — the outer global drain stays in
    /// <c>Program</c>.
    /// </summary>
    internal static async Task StopMaintenanceSchedulerAsync()
    {
        MaintenanceScheduler? scheduler = _maintenanceScheduler;
        _maintenanceScheduler = null;
        if (scheduler is null)
            return;
        await scheduler.StopAsync().ConfigureAwait(false);
        scheduler.Dispose();
    }
}
```
  The single edit vs. the verbatim copy: `StartMaintenanceScheduler()` → `StartMaintenanceScheduler(string workspaceRoot)` and inside, `Core.Configuration.WorkspaceConfig.Load(Root)` (verbatim `WorkspaceSetup.cs:86`) → `Core.Configuration.WorkspaceConfig.Load(workspaceRoot)`. `StopMaintenanceSchedulerAsync()` and `ActiveMaintenanceScheduler` are unchanged.
- [ ] **Delete** `ActiveMaintenanceScheduler`, `_maintenanceScheduler`, `StartMaintenanceScheduler`, `StopMaintenanceSchedulerAsync` from `src/Scrinia/Commands/WorkspaceSetup.cs`.
- [ ] **Verify** `MaintenanceScheduler` type is reachable in `MaintenanceSchedulerHost.cs`. It is referenced in the moved code; confirm the needed `using Scrinia.Core;` is present — add it if a grep shows it missing.

---

## Task 4 (cont.) — MaintenanceSchedulerHost test + call sites

- [ ] **Failing→passing test**: create `tests/Scrinia.Tests/MaintenanceSchedulerHostTests.cs` (verbatim):
```csharp
using Scrinia.Core;
using Scrinia.Commands;

namespace Scrinia.Tests;

public class MaintenanceSchedulerHostTests
{
    [Fact]
    public void StartWhenNoFileStore_ReturnsNull()
    {
        // With no store configured, StartMaintenanceScheduler returns null (no file-backed store).
        var scheduler = MaintenanceSchedulerHost.StartMaintenanceScheduler("/tmp/does-not-exist-xyz");
        Assert.Null(scheduler);
    }

    [Fact]
    public async Task StartAndStop_Lifecycle_NoThrow()
    {
        using var scope = new TestHelpers.StoreScope();
        WorkspaceSetup.Configure(scope.WorkspaceDir);

        var scheduler = MaintenanceSchedulerHost.StartMaintenanceScheduler(WorkspaceSetup.Root);
        // A scheduler (or null when policy disables maintenance) is acceptable; stop must dispose cleanly.
        await MaintenanceSchedulerHost.StopMaintenanceSchedulerAsync();
        Assert.Null(MaintenanceSchedulerHost.ActiveMaintenanceScheduler);
    }
}
```
  Note: `WorkspaceSetup.Configure` and `MaintenanceSchedulerHost` are in `Scrinia.Commands`; add `using Scrinia.Commands;`.
- [ ] **Update production call sites** (verbatim before → after):
  - `src/Scrinia/Commands/ScriniaCommands.Serve.cs:55`:
    `WorkspaceSetup.StartMaintenanceScheduler();` → `MaintenanceSchedulerHost.StartMaintenanceScheduler(WorkspaceSetup.Root);`
  - `src/Scrinia/Commands/ScriniaCommands.Serve.cs:88`:
    `await WorkspaceSetup.StopMaintenanceSchedulerAsync();` → `await MaintenanceSchedulerHost.StopMaintenanceSchedulerAsync();`
- [ ] **Commit** Task 4: `git add src/Scrinia/Commands/MaintenanceSchedulerHost.cs tests/Scrinia.Tests/MaintenanceSchedulerHostTests.cs src/Scrinia/Commands/ScriniaCommands.Serve.cs src/Scrinia/Commands/WorkspaceSetup.cs && git commit -m "refactor(cli): extract MaintenanceSchedulerHost from WorkspaceSetup"`.

---

## Task 5 — Verify `WorkspaceSetup` is thin

- [ ] **Re-read** `src/Scrinia/Commands/WorkspaceSetup.cs`. Confirm the file now contains ONLY:
  - fields: `_root`, `_rootOverride`
  - `Root`, `OverrideRoot`, `Store`
  - `Configure(string?)` / `Configure(string?, IMultiMemoryParticipantAdapterFactory?)` (now delegating to `StoreFactory.Create`)
  - `GetConfigValue` (2 overloads), `GetPluginName`
  - the XML docs attached to those members.
- [ ] **No test needed** (compile gate).
- [ ] **using cleanup**: grep each `using` in `WorkspaceSetup.cs` for remaining references; only remove a `using` line if the symbol it imports has zero remaining references in the file. Specifically:
  - `using Scrinia.Core.Embeddings;` — grep `Embeddings`; if zero references, remove.
  - `using System.Runtime.InteropServices;` — grep `RuntimeInformation`; if zero, remove.
  - `using Scrinia.Services;` — grep `UserHome`; if zero, remove.
  - `using Scrinia.Core.Configuration;` — grep `WorkspaceConfig`; `GetConfigValue` still calls `WorkspaceConfig.GetValue` (verbatim `WorkspaceSetup.cs:845,860`), so KEEP.
  - `using Microsoft.Extensions.Logging;` — grep `LoggerFactory`/`ILogger`; if zero references remain after the embeddings move, remove.
- [ ] **Commit** Task 5: `git add src/Scrinia/Commands/WorkspaceSetup.cs && git commit -m "refactor(cli): thin WorkspaceSetup to composition root only"`.

---

## Task 6 — Build + full test run + final verification

- [ ] **Build**: run `dotnet build Scrinia.sln`. Fix any compile errors — report exact `file:line` and the one-line fix for each.
- [ ] **Test**: run `dotnet test Scrinia.sln`. It must stay green.
- [ ] **Line count**: confirm `src/Scrinia/Commands/WorkspaceSetup.cs` dropped from 862 to under ~120 lines (run `wc -l src/Scrinia/Commands/WorkspaceSetup.cs`).
- [ ] **Report** the final test count and the new `WorkspaceSetup.cs` line count.
- [ ] **Commit rule**: Task 6 is the verification gate — do NOT commit the verification itself. Commit only the test additions made in Tasks 2-5 (already committed per-task). If Task 6 forces a fix (a compile error or a red test), fix it and commit the fix with an explanatory message; otherwise report results and confirm green without a new commit.

---

## Call-site inventory (authoritative, from grep on 2026-09-20)

`WorkspaceSetup.LoadPluginsAsync` → `EmbeddingsHost.LoadPluginsAsync(WorkspaceSetup.Root, ...)`:
- ScriniaCommands.Serve.cs:47, ScriniaCommands.Setup.cs:68/807/834, ScriniaCommands.Bundle.cs:89, ScriniaCommands.Memory.cs:344/485/638/684/739/772, ScriniaCommands.Migration.cs:214, ScriniaCommands.cs:325, ScriniaCommands.Facts.cs:34.

`WorkspaceSetup.ForceReindexAsync` → `EmbeddingsHost.ForceReindexAsync(WorkspaceSetup.Root, ...)`:
- ScriniaCommands.Setup.cs:810/836.

`WorkspaceSetup.WarnOnOrphanStores()` → `OrphanStoreReporter.WarnOnOrphanStores(WorkspaceSetup.Root)`:
- ScriniaCommands.Serve.cs:37, ScriniaCommands.Setup.cs:40, ScriniaCommands.cs:324, ScriniaCommands.Memory.cs:484/637/683/738/771.

`WorkspaceSetup.StartMaintenanceScheduler()` / `StopMaintenanceSchedulerAsync()` → `MaintenanceSchedulerHost.*`:
- ScriniaCommands.Serve.cs:55/88.

`WorkspaceSetup.ResolveEmbeddingsPluginExe()` → `EmbeddingsHost.ResolveEmbeddingsPluginExe()`:
- ScriniaCommands.Setup.cs:108, tests/Scrinia.Tests/ModelRelocationTests.cs:309.

`WorkspaceSetup.OverrideEmbeddingProviderForTest(...)` → `EmbeddingsHost.OverrideEmbeddingProviderForTest(...)`:
- tests/Scrinia.Tests/CliReviewGapTests.cs:144/164.

`WorkspaceSetup.Configure(...)` / `.Root` / `.Store` — UNCHANGED (stay on `WorkspaceSetup`).
`WorkspaceSetup.GetConfigValue(...)` / `GetPluginName(...)` — UNCHANGED (stay on `WorkspaceSetup`).
`new FileMemoryStore(...)` inline in `Configure` → `StoreFactory.Create(...)` — only inside `WorkspaceSetup.Configure` (`src/Scrinia/Commands/WorkspaceSetup.cs:120-122`).
