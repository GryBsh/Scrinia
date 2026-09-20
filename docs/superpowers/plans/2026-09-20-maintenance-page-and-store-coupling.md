# Maintenance Page Guards & Store-Coupling Port (Tasks F + D) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Reduce MaintenancePlannerPage's redundant class-equation guards (F) and break the FileMemoryStore ↔ MultiMemoryTransactionCoordinator coupling cycle by routing the address-resolution back-reference through a narrow IAddressResolver port (D) — without changing any maintenance-planning or transactional behavior.

**Architecture:** Two independent, contained tasks. F deletes redundant constructor guards in a single maintenance type. D adds a one-method interface, makes FileMemoryStore implement it, and widens the single ResolveCandidateSubject parameter type — a 3-file change that removes the coordinator→store address back-reference dependency in that method's signature.

**Tech Stack:** C# 12 / .NET, xUnit test framework, System.Text.Json source-gen contexts.

**Spec:** `docs/superpowers/specs/2026-09-06-review-remediation-design.md` (findings F and D). Read it before implementing.

## Global Constraints
- No behavioral change to maintenance planning or transactional recovery.
- Do NOT split FileMemoryStore into smaller types (it is a composition root; its size is out of scope).
- The single IAddressResolver method must be `(string Scope, string Subject) ResolveAddress(MemoryAddress address)` — matching FileMemoryStore's existing public method.
- MaintenancePlannerPage must still enforce its legitimate bounds (scanned ≤ CandidatePageSize, journal/lookahead caps, deferred ∈ {0,1}).
- Every task ends with a green `dotnet test` for its scope and a single focused commit.

---

## Task 1: F — Reduce MaintenancePlannerPage's redundant guards

**Files:**
- Modify: `src/Scrinia.Core/MaintenancePlanner.cs:76-105` (delete 5 redundant class-equation guards; keep the base admission equation + all bounds guards)
- Test: `tests/Scrinia.Tests/MaintenancePlannerPageGuardsTests.cs` (create)

**Interfaces:**
- Consumes: none new.
- Produces: MaintenancePlannerPage with 6 guards total (1 base equation `scanned == journalInspected + nonJournalInspected`, 5 bounds) instead of 11 (6 equations + 5 bounds). The 5 deleted equations are: `scanned == safeJournalInspected + unsafeJournalScanned + safeNonJournalInspected + unsafeNonJournalScanned`, `scanned - unsafeScanned == safeJournalInspected + safeNonJournalInspected`, `unsafeScanned == unsafeJournalScanned + unsafeNonJournalScanned`, `journalInspected == safeJournalInspected + unsafeJournalScanned`, `nonJournalInspected == safeNonJournalInspected + unsafeNonJournalScanned`.

**Steps:**

