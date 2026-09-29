# F2 Routing/Tracking Implementation Plan

> Implements the approved spec [2026-09-21-f2-routing-tracking-design.md](./specs/2026-09-21-f2-routing-tracking-design.md) — **Approach A: sunset shim then break, auto-migrate on touch, NO warning log**.
> Plan date: 2026-09-21. Sunset date for the `local-topic:` shim: **2027-03-21** (180 days from spec approval). Scope: **F2 (routing/tracking) only**.

## 0. Constraints (hold for every task)

- No public API / wire-format / behavioral change except the one approved legacy cleanup below.
- `dotnet test Scrinia.sln` **must be green after each task** (each task commits on its own green cycle).
- All new/changed tests use `TestHelpers.StoreScope`.
- `WithTools<T>` registers **all** public methods — helper methods stay `internal` or `private`.
- Source-generated JSON contexts only (`ScriniaMcpJsonContext`, `CliJsonContext`, `ConfigJsonContext`, `PluginClientJsonContext`).
- **Approved breaking legacy cleanup (logged in CHANGELOG.md Unreleased/Breaking only):**
  - `local-topic:` recovery-compat shim removed at the sunset date (2027-03-21); legacy inputs resolve to `Unknown` scope post-sunset.
  - `/agent/` `local:agent` NMP/2 entries are promoted to `.scrinia/agent/{name}.md` on recall/delete and the legacy NMP/2 entry is **deleted** on promotion.
  - Tracking precedence centralized in `EffectivePolicy.ResolveEffective`.
- Do **not** touch F1/F3/F4 (freeze legacy / eager bulk rewrite / dual-write).

## 1. Reference Signatures (exact repo spellings)

| Surface | Signature |
|---|---|
| `FileEntityMutation` | `new FileEntityMutation(string entityKind, string name, string bodyPath, string metaPath, string versionsDirectory, byte[] newBody, byte[]? newMeta, bool deleteBody, bool deleteMeta)` |
| `FileEntityMutationResult` | `new FileEntityMutationResult(WholeMemoryOutcome Outcome, string OperationId, string? Warning, RecoveryInfo? Recovery)` |
| `WholeMemoryOutcome` | `Created`, `Replaced`, `SuccessWithWarning`, `RecoveryRequired` |
| `IFileEntityTransactionCoordinator` | `Task<FileEntityMutationResult> ExecuteAsync(FileEntityMutation, CancellationToken)` |
| `IMemoryStore` | `bool Remove(MemoryAddress address)` (line 101); `Task<string> ReadArtifactAsync(MemoryAddress, CancellationToken)` (via `IPluginStore`); `IReadOnlyList<ArtifactEntry> LoadIndex(string scope="local")`; `string WorkspaceRoot { get; }` |
| `FileMemoryStore` | `ResolveAddress(MemoryAddress) → (string Scope, string Subject)` — for `/agent/ghost` returns `("local:agent", "ghost")` |
| `MemoryAddress` | `Parse(string)` / `TryParse(string?, out MemoryAddress, out MemoryAddressError)` |
| `Nmp2Strategy` | `Nmp2Strategy.Instance.Decode(string) → byte[]` (UTF-8 body bytes) |
| `PathRouter` | `TryToFileEntityPath(kind, name?, workspaceRoot, out string path) → bool`; `ToStoreDir(scope)`; `ToFileEntityDir(kind, workspaceRoot)` |
| `FileEntityName` | `IsValid(string?) → bool` |
| `MemoryNaming` | `BuildScopedTopicScope(string)` / `TryBuildScopedTopicScope(string?, out string)` |
| `WorkspaceConfig` | `ParseTracking(string?) → TrackingPolicy` (throws `ScriniaConfigurationException` on malformed); `TrackingKey = "Scrinia:Workspace:Tracking"`; `GetValue(root, key) → string?` |
| `LegacyMigrationService` | `Read(string workspaceRoot)` (bug: uses `WorkspaceConfig.LoadValuesUncached` directly); `Inspect(string)`, `Migrate(string, TrackingPolicy)`, `Set(string, TrackingPolicy)` |
| `TrackingCliCommands` | `Status(bool json)`, `Set(string policy, bool json)`, `MigrateLegacy(string to, bool json)`; uses `WorkspaceSetup.Configure(null)`, `WorkspaceSetup.Root` |
| `WorkspaceSetup` | `GetConfigValue(string key) → string?` (env single→double→file precedence, `Shared:Enabled` env-ignore special-case) |
| `ReservedPrefixGuard` (`Scrinia.Mcp`) | `Reserved` → rename to `ReservedTopicPrefixes` |

