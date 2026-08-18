# Plugin Contract Narrowing Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Narrow the `IMemoryEventSink` contract so implementers receive a four-member `IPluginStore` instead of the full 30-member `IMemoryStore`.

**Architecture:** A new `IPluginStore` interface in `Scrinia.Core` carries the four members the sink path actually uses. `IMemoryStore` derives from it, so every existing consumer and implementation is unaffected. `IMemoryEventSink` and `MemoryArtifactSource.ReadForAppendAsync` retype to the narrow interface. The two Core-internal handlers that need more cast back through one shared helper that throws with a named reason. No type moves between assemblies.

**Tech Stack:** C# / .NET 10, xUnit, FluentAssertions. Windows, PowerShell primary.

**Spec:** `docs/superpowers/specs/2026-08-18-plugin-contract-narrowing-design.md`

## Global Constraints

- **Branch:** `refactor/plugin-contract-narrowing`, cut from `fix/published-surface-coherence` at `089f892`.
- **Baseline:** build green, `dotnet test Scrinia.sln` = **2660 passed / 0 failed / 4 skipped**. Every task must end at or above this, accounting for tests it adds.
- **Known flake, do not chase:** `Scrinia.Tests.MemoryEventSinkContextInstallDefaultTests.AParallelScopeLifecycleDoesNotDisturbDefault` fails intermittently from test-suite interference.
- **The LSP in this workspace emits phantom `CS0535`/`CS1503` errors during large renames while `dotnet build` is green.** Verify by build and by real file bytes, never by the diagnostic stream.
- **Falsify every new pin by mutation before trusting it** — break the code, watch the test fail, restore, watch it pass, and paste both outputs. Two tests on the previous branch passed while the thing they claimed to pin was broken.
- **No type moves between assemblies.** `Scrinia.Plugin.Abstractions` keeps its `ProjectReference` to `Scrinia.Core`. No `AssemblyVersion` pin. No second sink interface. Hook contexts (`ctx.Store`) are not retyped.
- `TreatWarningsAsErrors` is on for non-`.Tests` projects (`Directory.Build.props:44-46`).

---

## File Structure

**Created:**
- `src/Scrinia.Core/IPluginStore.cs` — the four-member contract handed to sinks.
- `src/Scrinia.Core/CoreSinkStore.cs` — the one place the "Core handler needs the full store" cast and its failure message live.
- `tests/Scrinia.Tests/PluginStoreContractTests.cs` — pins the contract's membership and `IMemoryStore`'s derivation.
- `tests/Scrinia.Tests/PluginStoreOnlySinkTests.cs` — the load-bearing test: a store that is *only* an `IPluginStore`, driven through the sink pipeline.

**Modified:**
- `src/Scrinia.Core/IMemoryStore.cs` — four declarations move out; `: IPluginStore` added.
- `src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs:93` — parameter narrows.
- `src/Scrinia.Core/IMemoryEventSink.cs:10-12` — three method signatures narrow.
- Seven production sinks and nine test doubles — parameter type only, except the two Core handlers.
- `CHANGELOG.md`.

---

# Task 1: `IPluginStore`, and `IMemoryStore` derives from it

**Files:**
- Create: `src/Scrinia.Core/IPluginStore.cs`
- Modify: `src/Scrinia.Core/IMemoryStore.cs` (lines `13`, `15`, `66`, `318`, and the interface declaration at `11`)
- Test: `tests/Scrinia.Tests/PluginStoreContractTests.cs`

**Interfaces:**
- Produces: `Scrinia.Core.IPluginStore` with exactly `ParseQualifiedName`, `IsEphemeral`, `ResolveArtifactAsync`, `ReadArtifactAsync`. Tasks 2 and 3 consume it.

**Context:** This task is a pure declaration move. No behaviour changes, no implementation changes — `FileMemoryStore`, `HttpMemoryStore` and `FakeMemoryStore` already implement all four members, and interface inheritance is transparent to them.

**The four declarations to move, exactly.** Do not move anything else:

