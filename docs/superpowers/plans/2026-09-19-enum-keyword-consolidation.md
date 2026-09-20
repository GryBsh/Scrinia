# Enum & Keyword Consolidation (Tasks C + G) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Consolidate duplicated enum/keyword helpers within `Scrinia.Core` and standardize the published MCP status/availability vocabulary — reducing maintenance cost without changing any transactional or recovery behavior.

**Architecture:** Five small, independent consolidation tasks (C1–C3, G1–G2) plus a final full-solution build/test sweep. C1–C3 are internal deduplication in Core/Abstractions; G1–G2 are on the published MCP surface. Each task is self-contained with its own test cycle and is safe to review in isolation.

**Tech Stack:** C# 12 / .NET, xUnit test framework, System.Text.Json source-gen contexts, ConsoleAppFramework (CLI).

**Spec:** `docs/superpowers/specs/2026-09-19-enum-keyword-consolidation-design.md` (the plan argues from the spec; read both).

## Global Constraints
- No behavioral change to any transactional, recovery, or maintenance code path. The 33 `RequireText` call sites and both `TransactionalRetryPolicy` defaults must produce identical runtime behavior.
- Cross-assembly enum pairs stay duplicated (boundary-forced: Abstractions depends on Core). The only enforcement is a compile-time invariant test.
- Do NOT merge the five overlapping recovery enums (`RecoveryBlockReason`, `RecoveryEnvelopeFailure`, `MaintenanceProtectionReason`, `RestoreRecoveryFailure`, `MultiMemoryFailure`) — they span different lifecycle layers.
- Do NOT merge the different-concept retry `3`s: `RetryPolicy.cs:13` (`RetryOptions.MaxRetries`), `McpPluginHost.cs:49` (`MaxRestarts`), `FileMemoryStore.cs:1100` (store-local).
- Do NOT rename published wire strings: `"partial"`, `"success_with_warning"`, `"success"`, `"error"` on `SharedEnvelope`/`McpResponse`/`ConfigurationJsonResult`.
- `DefaultMaxAttempts` is a new public const on Core's surface; acknowledged by the spec.
- Every task ends with a green `dotnet test` for its own scope and a single focused commit.

---

## Task 1: C1 — Collapse the 11 `RequireText` definitions into one

**Files:**
- Create: `src/Scrinia.Core/RequireText.cs`
- Modify: `src/Scrinia.Core/MultiMemoryTransaction.cs` (delete `RequireText` defs at lines 123, 195, 295, 348, 461)
- Modify: `src/Scrinia.Core/MultiMemoryTransactionRecord.cs` (delete `RequireText` defs at lines 65, 93)
- Modify: `src/Scrinia.Core/RecoveryContracts.cs` (delete `RequireText` defs at lines 108, 263)
- Modify: `src/Scrinia.Core/WholeMemoryTransaction.cs` (delete `RequireText` defs at lines 59, 214)
- Test: `tests/Scrinia.Tests/RequireTextConsolidationTests.cs` (create)

**Interfaces:**
- Consumes: none (this task introduces the shared method).
- Produces: `internal static string RequireText(string value, string parameterName)` in namespace `Scrinia.Core`, callable unqualified by all existing call sites.

**Steps:**

- [ ] **Step 1: Verify all 4 files are in `namespace Scrinia.Core`.**
  Run: `rg "^namespace" src/Scrinia.Core/MultiMemoryTransaction.cs src/Scrinia.Core/MultiMemoryTransactionRecord.cs src/Scrinia.Core/RecoveryContracts.cs src/Scrinia.Core/WholeMemoryTransaction.cs`
  Expected: all four report `namespace Scrinia.Core;`. If any reports a child namespace (e.g. `namespace Scrinia.Core.Models;`) that is fine (child namespaces see parent namespace members unqualified). If any reports a completely different namespace, note it — that call site will need a `using Scrinia.Core;` after Step 3.

