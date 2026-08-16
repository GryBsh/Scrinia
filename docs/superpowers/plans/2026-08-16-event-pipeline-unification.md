# Event Pipeline Unification Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make every write to the server fire the same event sinks, whichever door it arrived at, and stop maintenance disappearing when embeddings are unavailable.

**Architecture:** Sinks are registered individually rather than as one pre-composed object, so maintenance can be registered unconditionally and so the REST-side adapter can filter. One `IMemoryOperationHook` adapter carries the sink set onto the REST path, skipping sinks already registered as hooks so nothing fires twice.

**Tech Stack:** .NET 10.0 preview, ASP.NET Core minimal APIs + DI, xUnit 2.9.3, FluentAssertions, Kestrel loopback for HTTP tests.

**Spec:** `docs/superpowers/specs/2026-08-16-event-pipeline-unification-design.md` (commit `a719147`)

## Global Constraints

- **`IMemoryOperationHook` is a PUBLISHED plugin contract** (`src/Scrinia.Plugin.Abstractions/`). Do not add, remove, or change any member of it, or of the `Before*Context`/`After*Context` types. The adapter implements the existing interface; it does not extend it.
- **Nothing may fire twice.** `BuiltInEmbeddingsService` implements both `IMemoryOperationHook` and `IMemoryEventSink`. The documented guarantee at `docs/architecture/overview.md:138` is "A plugin implementing both interfaces fires only once per operation" and it must still hold at the end of this branch.
- **The adapter implements no `OnBefore*` method.** `IMemoryEventSink` is after-only and cannot veto; inventing a before-phase would give sinks a cancel power they do not have.
- **`global using Xunit;` is already in `tests/Scrinia.Tests/GlobalUsings.cs`.** Do not add `using Xunit;` to a new test file there.
- **Kestrel, never `TestServer`, for anything driving `HttpMemoryStore`.** It sends synchronously and `TestHost.ClientHandler.Send` throws unconditionally.
- Build with `dotnet build Scrinia.sln`. Do NOT pass `-p:NuGetAudit=false`.
- Assertions use FluentAssertions with a `because:` message saying what a failure would mean.
- Do not modify a pre-existing test file except where a task explicitly says so.

---

### Task 1: Move `EntityTypingEventHandler` into Core

**Files:**
- Move: `src/Scrinia/Services/EntityTypingEventHandler.cs` → `src/Scrinia.Core/EntityTypingEventHandler.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Scrinia.Core.EntityTypingEventHandler`, constructor `EntityTypingEventHandler(IEmbeddingProvider provider)`, implementing `IMemoryEventSink`. Task 3 registers it in the server.

**Why this is task 1.** `Scrinia.Server.csproj` references `Scrinia.Core`, `Scrinia.Mcp`, and `Scrinia.Plugin.Abstractions` — **not** `src/Scrinia` (assembly `scri`). The server cannot reference this type where it currently lives, so D-3 is blocked until it moves.

**This is a pure move.** No behaviour change, no member change.

- [ ] **Step 1: Move the file and change its namespace**

Use `git mv` so history follows the file:

```bash
git mv src/Scrinia/Services/EntityTypingEventHandler.cs src/Scrinia.Core/EntityTypingEventHandler.cs
```

Then in the moved file change the namespace declaration from `namespace Scrinia.Services;` to `namespace Scrinia.Core;`, and delete the now-redundant `using Scrinia.Core;` line. Leave `using Scrinia.Core.Embeddings;`, `using Scrinia.Core.Models;` and `using Scrinia.Core.Search;` in place. **Change nothing else in the file** — not the class body, not the doc comment, not the `#pragma warning disable CA1711`.

- [ ] **Step 2: Build and confirm zero call-site changes were needed**

Run: `dotnet build Scrinia.sln`
Expected: 0 errors, with **no other file edited**.