| Line | Declaration | Section comment it sits under |
|---|---|---|
| `13` | `(string Scope, string Subject) ParseQualifiedName(string name);` | `// Naming` |
| `15` | `bool IsEphemeral(string name);` | `// Naming` |
| `66` | `Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default);` | `// CRUD` |
| `318` | `Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default);` | `// Artifact file I/O` |

**Two traps, both real:**

1. **`// Naming` holds four members, and only two move.** `FormatQualifiedName` (`:14`) and `SanitizeName` (`:16`) STAY on `IMemoryStore` — `MaintenanceEventSink` and `EntityTypingEventHandler` use them through the full store. Do not move the block.
2. **`IMemoryStore` declares `ListScoped` twice** — abstract one-arg at `:154`, default-bodied two-arg at `:197`. Neither moves. An instruction to "remove members now inherited" has previously risked deleting the topic-exclusion overload and its `ShouldExcludeScope` filter. Touch only the four rows above.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/PluginStoreContractTests.cs
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Pins the narrowed sink contract. IPluginStore exists so a sink is handed what it needs
/// (four members) rather than all 30 of IMemoryStore's. Nothing enforces that but this file:
/// widening IPluginStore is a one-line edit that no other test would notice.
/// </summary>
public class PluginStoreContractTests
{
    [Fact]
    public void IPluginStore_DeclaresExactlyTheSinkPathMembers()
    {
        string[] declared = [.. typeof(IPluginStore).GetMembers().Select(m => m.Name).OrderBy(n => n)];

        declared.Should().BeEquivalentTo(
            ["IsEphemeral", "ParseQualifiedName", "ReadArtifactAsync", "ResolveArtifactAsync"],
            because: "the contract's value is its narrowness; adding a member here should be a " +
                     "deliberate act that updates this list, not a quiet widening");
    }

    [Fact]
    public void IMemoryStore_DerivesFromIPluginStore()
    {
        typeof(IPluginStore).IsAssignableFrom(typeof(IMemoryStore)).Should().BeTrue(
            because: "every real store must satisfy both, so a store can be handed to a sink " +
                     "without a cast or an adapter");
    }

    [Fact]
    public void IMemoryStore_StillDeclaresTheNamingMembersThatDidNotMove()
    {
        string[] names = [.. typeof(IMemoryStore).GetMembers().Select(m => m.Name)];

        names.Should().Contain("FormatQualifiedName").And.Contain("SanitizeName",
            because: "only two of the four `// Naming` members move; Core's own handlers use " +
                     "these two through the full store");
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~PluginStoreContractTests`
Expected: FAIL to compile — `IPluginStore` does not exist (`CS0246`). That compile error is the failure.

- [ ] **Step 3: Create the interface**

```csharp
// src/Scrinia.Core/IPluginStore.cs
namespace Scrinia.Core;

/// <summary>
/// The store surface an <see cref="IMemoryEventSink"/> is handed. Four members, because four is
/// what the sink path uses: <c>ParseQualifiedName</c> to key the write, and the ephemeral-aware
/// read trio that <see cref="Embeddings.MemoryArtifactSource.ReadForAppendAsync"/> needs.
///
/// <para>Sinks used to receive the whole <see cref="IMemoryStore"/> — 30 instance members — which
/// said nothing about what a sink may rely on. Narrowing states the contract. It does not enforce
/// it: a sink can still cast back, and Core's own two handlers do exactly that (see
/// <see cref="CoreSinkStore"/>). What changes is that widening is now a visible cast rather than
/// an invisible use of an already-available member.</para>
///
/// <para><see cref="IMemoryStore"/> derives from this, so every concrete store satisfies both and
/// no adapter is needed at the call sites that hand a store to a sink.</para>
/// </summary>
public interface IPluginStore
{
    (string Scope, string Subject) ParseQualifiedName(string name);
    bool IsEphemeral(string name);
    Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default);
    Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default);
}
```

- [ ] **Step 4: Derive `IMemoryStore` and delete the four moved declarations**

In `src/Scrinia.Core/IMemoryStore.cs`:

Change the declaration at `:11` from `public interface IMemoryStore` to:

```csharp
public interface IMemoryStore : IPluginStore
```

Then delete exactly these four lines, leaving their neighbours and section comments intact:

```csharp
    (string Scope, string Subject) ParseQualifiedName(string name);              // was :13
    bool IsEphemeral(string name);                                               // was :15
    Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default);  // was :66
    Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default); // was :318