- [ ] **Step 2: Write the failing test** (references the shared method; it does not exist yet, so the build fails).
  Create `tests/Scrinia.Tests/RequireTextConsolidationTests.cs`:
  ```csharp
  using Scrinia.Core;

  namespace Scrinia.Tests;

  public class RequireTextConsolidationTests
  {
      [Fact]
      public void Returns_Value_When_Present()
      {
          var result = RequireText("hello", nameof(result));
          Assert.Equal("hello", result);
      }

      [Fact]
      public void Throws_When_Whitespace_With_Canonical_Message()
      {
          var ex = Assert.Throws<ArgumentException>(() => RequireText("   ", nameof(result)));
          Assert.Equal("Value cannot be null, empty, or whitespace.", ex.Message);
      }
  }
  ```
  (Note: the whitespace above is literal spaces inside the string literal — `"   "`.)

- [ ] **Step 3: Run test to verify it fails.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RequireTextConsolidationTests`
  Expected: FAIL (compile error — `RequireText` is not defined). If the test project cannot see the internal method, confirm `InternalsVisibleTo("Scrinia.Tests")` is configured on Scrinia.Core (grep `InternalsVisibleTo` in src/Scrinia.Core). If missing, add `[assembly: InternalsVisibleTo("Scrinia.Tests")]` to a source file in Scrinia.Core before continuing.

- [ ] **Step 4: Add the shared method.**
  Create `src/Scrinia.Core/RequireText.cs`:
  ```csharp
  namespace Scrinia.Core;

  internal static string RequireText(string value, string parameterName) =>
      string.IsNullOrWhiteSpace(value)
          ? throw new ArgumentException("Value cannot be null, empty, or whitespace.", parameterName)
          : value;
  ```

- [ ] **Step 5: Delete the 11 local `private static string RequireText(...)` definitions** in the four files (the exact definitions listed in Files above). Leave all call sites (`RequireText(operationId, nameof(operationId))`, etc.) untouched — they resolve to the shared method automatically because they are in `namespace Scrinia.Core`.

- [ ] **Step 6: Run test to verify it passes.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~RequireTextConsolidationTests`
  Expected: PASS (both facts). Also run the broader transaction test suite to confirm no call site broke: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MultiMemory` and `--filter FullyQualifiedName~Recovery`.

- [ ] **Step 7: Commit.**
  ```bash
  git add src/Scrinia.Core/RequireText.cs src/Scrinia.Core/MultiMemoryTransaction.cs src/Scrinia.Core/MultiMemoryTransactionRecord.cs src/Scrinia.Core/RecoveryContracts.cs src/Scrinia.Core/WholeMemoryTransaction.cs tests/Scrinia.Tests/RequireTextConsolidationTests.cs
  git commit -m "refactor: collapse 11 RequireText definitions into one Scrinia.Core helper"
  ```

---

## Task 2: C2 — Hoist the transactional-retry default `3` into a single const

**Files:**
- Modify: `src/Scrinia.Core/TransactionalRetry.cs:8-11` (add const, use it as default)
- Modify: `src/Scrinia.Plugin.Abstractions/ITransactionalMemoryParticipant.cs:41` (use const as default)
- Test: `tests/Scrinia.Tests/TransactionalRetryDefaultTests.cs` (create)

**Interfaces:**
- Consumes: none.
- Produces: `public const int DefaultMaxAttempts = 3;` in namespace `Scrinia.Core`.

**Steps:**

- [ ] **Step 1: Write the failing test.**
  Create `tests/Scrinia.Tests/TransactionalRetryDefaultTests.cs`:
  ```csharp
  using Scrinia.Core;
  using Scrinia.Plugin.Abstractions;

  namespace Scrinia.Tests;

  public class TransactionalRetryDefaultTests
  {
      [Fact]
      public void Core_Policy_Defaults_To_MaxAttempts_3()
      {
          var policy = new TransactionalRetryPolicy();
          Assert.Equal(3, policy.MaxAttempts);
          Assert.Equal(DefaultMaxAttempts, policy.MaxAttempts);
      }

      [Fact]
      public void Abstractions_Policy_Defaults_To_Same_MaxAttempts()
      {
          var policy = new TransactionalRetryPolicy();
          Assert.Equal(3, policy.MaxAttempts);
      }
  }
  ```
  (The `Scrinia.Core` using brings `DefaultMaxAttempts` and the Core policy into scope; `Scrinia.Plugin.Abstractions` brings the Abstractions policy.)

- [ ] **Step 2: Run test to verify it fails.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~TransactionalRetryDefaultTests`
  Expected: FAIL — `DefaultMaxAttempts` is not defined yet (compile error).