This is the check that matters. All three construction sites — `src/Scrinia/Commands/WorkspaceSetup.cs:402` and `:448`, and `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs` / `EntityTypingWiringTests.cs` — already carry `using Scrinia.Core;`, so the type resolves at its new home untouched.

If the build fails, do NOT start editing call sites to make it pass. Report what failed — an unexpected dependency means this move needs a different shape and the plan is wrong.

Note `WorkspaceSetup.cs` still needs its `using Scrinia.Services;` for `CoreEmbeddingEventHandler`, which stays in the CLI project. Do not remove it.

- [ ] **Step 3: Run the suites that cover this type**

Run: `dotnet test tests/Scrinia.Tests`
Expected: green, same counts as before the move (1691 passed / 4 skipped). `EntityTypingEventHandlerTests` and `EntityTypingWiringTests` must pass **unmodified** — that is what proves this was a pure move.

- [ ] **Step 4: Commit**

```bash
git add -A src/Scrinia.Core/EntityTypingEventHandler.cs src/Scrinia/Services/
git commit -m "refactor(core): move EntityTypingEventHandler to Core so the server can reach it"
```

---

### Task 2: Register sinks individually, and maintenance unconditionally

**Files:**
- Modify: `src/Scrinia.Server/Program.cs` (`:151-155` composite registration, `:297` consumption)
- Create: `tests/Scrinia.Server.Tests/ServerSinkRegistrationTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `IMemoryEventSink` is now registered **many times** (once per sink) rather than once as a composite. Anything resolving it must use `GetServices<IMemoryEventSink>()`, not `GetService<IMemoryEventSink>()`. Task 3 depends on this.

**The defect.** There is exactly one `AddSingleton<IMemoryEventSink>` in the server, at `Program.cs:151`, nested three deep:

```
if (!pluginProvidesEmbeddings)              // :107
    try {                                    // :109
        if (embeddingProvider.IsAvailable)   // :127
            AddSingleton<IMemoryEventSink>(new CompositeEventSink([...]));   // :151
```

Both fallbacks — `else` at `:162`, `catch` at `:171` — register only an `ISearchScoreContributor`. `MaintenanceEventSink` is constructed with `new MaintenanceEventSink()`, takes no arguments, and has no relationship to embeddings. So ref-linking and orphan marking vanish whenever the model is missing, initialisation throws, or **a plugin provides embeddings** — the last being a supported configuration.

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Server.Tests/ServerSinkRegistrationTests.cs`. These assert on the resolved DI graph, not on HTTP behaviour.

Read `tests/Scrinia.Server.Tests/ScriniaServerFactory.cs` first and follow its configuration mechanism. The three cases are driven by settings:

```csharp
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using Scrinia.Core;

namespace Scrinia.Server.Tests;

/// <summary>
/// MaintenanceEventSink has no dependency on embeddings — it is constructed with
/// `new MaintenanceEventSink()`. It was nevertheless registered inside the embeddings block, so
/// ref-linking and orphan marking disappeared from the server whenever embeddings did: model not
/// downloaded, initialisation threw, or a plugin supplied embeddings instead. The last is a
/// supported configuration, which made installing a plugin silently disable orphan marking.
/// </summary>
public sealed class ServerSinkRegistrationTests
{
    [Fact]
    public void MaintenanceSink_IsRegistered_WhenEmbeddingsAreAvailable()
    {
        using var factory = new ScriniaServerFactory();
        var sinks = factory.Services.GetServices<IMemoryEventSink>().ToList();

        sinks.Should().ContainSingle(s => s is MaintenanceEventSink,
            because: "ref-linking and orphan marking are the server's baseline maintenance");
    }

    [Fact]
    public void MaintenanceSink_IsStillRegistered_WhenTheEmbeddingModelIsUnavailable()
    {
        // NoEmbeddingsServerFactory: see below — points the embedding provider at a model that
        // does not exist, so IsAvailable is false and Program.cs takes its `else` branch.
        using var factory = new NoEmbeddingsServerFactory();
        var sinks = factory.Services.GetServices<IMemoryEventSink>().ToList();

        sinks.Should().ContainSingle(s => s is MaintenanceEventSink,
            because: "maintenance takes no constructor arguments and has nothing to do with "
                   + "embeddings — an absent model must not disable orphan marking");
    }
}
```

**VERIFIED, so do not go looking for an easier route:** `ScriniaServerFactory` is `sealed`, applies its settings inside its own `ConfigureWebHost`, and exposes **no** per-instance settings hook — there is no `WithSetting` method to call. It also cannot be subclassed.

So the second and third cases need their own `WebApplicationFactory<Program>` subclass in this test file, following the existing pattern of `PagingRateLimitServerFactory` (`RemoteLoadIndexTests.cs:791`) and `PatchEndpointTests.ReadOnlyClaimsServerFactory`: a small sealed class with its own `ConfigureWebHost` that sets `Scrinia:DataDir` and `Scrinia:Stores:{name}` to a temp dir, plus whatever setting forces the case under test.

**Do NOT modify `ScriniaServerFactory`.** `RateLimitTests.Exceeding_rate_limit_returns_429` and others pin its behaviour, and it is a pre-existing test file.

Determine the right key for "no embeddings" by reading how `EmbeddingProviderFactory.Create` decides `IsAvailable` — do not guess the setting name.

Write a third test for the plugin-provides-embeddings case if you can construct it without a real plugin assembly. **If you cannot, say so explicitly in your report and do not fake it** — an untestable case reported honestly is worth more than a test that asserts nothing. Cover the two you can.

- [ ] **Step 2: Run them and record the failures**

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~ServerSinkRegistration"`
Expected: the first test may pass already (embeddings available is the happy path); the unavailable-model test must FAIL with zero `MaintenanceEventSink` resolved. Record the exact observed result for each.

- [ ] **Step 3: Split the composite into individual registrations**

In `src/Scrinia.Server/Program.cs`, replace the composite at `:151-155`:

```csharp
            builder.Services.AddSingleton<IMemoryEventSink>(sp =>
                sp.GetRequiredService<BuiltInEmbeddingsService>());
```

— that is, inside the embeddings-available branch, register **only** the embeddings service as a sink. Then, **outside every embeddings conditional** (alongside the other dependency-free registrations near `builder.Services.AddSingleton<IStorageBackend, FilesystemBackend>()`), add:

```csharp
// Maintenance takes no dependencies and has nothing to do with embeddings. It used to be
// registered inside the embeddings block, so ref-linking and orphan marking silently vanished
// whenever the model was missing, initialisation threw, or a plugin supplied embeddings — the
// last being a supported configuration, which meant installing a plugin turned off orphan
// marking. Registered here so none of those can reach it.
builder.Services.AddSingleton<IMemoryEventSink>(_ => new MaintenanceEventSink());
```

- [ ] **Step 4: Fix the consumption site**

`Program.cs:297` currently reads:

```csharp
            MemoryEventSinkContext.Current = httpContext.RequestServices.GetService<IMemoryEventSink>();
```

With several individual registrations, `GetService<T>()` returns only the **last** one, silently dropping the rest. Change it to compose all of them:

```csharp
            // GetServices, not GetService: sinks are registered individually (so maintenance can
            // be registered unconditionally), and GetService would return only the last one.
            MemoryEventSinkContext.Current = new CompositeEventSink(
                [.. httpContext.RequestServices.GetServices<IMemoryEventSink>()]);
```

Add `using Scrinia.Core;` if it is not already present.

- [ ] **Step 5: Run the tests and confirm they pass**

Run: `dotnet test tests/Scrinia.Server.Tests --filter "FullyQualifiedName~ServerSinkRegistration"`
Expected: PASS. Then `dotnet test tests/Scrinia.Server.Tests` in full — expected green, report counts.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Server/Program.cs tests/Scrinia.Server.Tests/ServerSinkRegistrationTests.cs
git commit -m "fix(server): register maintenance outside the embeddings block"
```

---

### Task 3: The adapter, and entity typing server-side

**Files:**
- Create: `src/Scrinia.Server/Services/EventSinkOperationHook.cs`
- Modify: `src/Scrinia.Server/Program.cs` (register the adapter; register `EntityTypingEventHandler`)

**Interfaces:**
- Consumes: Task 1's `Scrinia.Core.EntityTypingEventHandler`; Task 2's individual `IMemoryEventSink` registrations.
- Produces: `EventSinkOperationHook`, an `IMemoryOperationHook` that Task 4's parity tests exercise.

- [ ] **Step 1: Write the adapter**

Create `src/Scrinia.Server/Services/EventSinkOperationHook.cs`:

```csharp
using Scrinia.Core;
using Scrinia.Plugin.Abstractions;

namespace Scrinia.Server.Services;

/// <summary>
/// Carries the registered <see cref="IMemoryEventSink"/> set onto the REST path.
///
/// <para>The server has two write doors — REST via <see cref="PluginPipeline"/>, and the hosted
/// MCP endpoint via <c>MemoryEventSinkContext</c> — and they used different mechanisms, so a REST
/// write got embeddings but no ref-linking or orphan marking while the identical write through
/// MCP, in the same process against the same store, got both. The split itself is deliberate and
/// documented (docs/architecture/overview.md): it is what stops a component that implements both
/// interfaces firing twice. Only the membership was wrong.</para>
///
/// <para><b>The exclusion rule is the whole point.</b> A sink that is ALSO registered as an
/// <see cref="IMemoryOperationHook"/> — <c>BuiltInEmbeddingsService</c> is — already runs on this
/// path in its own right, so firing it from here would embed every REST write twice. Filtering
/// here keeps the documented once-per-operation guarantee while making the sink set identical
/// across both doors, and it puts that rule in ONE place instead of requiring every future sink
/// to remember which interfaces to implement.</para>
///
/// <para>After-phases only. <see cref="IMemoryEventSink"/> is an after-the-fact notification with
/// no veto; implementing <c>OnBefore*</c> here would hand sinks a cancel power the contract does
/// not give them.</para>
/// </summary>
public sealed class EventSinkOperationHook : IMemoryOperationHook
{
    private readonly IMemoryEventSink[] _sinks;

    public EventSinkOperationHook(IEnumerable<IMemoryEventSink> sinks)
    {
        ArgumentNullException.ThrowIfNull(sinks);
        _sinks = sinks.Where(s => s is not IMemoryOperationHook).ToArray();
    }

    /// <summary>Runs after the hooks that do the primary work.</summary>
    public int Order => 100;

    public async Task OnAfterStoreAsync(AfterStoreContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var sink in _sinks)
            await sink.OnStoredAsync(context.QualifiedName, context.Content, context.Store, ct);
    }

    public async Task OnAfterAppendAsync(AfterAppendContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var sink in _sinks)
            await sink.OnAppendedAsync(context.Name, context.Content, context.Store, ct);
    }

    public async Task OnAfterForgetAsync(AfterForgetContext context, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        foreach (var sink in _sinks)
            await sink.OnForgottenAsync(context.Name, context.WasDeleted, context.Store, ct);
    }
}
```

**Verify the context property names against `src/Scrinia.Plugin.Abstractions/` before assuming them.** `PluginPipeline.cs:59-67, 127-134, 166-171` shows what each context is populated with. Note `AfterStoreContext` carries `QualifiedName` while `AfterAppendContext` and `AfterForgetContext` carry `Name` — that asymmetry is pre-existing and deliberate for this branch; pass each through as-is and do not "fix" it.

- [ ] **Step 2: Register the adapter**

In `Program.cs`, outside every embeddings conditional (next to Task 2's maintenance registration):

```csharp
builder.Services.AddSingleton<IMemoryOperationHook>(sp =>
    new EventSinkOperationHook(sp.GetServices<IMemoryEventSink>()));