```

Add a one-line comment where the `// Naming` pair was, so the next reader is not puzzled by a half-empty section:

```csharp
    // Naming — ParseQualifiedName and IsEphemeral moved to IPluginStore (inherited).
    string FormatQualifiedName(string scope, string subject);
    string SanitizeName(string name);
```

- [ ] **Step 5: Run the tests and verify they pass**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~PluginStoreContractTests`
Expected: PASS, 3 of 3.

- [ ] **Step 6: Falsify the membership pin**

Add a fifth member (`string SanitizeName(string name);`) to `IPluginStore`, rebuild, and confirm `IPluginStore_DeclaresExactlyTheSinkPathMembers` FAILS. Paste the output. Remove it.

This pin is the whole point of the task — if it cannot fail, the contract can be widened silently.

- [ ] **Step 7: Run the full suite and commit**

```bash
dotnet build Scrinia.sln
dotnet test Scrinia.sln
```
Expected: build 0 errors; 2663 passed / 0 failed / 4 skipped (2660 + 3 new).

```bash
git add src/Scrinia.Core/IPluginStore.cs src/Scrinia.Core/IMemoryStore.cs tests/Scrinia.Tests/PluginStoreContractTests.cs
git commit -m "refactor(core): add IPluginStore and derive IMemoryStore from it"
```

---

# Task 2: `ReadForAppendAsync` takes the narrow contract

**Files:**
- Modify: `src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs:93-94`
- Test: `tests/Scrinia.Tests/PluginStoreOnlySinkTests.cs` (created here, extended in Task 3)

**Interfaces:**
- Consumes: `IPluginStore` (Task 1).
- Produces: `MemoryArtifactSource.ReadForAppendAsync(IPluginStore store, string qualifiedName, CancellationToken ct)` returning `(string Scope, string Subject, string[] Elements)`. Task 3's sinks call it.

**Context:** This helper is why M3's first measurement was wrong twice. Three of the four plugin-shaped sinks reach their store members *only* through it, so a `store.` grep scoped to a sink's own file finds nothing. Its body uses exactly the four `IPluginStore` members and no others — verified at `089f892`.

Its three call sites all pass an `IMemoryStore`, which upcasts implicitly, so they need no edit:
`CoreEmbeddingEventHandler.cs:51`, `EmbeddingsPlugin.cs:351`, `BuiltInEmbeddingsService.cs:181`.

- [ ] **Step 1: Write the failing test**

```csharp
// tests/Scrinia.Tests/PluginStoreOnlySinkTests.cs
using Scrinia.Core;
using Scrinia.Core.Embeddings;

namespace Scrinia.Tests;

/// <summary>
/// The load-bearing assertion of this branch: code on the sink path must work with a store that
/// is ONLY an IPluginStore. PluginStoreOnly deliberately does NOT implement IMemoryStore — if
/// someone widens the sink path back to the full store, this file stops compiling, which is a
/// louder failure than an assertion.
/// </summary>
public class PluginStoreOnlySinkTests
{
    /// <summary>Records which read path was taken, so the ephemeral routing is pinned too.</summary>
    private sealed class PluginStoreOnly(bool ephemeral, string artifact) : IPluginStore
    {
        public bool ResolveCalled { get; private set; }
        public bool ReadCalled { get; private set; }

        public (string Scope, string Subject) ParseQualifiedName(string name)
        {
            int i = name.IndexOf(':', StringComparison.Ordinal);
            return i < 0 ? ("local", name) : (name[..i], name[(i + 1)..]);
        }

        public bool IsEphemeral(string name) => ephemeral;