- [ ] **Step 3: Add the const and change both defaults.**
  In `src/Scrinia.Core/TransactionalRetry.cs`, after the namespace declaration (line 2), add:
  ```csharp
  public const int DefaultMaxAttempts = 3;
  ```
  Then change the Core overload default (line 10) from `int maxAttempts = 3` to `int maxAttempts = DefaultMaxAttempts`.
  Then change the Abstractions overload default (line 41) from `int maxAttempts = 3` to `int maxAttempts = DefaultMaxAttempts`.

- [ ] **Step 4: Run test to verify it passes.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~TransactionalRetryDefaultTests`
  Expected: PASS.

- [ ] **Step 5: Commit.**
  ```bash
  git add src/Scrinia.Core/TransactionalRetry.cs src/Scrinia.Plugin.Abstractions/ITransactionalMemoryParticipant.cs tests/Scrinia.Tests/TransactionalRetryDefaultTests.cs
  git commit -m "refactor: hoist transactional-retry default attempts into DefaultMaxAttempts const"
  ```

---

## Task 3: C3 — Add drift-invariant test for the identical cross-assembly `TransactionalFailureCode` pair

**Files:**
- Modify: `tests/Scrinia.Tests/TransactionalParticipantContractTests.cs` (append one test)

**Interfaces:**
- Consumes: `Scrinia.Core.TransactionalFailureCode`, `Scrinia.Plugin.Abstractions.TransactionalFailureCode`.
- Produces: a compile-time guard that fails the build if the two enums ever diverge.

**Steps:**

- [ ] **Step 1: Append the invariant test** to `tests/Scrinia.Tests/TransactionalParticipantContractTests.cs` (this file already uses `Enum.GetValues<...>` as a template — add after the last test method):
  ```csharp
  [Fact]
  public void TransactionalFailureCode_Core_And_Abstractions_AreIdentical()
  {
      var core = Enum.GetNames<Scrinia.Core.TransactionalFailureCode>();
      var abstractions = Enum.GetNames<Scrinia.Plugin.Abstractions.TransactionalFailureCode>();
      Assert.Equal(abstractions, core);
  }
  ```

- [ ] **Step 2: Run test to verify it passes.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~TransactionalParticipantContractTests`
  Expected: PASS (the enums are currently identical; this test locks the invariant so a future divergent addition fails the build).

- [ ] **Step 3: Commit.**
  ```bash
  git add tests/Scrinia.Tests/TransactionalParticipantContractTests.cs
  git commit -m "test: invariant — Core and Abstractions TransactionalFailureCode members must stay identical"
  ```
  Note: this is a regression guard, not a behavior change. It passes immediately.

---

## Task 4: G1 — Collapse the duplicated `SharedAvailability` enum into `SharedBaseAvailability`

**Files:**
- Modify: `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs:14` (add `[JsonStringEnumMemberName]` attributes matching the current wire spellings)
- Modify: `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs` (delete `SharedAvailability` enum; repoint `SharedBaseSummary.Availability` line 37 and `RefreshResult.Availability` line 58 to `SharedBaseAvailability`)
- Modify: `src/Scrinia.Mcp/Shared/SharedServer.cs:104,106` (update the two consumers from `SharedAvailability.Unavailable`)
- Test: `tests/Scrinia.Tests/SharedAvailabilityConsolidationTests.cs` (create)