```

- [ ] **Step 3: Register entity typing server-side**

Inside the embeddings-available branch (this one genuinely depends on a provider), next to the embeddings sink registration:

```csharp
            // Parity with the CLI, which has run this since WorkspaceSetup. Safe only because the
            // store-contract branch fixed remote listing truncation first: PromoteAsync reads the
            // existing typed vocabulary through GetEntityFactStore and PERSISTS its promotions, so
            // against a store that returned only the first 200 entries it wrote durable corruption.
            builder.Services.AddSingleton<IMemoryEventSink>(sp =>
                new EntityTypingEventHandler(sp.GetRequiredService<IEmbeddingProvider>()));
```

Add `using Scrinia.Core.Embeddings;` if needed.

- [ ] **Step 4: Build and run the server suite**

Run: `dotnet build Scrinia.sln` then `dotnet test tests/Scrinia.Server.Tests`
Expected: 0 errors, green. Report counts.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Services/EventSinkOperationHook.cs src/Scrinia.Server/Program.cs
git commit -m "fix(server): one adapter carries the sinks onto the REST path"
```

---

### Task 4: Prove the doors agree

**Files:**
- Create: `tests/Scrinia.Server.Tests/EventPipelineParityTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-3.
- Produces: nothing.

**The hazard.** Tasks 2 and 3 already fixed the behaviour, so every test here passes on arrival. A test never observed failing is not evidence — the previous branch in this project shipped exactly that (a guard test that passed with the guard removed) and it was caught only by deliberately breaking the code. **Step 3 is mandatory.**

- [ ] **Step 1: Read the existing MCP-endpoint harness**

`tests/Scrinia.Server.Tests/McpEndpointTests.cs` already drives the server's hosted MCP endpoint. Read it and reuse its mechanism — do not invent a second way to call MCP.

- [ ] **Step 2: Write the parity tests**

Assert on **observable side effects**, not on invocation counts. `MaintenanceEventSink` writes attributes (`src/Scrinia.Core/MaintenanceEventSink.cs`): bidirectional ref links under the `ref` attribute key, and an orphan marker under `KnownAttributes.OrphanKey` with value `"true"`. Both are readable from a plain entry GET.

Minimum coverage:

1. **Ref-linking parity** — store memory `A`, then store memory `B` whose content references `A`. Assert `A` gains the inbound `ref` attribute. Run the whole scenario once entirely over REST and once entirely over the MCP endpoint, against separate stores or separate names, and assert the same outcome both ways.
2. **Orphan-marking parity** — store a memory nothing references; assert it carries the orphan marker. Both doors.
3. **No double-embedding** — a REST write must produce exactly one set of vectors, not two. This is the exclusion rule's guard: removing the `.Where(s => s is not IMemoryOperationHook)` filter must turn this test red. If you cannot observe vector counts directly, assert on whatever `VectorStore` exposes; if there is genuinely no observable, say so in your report rather than writing a test that cannot fail.

Use `because:` messages that name what a failure would mean.

- [ ] **Step 3: Prove each test can fail (MANDATORY)**

For each of the three, break the specific thing it guards, observe the failure, record the exact output, then restore:

- Ref-linking / orphan parity: revert Task 2's unconditional maintenance registration back inside the embeddings block, or remove Task 3's adapter registration. The REST half must fail; the MCP half must still pass. **That asymmetry is the point** — it reproduces the original defect exactly.
- No-double-embedding: delete the `.Where(s => s is not IMemoryOperationHook)` filter in `EventSinkOperationHook`. The test must fail.

After each, restore the file byte-for-byte and confirm with `git diff` showing no changes before committing.

Record all three observed failures verbatim in your report. Any test that stays green under its break is asserting nothing — strengthen it or report it as unfixable with the reason.

- [ ] **Step 4: Run the full server suite and commit**

Run: `dotnet test tests/Scrinia.Server.Tests`

```bash
git add tests/Scrinia.Server.Tests/EventPipelineParityTests.cs
git commit -m "test(server): the same write through both doors, and nothing fires twice"
```

---

### Task 5: Documentation and the full-solution gate

**Files:**
- Modify: `docs/architecture/overview.md` (the "Two Plugin Code Paths" section at `:138`)
- Modify: `CHANGELOG.md`

**Interfaces:**
- Consumes: Tasks 1-4.
- Produces: nothing.

- [ ] **Step 1: Correct the architecture table**

The section is titled "Two Plugin Code Paths" and its table has two rows. There are three write paths, and the third is why the divergence went unnoticed: the server hosts an MCP endpoint as well as the REST API (`Program.cs:297`), so the real discrepancy was REST-versus-MCP *inside one process*, which a CLI-versus-server table cannot express.

Rewrite the section to show all three paths and their sink membership, and to state the rule the adapter now enforces: every write fires the same sink set whichever door it arrived at, and a component registered as both a hook and a sink still fires exactly once. Keep the existing double-firing rationale — it was correct and is still load-bearing. Retitle the section if "Two" no longer fits.

- [ ] **Step 2: Changelog**

Match the file's house style exactly: **every** section heading uses the form `### <Type> — <descriptive title>`. Read the neighbouring entries first. A plain `### Fixed` does not match this file.