## 2. Sunset seam

- Named const in `PathRouter`:
  ```csharp
  // ADJUST IF MERGER LATER — see CHANGELOG Breaking
  private static readonly DateTimeOffset LocalTopicSunsetDate = new DateTimeOffset(2027, 3, 21, 0, 0, 0, TimeSpan.Zero);
  ```
- The `local-topic:` shim branch in `PathRouter.ToStoreDir` resolves legacy `local-topic:` journals **only when** `DateTimeOffset.UtcNow < LocalTopicSunsetDate`. After sunset the branch is removed and legacy inputs resolve to `Unknown` scope.

---

## Task 1 — `EffectivePolicy` precedence function

**Goal.** Single tracking-precedence function: single-underscore env wins over double-underscore env wins over file value wins over `Unset`. Malformed throws via `ParseTracking`.

**Changes.**
- Create `src/Scrinia.Core/Configuration/EffectivePolicy.cs`:
  ```csharp
  namespace Scrinia.Core.Configuration;

  public static class EffectivePolicy
  {
      public const string EnvSingle = "SCRINIA_WORKSPACE_TRACKING";
      public const string EnvDouble = "SCRINIA__WORKSPACE__TRACKING";
      public const string FileKey = "Scrinia:Workspace:Tracking";

      /// <summary>Single tracking-precedence function: single-underscore env wins over double-underscore env wins over file value wins over Unset. Malformed throws via ParseTracking.</summary>
      public static TrackingPolicy ResolveEffective(string? envSingle, string? envDouble, string? fileValue) =>
          WorkspaceConfig.ParseTracking(envSingle ?? envDouble ?? fileValue);
  }
  ```
- `using`/namespace alignment with `WorkspaceConfig` and `TrackingPolicy` as they already live in Core.

**Tests.** `tests/Scrinia.Tests/Configuration/EffectivePolicyTests.cs` (new):
- Precedence: single > double > file > `Unset` (with `null`s for the higher slots).
- Malformed **file** value throws `ScriniaConfigurationException`.
- Malformed **env** value throws `ScriniaConfigurationException`.
- Run via `dotnet test Scrinia.sln`.

**Commit.** Green cycle → commit Task 1.

---

## Task 2 — `local-topic:` date-gated recovery shim in `PathRouter`

**Goal.** Gate the existing read-only `local-topic:` recovery branch (current `PathRouter.cs` lines 108–110) behind the sunset date.

**Changes.** `src/Scrinia.Core/PathRouter.cs`:
- Add `using Scrinia.Core.Configuration;`.
- Add the `LocalTopicSunsetDate` const (Section 2 above) at the top of the file.
- Wrap the `local-topic:` branch in `ToStoreDir`:
  ```csharp
  // [Obsolete] Recovery-compat read-only shim — see LocalTopicSunsetDate; sunset 2027-03-21 (CHANGELOG Breaking).
  if (DateTimeOffset.UtcNow < LocalTopicSunsetDate && scope.StartsWith("local-topic:", StringComparison.Ordinal))
  {
      // existing read-only dir resolution for legacy journals
  }
  ```
  - Keep the branch **read-only** (resolve dir only). Attributes cannot be applied per-line; instead add an XML-doc note to `ToStoreDir` describing the recovery-compat shim and its sunset.
  - `StartsWith(..., StringComparison.Ordinal)` — no culture/ordinal surprises.

**Tests.** Append to `tests/Scrinia.Tests/PathRouterTests.cs`:
- `local-topic:findings` scope resolves to `.scrinia/memories/findings` pre-sunset; assert the branch returns that path.

**Commit.** Green cycle → commit Task 2.

---

## Task 3 — rename `Reserved` → `ReservedTopicPrefixes`

**Goal.** Pure rename; same 8-item set, same semantics, clearer name.

**Changes.**
- `src/Scrinia.Mcp/ReservedPrefixGuard.cs`: rename `Reserved` → `ReservedTopicPrefixes`.
- Update all callers — grep `\.Reserved` across `Scrinia.Mcp` (and any other references) and rename. No behavior change.