        public Task<string> ResolveArtifactAsync(string nameOrArtifact, CancellationToken ct = default)
        {
            ResolveCalled = true;
            return Task.FromResult(artifact);
        }

        public Task<string> ReadArtifactAsync(string subject, string scope, CancellationToken ct = default)
        {
            ReadCalled = true;
            return Task.FromResult(artifact);
        }
    }

    [Fact]
    public async Task ReadForAppendAsync_AcceptsAStoreThatIsOnlyAnIPluginStore()
    {
        var store = new PluginStoreOnly(ephemeral: false, artifact: "");

        var (scope, subject, elements) = await MemoryArtifactSource.ReadForAppendAsync(
            store, "local:probe", CancellationToken.None);

        scope.Should().Be("local");
        subject.Should().Be("probe");
        elements.Should().BeEmpty("an empty artifact decodes to no elements");
        store.ReadCalled.Should().BeTrue("a non-ephemeral name reads by subject and scope");
        store.ResolveCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ReadForAppendAsync_RoutesAnEphemeralNameThroughResolve()
    {
        var store = new PluginStoreOnly(ephemeral: true, artifact: "");

        await MemoryArtifactSource.ReadForAppendAsync(store, "~probe", CancellationToken.None);

        store.ResolveCalled.Should().BeTrue(
            "ephemerality is a property of the NAME, so the ephemeral branch resolves rather " +
            "than reading by scope — narrowing the parameter must not change that routing");
        store.ReadCalled.Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~PluginStoreOnlySinkTests`
Expected: FAIL to compile — `PluginStoreOnly` is not an `IMemoryStore`, so it cannot be passed to `ReadForAppendAsync` (`CS1503`). That is the failure.

- [ ] **Step 3: Narrow the parameter**

In `src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs`, change the signature at `:93-94`:

```csharp
    public static async Task<(string Scope, string Subject, string[] Elements)> ReadForAppendAsync(
        IPluginStore store, string qualifiedName, CancellationToken ct)
```

Nothing in the body changes — it already uses only the four members.

- [ ] **Step 4: Run and verify the tests pass, then the full suite**

Run: `dotnet test tests/Scrinia.Tests --filter FullyQualifiedName~PluginStoreOnlySinkTests`
Expected: PASS, 2 of 2.

Then `dotnet build Scrinia.sln && dotnet test Scrinia.sln`.
Expected: build 0 errors, 2665 passed / 0 failed / 4 skipped. **No call site should have needed an edit** — if one did, say which and why in your report, because that contradicts the measured upcast claim.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs tests/Scrinia.Tests/PluginStoreOnlySinkTests.cs
git commit -m "refactor(core): ReadForAppendAsync takes IPluginStore"
```

---

# Task 3: The sink contract narrows

**Files:**
- Create: `src/Scrinia.Core/CoreSinkStore.cs`
- Modify: `src/Scrinia.Core/IMemoryEventSink.cs:10-12`
- Modify (7 production sinks): `src/Scrinia.Core/CompositeEventSink.cs:7`, `src/Scrinia.Core/EntityTypingEventHandler.cs:16`, `src/Scrinia.Core/MaintenanceEventSink.cs:45`, `src/Scrinia/Services/CoreEmbeddingEventHandler.cs:17`, `src/Scrinia/Services/McpPluginHost.cs:21`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs:25`, `src/Scrinia.Server/Services/BuiltInEmbeddingsService.cs:18`
- Modify (9 test doubles): `tests/Scrinia.TestDoubles/SilentEventSink.cs:35`, `tests/Scrinia.Tests/CompositeEventSinkTests.cs:59` and `:73`, `tests/Scrinia.Tests/Embeddings/EmbeddingReindexerNamespacedScopeTests.cs:117`, `tests/Scrinia.Tests/Embeddings/EmbeddingReindexerTests.cs:216`, `tests/Scrinia.Tests/MemoryEventSinkContextInstallDefaultTests.cs:121` and `:136`, `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:624`, `tests/Scrinia.Server.Tests/EventPipelineParityTests.cs:360`
- Test: `tests/Scrinia.Tests/PluginStoreOnlySinkTests.cs` (extend), `tests/Scrinia.Tests/CoreSinkStoreTests.cs` (create)

**Interfaces:**
- Consumes: `IPluginStore` (Task 1), `ReadForAppendAsync(IPluginStore, …)` (Task 2).
- Produces: `IMemoryEventSink` whose three methods take `IPluginStore`; `CoreSinkStore.Require(IPluginStore store, string handler)` returning `IMemoryStore`.

**Context:** This is one atomic change — narrowing the interface breaks all 16 implementations at once, so they move together. Five of seven production sinks need nothing but a parameter type. Two need the full store and cast back:

| Handler | Members it needs |
|---|---|
| `EntityTypingEventHandler` | `GetEntityFactStore`, `LoadIndex`, `ParseQualifiedName`, `SanitizeName`, `TryUpdateEntry` |
| `MaintenanceEventSink` | `LoadIndex`, `SanitizeName`, `ParseQualifiedName`, `TryUpdateEntry`, `FormatQualifiedName`, `AnyScoped` |

Two handlers × three methods = **six cast sites**, which is why the cast and its message live in one helper rather than being written out six times.

- [ ] **Step 1: Write the failing tests**

```csharp
// tests/Scrinia.Tests/CoreSinkStoreTests.cs
using Scrinia.Core;

namespace Scrinia.Tests;

/// <summary>
/// Core's own handlers need more of the store than the sink contract declares, so they cast back
/// through CoreSinkStore.Require. It THROWS rather than skipping: silently not typing entities or
/// not maintaining cross-memory references is invisible data loss, and CompositeEventSink's
/// per-sink isolation already turns the throw into one logged sink failure.
/// </summary>
public class CoreSinkStoreTests
{
    private sealed class NotAMemoryStore : IPluginStore
    {
        public (string Scope, string Subject) ParseQualifiedName(string name) => ("local", name);
        public bool IsEphemeral(string name) => false;
        public Task<string> ResolveArtifactAsync(string n, CancellationToken ct = default) => Task.FromResult("");
        public Task<string> ReadArtifactAsync(string s, string sc, CancellationToken ct = default) => Task.FromResult("");
    }

    [Fact]
    public void Require_ReturnsTheStoreWhenItIsAFullMemoryStore()
    {
        using var scope = new TestHelpers.StoreScope();
        IPluginStore store = MemoryStoreContext.Current!;

        CoreSinkStore.Require(store, "TestHandler").Should().BeSameAs(store);
    }

    [Fact]
    public void Require_ThrowsNamingTheHandlerWhenTheStoreIsNarrow()
    {
        var act = () => CoreSinkStore.Require(new NotAMemoryStore(), "EntityTypingEventHandler");

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*EntityTypingEventHandler*",
                because: "a bare cast failure would not say which handler could not run");
    }
}
```

Extend `PluginStoreOnlySinkTests` with the pipeline-level assertion:

```csharp
    /// <summary>A plugin-shaped sink: records what it was handed, needs nothing beyond the contract.</summary>
    private sealed class NarrowSink : IMemoryEventSink
    {
        public string? LastStored { get; private set; }

        public Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct)
        {
            LastStored = store.ParseQualifiedName(qualifiedName).Subject;
            return Task.CompletedTask;
        }

        public Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;

        public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct)
            => Task.CompletedTask;
    }

    [Fact]
    public async Task ASinkCanBeDrivenWithAStoreThatIsOnlyAnIPluginStore()
    {
        var sink = new NarrowSink();
        var composite = new CompositeEventSink([sink]);

        await composite.OnStoredAsync("local:probe", ["x"], new PluginStoreOnly(false, ""), CancellationToken.None);

        sink.LastStored.Should().Be("probe",
            because: "this is the assertion the narrowing exists to make true — the pipeline must " +
                     "carry a store that is not an IMemoryStore at all");
    }