**Interfaces:**
- Consumes: `Scrinia.Core.Configuration.SharedBaseAvailability`.
- Produces: a single source of truth for the availability enum; wire JSON spellings (`available`, `unavailable`, `stale`, `conflicted`) preserved exactly.

**Steps:**

- [ ] **Step 1: Confirm the wire spellings on the DTO enum** by reading `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs:5-11` and recording the `[JsonStringEnumMemberName("available")]` etc. values. These must be reproduced verbatim on the Core enum.

- [ ] **Step 2: Write the round-trip test** asserting the Core enum serializes to the exact wire spellings. Create `tests/Scrinia.Tests/SharedAvailabilityConsolidationTests.cs`:
  ```csharp
  using System.Text.Json;
  using Scrinia.Core.Configuration;

  namespace Scrinia.Tests;

  public class SharedAvailabilityConsolidationTests
  {
      [Theory]
      [InlineData(SharedBaseAvailability.Available, "available")]
      [InlineData(SharedBaseAvailability.Unavailable, "unavailable")]
      [InlineData(SharedBaseAvailability.Stale, "stale")]
      [InlineData(SharedBaseAvailability.Conflicted, "conflicted")]
      public void Serializes_To_Published_Wire_Spellings(SharedBaseAvailability value, string expected)
      {
          var json = JsonSerializer.Serialize(value);
          Assert.Equal(expected, json.Trim('"'));
      }
  }
  ```
  (This pins the wire contract: the enum must serialize to the same strings consumers already read.)

- [ ] **Step 3: Run test to verify it fails.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SharedAvailabilityConsolidationTests`
  Expected: FAIL — the `[JsonStringEnumMemberName]` attributes are not yet on `SharedBaseAvailability`, so serialization yields the member name (`Available`) not the wire spelling (`available`).

- [ ] **Step 4: Add the `[JsonStringEnumMemberName]` attributes** to `SharedBaseAvailability` in `src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs:14`, matching Step 1's recorded spellings:
  ```csharp
  public enum SharedBaseAvailability
  {
      [JsonStringEnumMemberName("available")]
      Available,
      [JsonStringEnumMemberName("unavailable")]
      Unavailable,
      [JsonStringEnumMemberName("stale")]
      Stale,
      [JsonStringEnumMemberName("conflicted")]
      Conflicted
  }
  ```
  (Ensure `using System.Text.Json.Serialization;` is present at the top of that file; add it if missing.)

- [ ] **Step 5: Run test to verify it passes.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SharedAvailabilityConsolidationTests`
  Expected: PASS.

- [ ] **Step 6: Remove the duplicate `SharedAvailability` enum** from `src/Scrinia.Mcp/Shared/SharedDtoContracts.cs` and repoint the two records (`SharedBaseSummary.Availability`, `RefreshResult.Availability`) to `Scrinia.Core.Configuration.SharedBaseAvailability`. Update the two consumers in `SharedServer.cs:104,106`.