**Tests.** Append to the existing reserved-prefix test (or a small new test):
- Assert `ReservedTopicPrefixes` carries exactly the 8 canonical reserved topics.
- Confirm colon-guard / `Shared:Enabled` env-ignore tests stay green unchanged.

**Commit.** Green cycle → commit Task 3.

---

## Task 4 — `/agent/` auto-promote on touch

**Goal.** On disk-miss + legacy `local:agent` hit, auto-promote in two ordered steps, then delete the legacy NMP/2 entry. Idempotent. No warning log.

**Changes.** `src/Scrinia.Mcp/MemoryTools.Core.cs` (`/agent/` recall handler, lines 162–178):
- After the disk-miss branch, **before** falling through to NMP/2 store resolution, add:
  ```csharp
  // disk miss — attempt legacy local:agent auto-promote (two ordered steps)
  if (MemoryStoreContext.Current is { } agentStore &&
      agentStore.ReadArtifactAsync(address, cancellationToken).GetAwaiter().GetResult() is { } legacyArtifact)
  {
      byte[] body = Encoding.UTF8.GetBytes(Nmp2Strategy.Instance.Decode(legacyArtifact));
      string agentDir = PathRouter.ToFileEntityDir("agent", agentStore.WorkspaceRoot);
       string bodyPath = Path.Combine(agentDir, agentSubject + ".md");
       string metaPath = Path.ChangeExtension(bodyPath, ".meta.json");
       string versionsDir = Path.Combine(agentDir, "versions");

       FileEntityMutationResult publish =
           new FileEntityTransactionCoordinator(agentStore.WorkspaceRoot)
               .ExecuteAsync(
                   new FileEntityMutation("agent", agentSubject, bodyPath, metaPath, versionsDir, body, null, false, false),
                   cancellationToken).GetAwaiter().GetResult();

       if (publish.Outcome is WholeMemoryOutcome.Created or WholeMemoryOutcome.Replaced)
       {
           agentStore.Remove(address);
           return ReadFileEntityPromoted(agentSubject, bodyPath, actionLabel); // published .md content
      }
      // RecoveryRequired / throw: leave legacy intact, fall through to store resolution
  }
  ```
- Wrap the `ReadArtifactAsync` call in `try { … } catch (FileNotFoundException) { /* no legacy entry — fall through to existing NMP/2 resolution */ }`.
- Add a private helper (in the same partial):
  ```csharp
   private string ReadFileEntityPromoted(string agentSubject, string filePath, string actionLabel)
   ```
   that reads the published `.scrinia/agent/{agentSubject}.md` file and returns a `Success` response **mirroring the existing disk-hit response shape at lines 173–174** (reuse exactly).
- Reuse `FileEntityName.IsValid` where the name is validated for the mutation; keep `name` handling consistent with the existing `/skill/` handler.

**Tests.** Append to `tests/Scrinia.Tests/SkillPathAuthorityTests.cs`:
- **Auto-promote + delete:** seed a legacy `local:agent` entry (write NMP/2 artifact via `store.WriteArtifactAsync("ghost", "local:agent", Nmp2ChunkedEncoder.Encode("legacy agent body"))`), then `MemoryRecall(path:"/agent/ghost")` must:
  - return the promoted file content,
  - the legacy entry must be gone (`ReadArtifactAsync` throws),
  - `.scrinia/agent/ghost.md` must exist.
  - Second recall returns file content directly (no re-promote).
- **Disk-hit:** pre-create the file, recall returns file, no legacy touch (legacy entry, if present, stays intact).
- **Legacy-miss:** no file, no legacy entry → `NotFound` unchanged.

**Commit.** Green cycle → commit Task 4.

---

## Task 5 — env-aware `Inspect`/`Migrate`/`Set`

**Goal.** Route tracking precedence through `EffectivePolicy.ResolveEffective`; `WorkspaceSetup.GetConfigValue` backing for file reads.

**Changes.**
- `src/Scrinia.Core/Migration/LegacyMigrationState.cs`:
  - `Read(string workspaceRoot)` → `Read(string workspaceRoot, string? envSingle, string? envDouble)`.
  - Replace the `WorkspaceConfig.LoadValuesUncached` tracking-policy extraction with:
    ```csharp
    EffectivePolicy.ResolveEffective(
        envSingle, envDouble,
        WorkspaceConfig.LoadValuesUncached(workspaceRoot).GetValueOrDefault(WorkspaceConfig.TrackingKey))
    ```
    This also fixes the spec'd bug (direct `LoadValuesUncached` bypass of env precedence).
  - Same change for `Migrate`/`Set` (they call `Read` via `InspectLocked`; pass `envSingle`/`envDouble` through).