```

- [ ] **Step 2: Run and verify failure**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CoreSinkStoreTests|FullyQualifiedName~PluginStoreOnlySinkTests"`
Expected: FAIL to compile — `CoreSinkStore` does not exist, and `NarrowSink` does not satisfy `IMemoryEventSink` while its methods still take `IMemoryStore`.

- [ ] **Step 3: Create the cast helper**

```csharp
// src/Scrinia.Core/CoreSinkStore.cs
namespace Scrinia.Core;

/// <summary>
/// The one place Core's internal event handlers widen the narrowed sink contract back to the full
/// store. <see cref="IMemoryEventSink"/> declares <see cref="IPluginStore"/> because that is what a
/// plugin-shaped sink needs; <c>EntityTypingEventHandler</c> and <c>MaintenanceEventSink</c> are not
/// plugin-shaped — they are Core's own maintenance code and need five and six members respectively.
///
/// <para>Two handlers × three sink methods is six cast sites, so the cast and its message live here
/// rather than being written out six times. It throws rather than returning null or skipping,
/// because a handler that silently does not run is invisible data loss;
/// <see cref="CompositeEventSink"/> isolates per-sink exceptions, so the blast radius is one logged
/// sink failure.</para>
/// </summary>
internal static class CoreSinkStore
{
    public static IMemoryStore Require(IPluginStore store, string handler) =>
        store as IMemoryStore
        ?? throw new InvalidOperationException(
            $"{handler} is a Core-internal event handler and requires the full {nameof(IMemoryStore)}, " +
            $"not the narrowed {nameof(IPluginStore)} the sink contract declares. It was handed a " +
            $"{store.GetType().Name}.");
}
```