- [ ] **Step 1: Write the failing test** asserting the base equation still holds and a violated base equation still throws (guards are still enforced for the base case).
  Create `tests/Scrinia.Tests/MaintenancePlannerPageGuardsTests.cs`:
  ```csharp
  using Scrinia.Core;

  namespace Scrinia.Tests;

  public class MaintenancePlannerPageGuardsTests
  {
      [Fact]
      public void Valid_Counters_AreAccepted()
      {
          var page = new MaintenancePlannerPage(
              projections: Array.Empty<MaintenancePlannerProjection>(),
              scanned: 2,
              projected: 0,
              metadataReads: 0,
              returned: 0,
              lookahead: 0,
              hasMore: false,
              unsafeScanned: 0,
              unsafeJournalScanned: 0,
              unsafeNonJournalScanned: 0,
              safeJournalInspected: 2,
              safeNonJournalInspected: 0,
              journalBudget: MaintenanceLimits.JournalBudgetMaximum,
              journalBudgetConsumed: 0,
              journalInspected: 2,
              nonJournalInspected: 0,
              journalCapReached: false,
              journalDeferred: 0);
          Assert.Equal(2, page.Scanned);
      }

      [Fact]
      public void Base_Equation_Violation_Throws()
      {
          Assert.Throws<ArgumentException>(() => new MaintenancePlannerPage(
              projections: Array.Empty<MaintenancePlannerProjection>(),
              scanned: 3,
              projected: 0,
              metadataReads: 0,
              returned: 0,
              lookahead: 0,
              hasMore: false,
              unsafeScanned: 0,
              unsafeJournalScanned: 0,
              unsafeNonJournalScanned: 0,
              safeJournalInspected: 2,
              safeNonJournalInspected: 0,
              journalBudget: MaintenanceLimits.JournalBudgetMaximum,
              journalBudgetConsumed: 0,
              journalInspected: 1,
              nonJournalInspected: 0,
              journalCapReached: false,
              journalDeferred: 0));
      }

      [Fact]
      public void Bounds_Still_Enforced()
      {
          Assert.Throws<ArgumentException>(() => new MaintenancePlannerPage(
              projections: Array.Empty<MaintenancePlannerProjection>(),
              scanned: MaintenanceLimits.CandidatePageSize + 1,
              projected: 0,
              metadataReads: 0,
              returned: 0,
              lookahead: 0,
              hasMore: false,
              unsafeScanned: 0,
              unsafeJournalScanned: 0,
              unsafeNonJournalScanned: 0,
              safeJournalInspected: MaintenanceLimits.CandidatePageSize + 1,
              safeNonJournalInspected: 0,
              journalBudget: MaintenanceLimits.JournalBudgetMaximum,
              journalBudgetConsumed: 0,
              journalInspected: MaintenanceLimits.CandidatePageSize + 1,
              nonJournalInspected: 0,
              journalCapReached: false,
              journalDeferred: 0));
      }
  }
  ```
  (Confirm `MaintenancePlannerProjection` and `MaintenanceLimits` are the correct type/namespace names by reading the top of MaintenancePlanner.cs; adjust the `Array.Empty<...>` generic and `MaintenanceLimits.Xxx` references if the actual names differ.)

- [ ] **Step 2: Run test to verify it fails.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MaintenancePlannerPageGuardsTests`
  Expected: FAIL — the test project cannot see the internal MaintenancePlannerPage. Confirm `InternalsVisibleTo("Scrinia.Tests")` is configured on Scrinia.Core (grep `InternalsVisibleTo` in src/Scrinia.Core). If missing, add `[assembly: InternalsVisibleTo("Scrinia.Tests")]` before continuing.

- [ ] **Step 3: Delete the 5 redundant class-equation guards** in `src/Scrinia.Core/MaintenancePlanner.cs:78,80,82,84,86` (the guards that re-derive quantities the constructor parameters already encode). Keep guard line 76 (the base equation `scanned == journalInspected + nonJournalInspected`) and all bounds guards lines 88-105.

- [ ] **Step 4: Run test to verify it passes.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MaintenancePlannerPageGuardsTests`
  Expected: PASS (all three facts).

- [ ] **Step 5: Commit.**
  ```bash
  git add src/Scrinia.Core/MaintenancePlanner.cs tests/Scrinia.Tests/MaintenancePlannerPageGuardsTests.cs
  git commit -m "refactor: drop redundant class-equation guards from MaintenancePlannerPage"
  ```

---

## Task 2: D — Route the coordinator's address back-reference through IAddressResolver

**Files:**
- Create: `src/Scrinia.Core/IAddressResolver.cs`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (add `IAddressResolver` to the class declaration)
- Modify: `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs:1163` (change `ResolveCandidateSubject` param type from `FileMemoryStore` to `IAddressResolver`)
- Test: `tests/Scrinia.Tests/AddressResolverPortTests.cs` (create)