- `src/Scrinia/Commands/TrackingCommands.cs`:
  - `Status`/`Set`/`MigrateLegacy` compute raw env values:
    ```csharp
    string? envSingle = Environment.GetEnvironmentVariable(EffectivePolicy.EnvSingle);
    string? envDouble = Environment.GetEnvironmentVariable(EffectivePolicy.EnvDouble);
    ```
    and pass them to `LegacyMigrationService().Inspect(WorkspaceSetup.Root, envSingle, envDouble)`, etc.

**Tests.** Append to `tests/Scrinia.Tests/TrackingPrecedenceTests.cs`:
- Via `StoreScope`: set env `SCRINIA_WORKSPACE_TRACKING=local` and `SCRINIA__WORKSPACE__TRACKING=project-git` and a file value `local`; assert `Inspect` reports `Local` (single wins).
- Unset single, set `double=project-git`, file absent → `ProjectGit`.
- Malformed env value throws `ScriniaConfigurationException` (consistency with Task 1).

**Commit.** Green cycle → commit Task 5.

---

## Task 6 — bulk sweep helper + CHANGELOG

**Goal.** Reuse the Task-4 promotion as one helper consumed by both the recall path (demand) and the `tracking migrate-legacy` sweep (bulk); add test-only sunset seam; record the approved breaking change.

**Changes.**
- Extract the two-step promotion into a shared helper. Add a partial `src/Scrinia.Mcp/MemoryTools.Agent.cs`:
  ```csharp
  public static async Task PromoteLocalAgentAsync(IMemoryStore store, MemoryAddress address, string name, CancellationToken cancellationToken)
  ```
  containing the ordered (1) `FileEntityTransactionCoordinator.ExecuteAsync(new FileEntityMutation(...))` publish + (2) on `Created`/`Replaced` only `store.Remove(address)`. Reused by the `/agent/` recall handler (Task 4) and the bulk sweep.
   - The helper takes `MemoryAddress address` so it can pass it to `store.Remove(address)` on `Created`/`Replaced` only, deleting the legacy entry.
  - `MigrateLegacy` in `TrackingCommands` calls this helper per legacy `local:agent` entry during the sweep (one entry failure is fault-isolated — existing migrate-legacy semantics).
- Test-only sunset seam: add `public static void SetLocalTopicSunsetForTest(DateTimeOffset)` to `PathRouter` (used only by tests) to flip `LocalTopicSunsetDate`.
  - Update the Task-2 sunset-expiry test: `SetLocalTopicSunsetForTest(past)` → assert `local-topic:findings` resolves to an `Unknown`-scope path (or `ToStoreDir` throws `Unknown scope`) post-sunset.
- `CHANGELOG.md` — append under `[Unreleased]` → `### Removed (breaking)` (and `### Changed (breaking)` where the tracking/agent behavior shifts):
  - `tracking migrate-legacy` sweep no longer re-reads the file value directly; tracking precedence now follows single-underscore > double-underscore > file > `Unset` (`EffectivePolicy.ResolveEffective`).
  - `local-topic:` shim removed after **2027-03-21**; legacy `local-topic:` journals resolve to `Unknown` scope post-sunset.
  - `/agent/` legacy `local:agent` entries are promoted to `.scrinia/agent/{name}.md` on recall (and bulk-swept by `tracking migrate-legacy`); **breaking**: NMP/2 `local:agent` store entries are deleted on promotion.

**Final.**
- Run `dotnet test Scrinia.sln` (full solution, green).
- Commit Task 6.

---

## Verification gate (end state)

- All six tasks committed, each on its own green `dotnet test Scrinia.sln` cycle.
- Coverage: tracking precedence (single-underscore wins) — Inspect level; `/agent/` auto-promote idempotency; `local-topic:` recovery pre-sunset; `local-topic:` `Unknown` post-sunset; `ReservedTopicPrefixes` set unchanged; colon-guard / `Shared:Enabled` env-ignore unchanged.
- `CHANGELOG.md` Unreleased/Breaking records the two approved legacy removals (`local-topic:` shim, `/agent/` `local:agent` promotion+delete).