- [ ] **Step 4: Narrow the interface**

`src/Scrinia.Core/IMemoryEventSink.cs`, replacing `:10-12`:

```csharp
    Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct);
    Task OnAppendedAsync(string qualifiedName, string content, IPluginStore store, CancellationToken ct);
    Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IPluginStore store, CancellationToken ct);
```

- [ ] **Step 5: Update the five sinks that need nothing more**

`CompositeEventSink`, `CoreEmbeddingEventHandler`, `McpPluginHost`, `EmbeddingsPlugin`, `BuiltInEmbeddingsService`: change `IMemoryStore store` to `IPluginStore store` in the three method signatures. **No body changes.** `CompositeEventSink` forwards the parameter; the other four use only contract members or `ReadForAppendAsync`.

`EmbeddingsPlugin.ReindexStoreAsync` and `BuiltInEmbeddingsService.ReindexStoreAsync` keep `IMemoryStore` — they are `IEmbeddingsAdmin`, not the sink path, and they call `ListScoped`. Do not touch them.

- [ ] **Step 6: Update the two Core handlers**

In `EntityTypingEventHandler` and `MaintenanceEventSink`, narrow the three signatures and open each method body by widening. For example, in `MaintenanceEventSink.OnStoredAsync`:

```csharp
    public Task OnStoredAsync(string qualifiedName, string[] content, IPluginStore store, CancellationToken ct)
    {
        var full = CoreSinkStore.Require(store, nameof(MaintenanceEventSink));
        // ... existing body, reading `full` where it read `store`
    }
```

Apply the same shape to all three methods of both handlers — six sites. Where a method delegates to a private helper that takes `IMemoryStore`, pass `full`; do not narrow the private helpers.

- [ ] **Step 7: Update the nine test doubles**

Parameter type only, in each of the nine listed under **Files**. None of them read the store beyond what the contract offers — if one does, report it rather than widening the contract to suit a test double.

- [ ] **Step 8: Run and verify the tests pass**

Run: `dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CoreSinkStoreTests|FullyQualifiedName~PluginStoreOnlySinkTests"`
Expected: PASS, 5 of 5.

- [ ] **Step 9: Falsify both new pins**

1. Change `CoreSinkStore.Require` to `store as IMemoryStore!` (returning null instead of throwing) and confirm `Require_ThrowsNamingTheHandlerWhenTheStoreIsNarrow` FAILS. Restore.
2. Widen `NarrowSink.OnStoredAsync` to take `IMemoryStore` and confirm the file no longer compiles — the compile error IS the pin. Restore.