**Interfaces:**
- Consumes: `FileMemoryStore.ResolveAddress(MemoryAddress)` (public, returns `(string Scope, string Subject)`).
- Produces: `internal interface IAddressResolver { (string Scope, string Subject) ResolveAddress(MemoryAddress address); }`.

**Steps:**

- [ ] **Step 1: Write the failing test** asserting FileMemoryStore implements IAddressResolver and the coordinator no longer references FileMemoryStore in ResolveCandidateSubject's signature.
  Create `tests/Scrinia.Tests/AddressResolverPortTests.cs`:
  ```csharp
  using Scrinia.Core;

  namespace Scrinia.Tests;

  public class AddressResolverPortTests
  {
      [Fact]
      public void FileMemoryStore_Implements_IAddressResolver()
      {
          var store = new FileMemoryStore(...);
          Assert.IsAssignableFrom<IAddressResolver>(store);
      }
  }
  ```
  (The FileMemoryStore constructor requires a workspace root and options — confirm the exact constructor signature by reading FileMemoryStore.cs around line 43. If instantiation is heavy, replace this fact with a compile-time check instead: `Assert.True(typeof(IAddressResolver).IsAssignableFrom(typeof(FileMemoryStore)));` which needs no instance. Prefer the static reflection check if the constructor is cumbersome.)

- [ ] **Step 2: Run test to verify it fails.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AddressResolverPortTests`
  Expected: FAIL — IAddressResolver does not exist yet.

- [ ] **Step 3: Create the interface.**
  Create `src/Scrinia.Core/IAddressResolver.cs`:
  ```csharp
  using Scrinia.Core.Models;

  namespace Scrinia.Core;

  internal interface IAddressResolver
  {
      (string Scope, string Subject) ResolveAddress(MemoryAddress address);
  }
  ```
  (Confirm `MemoryAddress` lives in namespace `Scrinia.Core.Models` — verify by reading the `using` at the top of FileMemoryStore.cs; adjust the using if it differs.)

- [ ] **Step 4: Make FileMemoryStore implement IAddressResolver.**
  Add `IAddressResolver` to FileMemoryStore's class declaration in `src/Scrinia.Core/FileMemoryStore.cs`. Find the class declaration (search for `class FileMemoryStore`) and append `, IAddressResolver` to its interface list.

- [ ] **Step 5: Change the ResolveCandidateSubject signature.**
  In `src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs:1163`, change:
  ```csharp
  private static string ResolveCandidateSubject(FileMemoryStore store, RecoveryCandidate candidate)
  ```
  to:
  ```csharp
  private static string ResolveCandidateSubject(IAddressResolver store, RecoveryCandidate candidate)
  ```
  The caller at line 1093 passes a `FileMemoryStore` (which now implements `IAddressResolver`), so no change is needed there.

- [ ] **Step 6: Build the whole solution to catch any other FileMemoryStore-specific address reference in the coordinator.**
  Run: `dotnet build Scrinia.sln`
  Expected: BUILD OK. If the coordinator has other methods taking `FileMemoryStore` that only need address resolution, leave them (the cycle is specifically the ResolveCandidateSubject back-reference).

- [ ] **Step 7: Run test to verify it passes.**
  Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~AddressResolverPortTests`
  Expected: PASS.

- [ ] **Step 8: Commit.**
  ```bash
  git add src/Scrinia.Core/IAddressResolver.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/MultiMemoryTransactionCoordinator.cs tests/Scrinia.Tests/AddressResolverPortTests.cs
  git commit -m "refactor: route coordinator address back-reference through IAddressResolver port"
  ```

---

## Verification Checklist (final)
- `MaintenancePlannerPage` still enforces its base admission equation and all bounds guards; the 5 derived equations are gone.
- `IAddressResolver` exists with exactly the `ResolveAddress` method; `FileMemoryStore` implements it.
- `ResolveCandidateSubject` takes `IAddressResolver`, not `FileMemoryStore`.
- Full `dotnet test Scrinia.sln` passes.