Two entries, both Fixed. There is no breaking change in this branch. Cover: (1) maintenance no longer disappears when embeddings are unavailable or a plugin supplies them; (2) REST writes now fire the same sinks as MCP writes, and entity typing runs server-side.

- [ ] **Step 3: Full-solution gate**

Run: `dotnet test Scrinia.sln`

Capture **failure names, not just summary lines** — filter for `[FAIL]` and `Failed!`, not `Failed!` alone. A prior branch lost a failure's identity to an over-narrow filter and could not classify it afterwards.

Expected assemblies: `Scrinia.Tests`, `Scrinia.Server.Tests`, `Scrinia.Merge.Tests`, `Scrinia.Retrieval.Benchmarks.Tests`, `Scrinia.Plugin.Embeddings.Tests`.

One known-acceptable failure: `MemoryEventSinkContextInstallDefaultTests.AParallelScopeLifecycleDoesNotDisturbDefault`, a pre-existing flake on `main` (2 failures in 16 runs on a control worktree with none of this work present). **Any other failure is this branch's** — report it plainly; do not call anything "pre-existing" or "unrelated" without verifying it, and do not modify source to make the gate pass.

Report per-assembly counts.

- [ ] **Step 4: Commit**

```bash
git add docs/architecture/overview.md CHANGELOG.md
git commit -m "docs: three write paths, and one sink set across them"
```

---

## Self-review notes

- **Spec coverage:** D-1 → Task 2. D-2 → Task 3 (adapter) + Task 4 (proof). D-3 → Task 1 (the move, which unblocks it) + Task 3 (registration). D-4 → Task 5. Testing section → Tasks 2 and 4.
- **Ordering:** Task 1 must precede Task 3 — the server cannot reference `EntityTypingEventHandler` until it moves. Task 2 must precede Task 3 — the adapter resolves `GetServices<IMemoryEventSink>()`, which is only correct once sinks are registered individually. Task 4 must follow both, since its revert-to-fail step breaks their changes specifically. No commit leaves the tree worse than `HEAD`: Tasks 2 and 4 are test-then-fix within a single commit.
- **The riskiest assumption**, flagged for the implementer rather than buried: Task 2's three DI cases depend on `ScriniaServerFactory`'s configuration surface, which the plan does not pin down because it has not been read. Task 2 Step 1 instructs reading it first and permits a dedicated factory subclass, and permits reporting the plugin-provides-embeddings case as untestable rather than faking it.