- [ ] **Step 7: Build the whole solution to catch any other `SharedAvailability` reference.**
  Run: `dotnet build Scrinia.sln`
  Expected: BUILD OK. Fix any remaining `SharedAvailability` reference (there should be none after Step 6; the test's `NotBe("partial")` and golden fixtures are unaffected because the wire strings are unchanged).

- [ ] **Step 8: Commit.**
  ```bash
  git add src/Scrinia.Core/Configuration/SharedBaseRuntimeContracts.cs src/Scrinia.Mcp/Shared/SharedDtoContracts.cs src/Scrinia.Mcp/Shared/SharedServer.cs tests/Scrinia.Tests/SharedAvailabilityConsolidationTests.cs
  git commit -m "refactor: collapse duplicate SharedAvailability into SharedBaseAvailability; pin wire spellings"
  ```

---

## Task 5: G2 — Add status predicates to `SharedEnvelope`

**Files:**
- Modify: `src/Scrinia.Mcp/Shared/SharedServer.cs` (add predicates to `SharedEnvelope`)
- Test: `tests/Scrinia.Tests/SharedEnvelopePredicateTests.cs` (create)

**Interfaces:**
- Consumes: `SharedEnvelope.Status` (public string).
- Produces: `SharedEnvelope.IsError` and `SharedEnvelope.IsWarning` predicates mirroring `McpResponse`.

**Steps:**

- [ ] **Step 1: Write the failing test.**
  Create `tests/Scrinia.Tests/SharedEnvelopePredicateTests.cs`:
  ```csharp
  using Scrinia.Mcp.Shared;

  namespace Scrinia.Tests;

  public class SharedEnvelopePredicateTests
  {
      [Fact]
      public void Partial_Envelope_IsWarning_And_NotError()
      {
          var envelope = SharedEnvelope.Partial(Array.Empty<object>(), Array.Empty<SharedWarning>());
          Assert.True(envelope.IsWarning);
          Assert.False(envelope.IsError);
      }

      [Fact]
      public void Error_Envelope_IsError_And_NotWarning()
      {
          var envelope = SharedEnvelope.CreateError("err", "code");
          Assert.True(envelope.IsError);
          Assert.False(envelope.IsWarning);
      }
  }
  ```
  (Confirm the exact `Partial` and `CreateError` factory signatures by reading `SharedServer.cs:141-147` before writing the calls; adjust arguments if the signatures differ.)

- [ ] **Step 2: Run test to verify it fails.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SharedEnvelopePredicateTests`
  Expected: FAIL — `IsError`/`IsWarning` do not exist on `SharedEnvelope`.

- [ ] **Step 3: Add the predicates** inside the `SharedEnvelope` record in `src/Scrinia.Mcp/Shared/SharedServer.cs` (after the `Status` property, around line 127):
  ```csharp
  public bool IsError => Status is "error";
  public bool IsWarning => Status is "partial";
  ```

- [ ] **Step 4: Run test to verify it passes.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SharedEnvelopePredicateTests`
  Expected: PASS.

- [ ] **Step 5: Commit.**
  ```bash
  git add src/Scrinia.Mcp/Shared/SharedServer.cs tests/Scrinia.Tests/SharedEnvelopePredicateTests.cs
  git commit -m "feat: add IsError/IsWarning predicates to SharedEnvelope"
  ```

---

## Task 6: Full sweep — build + run the entire test suite

**Files:** none changed (verification only).

**Steps:**

- [ ] **Step 1: Build the whole solution.**
  Run: `dotnet build Scrinia.sln`
  Expected: BUILD OK (0 errors).

- [ ] **Step 2: Run the full test suite.**
  Run: `dotnet test Scrinia.sln`
  Expected: ALL PASS. If any failure, it should trace to one of the six tasks — fix in the same style (TDD), then re-run. Note: the run may take several minutes on durable storage (per AGENTS.md long-running benchmark caveat does NOT apply to the default suite — the default `dotnet test Scrinia.sln` excludes benchmarks).

- [ ] **Step 3: Commit** any fixes from Step 2 with descriptive messages.

---

## Verification Checklist (final)
- `RequireText` has exactly one definition (grep `RequireText` across src/ — only the shared method remains).
- `DefaultMaxAttempts` is the single source of the retry default; the three out-of-scope `3`s are unchanged.
- `TransactionalFailureCode` drift test is green.
- No remaining reference to `SharedAvailability` (grep `SharedAvailability` across src/ and tests/ — should be zero).
- `SharedEnvelope.IsError`/`IsWarning` exist and are tested.
- Full `dotnet test Scrinia.sln` passes.