Paste both outputs.

- [ ] **Step 10: Run the full suite and commit**

```bash
dotnet build Scrinia.sln
dotnet test Scrinia.sln
```
Expected: build 0 errors, 2668 passed / 0 failed / 4 skipped.

Every call site that hands a store to a sink — `MemoryTools.Core.cs:224,953,982,1015,1083,1346`, `ScriniaCommands.Migration.cs:314`, `EventSinkOperationHook`, `EmbeddingReindexer.ReindexViaSinkAsync` — passes an `IMemoryStore` where an `IPluginStore` is now wanted, which upcasts implicitly. **None should need an edit.** If any did, name it and why.

```bash
git add src/Scrinia.Core src/Scrinia src/Scrinia.Plugin.Embeddings src/Scrinia.Server tests/
git commit -m "refactor(core)!: IMemoryEventSink hands sinks IPluginStore, not the whole store"
```

---

# Task 4: Record the break and the superseded design

**Files:**
- Modify: `CHANGELOG.md`
- Modify: `docs/superpowers/specs/2026-08-18-published-surface-coherence-design.md` (Part C header)

**Context:** `IMemoryEventSink` is public, so its signature change is source-breaking for any out-of-tree sink. The previous branch established that out-of-tree implementers are a distinct CHANGELOG audience from MCP and REST callers; this entry belongs to that audience.

- [ ] **Step 1: Add the CHANGELOG entry**

Under `## [Unreleased]` → `### Changed (breaking)`:

```markdown
### Changed (breaking) — `IMemoryEventSink` receives `IPluginStore`, not `IMemoryStore`

- `IMemoryEventSink.OnStoredAsync`, `OnAppendedAsync` and `OnForgottenAsync` now take
  `Scrinia.Core.IPluginStore` — `ParseQualifiedName`, `IsEphemeral`, `ResolveArtifactAsync`,
  `ReadArtifactAsync` — instead of the full `IMemoryStore`. Source-breaking for any out-of-tree
  sink; the fix is a parameter type change. A sink needing more can cast to `IMemoryStore`, which
  every concrete store still implements.
- `MemoryArtifactSource.ReadForAppendAsync` takes `IPluginStore` for the same reason.
- `IMemoryStore` now derives from `IPluginStore`. No change is required of store implementations:
  the four members moved to the base interface and are inherited back.
```

- [ ] **Step 2: Mark Part C superseded**

In the published-surface-coherence spec, extend the existing Part C banner with one line naming its replacement:

```markdown
> **Superseded by `docs/superpowers/specs/2026-08-18-plugin-contract-narrowing-design.md` (2026-08-18).**
> That spec establishes there is no external plugin audience, so the assembly move, the
> `ProjectReference` reversal and the `AssemblyVersion` pin are all withdrawn. What survives —
> narrowing the sink contract — is implemented there without moving any type between assemblies.
```

- [ ] **Step 3: Run the full suite and commit**

```bash
dotnet test Scrinia.sln
git add CHANGELOG.md docs/superpowers/specs/2026-08-18-published-surface-coherence-design.md
git commit -m "docs: record the sink contract break and supersede Part C"
```

---

## Self-review notes

**Spec coverage.** Design §1 → Task 1. §2 → Tasks 2 and 3. §3 (the casts) → Task 3 Steps 3 and 6. §4 (call sites) → Task 3 Step 10, stated as a claim to verify rather than an assumption. §5 (16 implementations) → Task 3 Steps 5–7. Testing §, all four bullets → Tasks 1–3, each with a mutation-falsification step. Non-goals → Global Constraints. Nothing in the spec lacks a task.

**One deliberate deviation from the spec's letter.** The spec describes the throw inline in each handler; this plan puts it in `CoreSinkStore` because the site count is six, not two. Same behaviour, one definition.

**Not covered, deliberately.** The spec's "what this leaves on the table" section is context for a future branch, not work.
