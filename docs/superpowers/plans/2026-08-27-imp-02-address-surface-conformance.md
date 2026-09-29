# IMP-02 Address-Surface Conformance Tests Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically inventory every current address-taking MCP, CLI, and MCP-resource surface and pin its existing canonicalization, refusal, ephemeral policy, and route ownership.

**Architecture:** Add declarative test data whose rows name the surface, input, expected result, ephemeral policy, and owner. Reflection/source checks cover MCP methods and CLI registrations; direct invocations separately cover the CLI’s internal-handler bypass. No production helper or policy changes are permitted.

**Refs:** `docs/superpowers/specs/2026-08-27-imp-02-address-surface-conformance-design.md` (approved inventory scope); `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/ScriniaMcpResources.cs`, and `src/Scrinia/Commands/ScriniaCommands.Memory.cs` (address-taking surfaces and CLI bypass).
ref: `docs/superpowers/specs/2026-08-27-imp-02-address-surface-conformance-design.md`

**Tech Stack:** .NET 10, C#, xUnit, FluentAssertions, reflection, source-text inventory checks, existing `MemoryStoreContext` and test store helpers.

## Global Constraints

- “No canonicalization algorithm change.”
- “No new address format, public API, storage layout, path-length policy, or remote/HTTP surface.”
- “A newly added address-taking surface must fail the inventory test until its intentional policy is recorded.”
- “Tests must distinguish invalid input, missing memory, and valid ephemeral addressing.”
- “No test claims a helper is universal merely because it is shared by current implementations.”
- **Ref:** `docs/superpowers/specs/2026-08-27-imp-02-address-surface-conformance-design.md` (surface-by-surface policy requirement).

---

## Dependencies / Order

- Extend the existing `tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs`; do not create a duplicate address inventory class.
- Task 1 extends the MCP/resource rows and Task 2 adds CLI/direct-handler rows to that same fixture. Complete both before release/version documentation and final integration verification.

### Task 1: Inventory MCP tools and resource

**Files:**
- Modify: `tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs`
- Read: `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/ScriniaMcpResources.cs`

**Interfaces:**
- Consumes: `[McpServerTool]` methods `MemoryRemember`, `MemoryRecall`, `MemoryAppend`, `MemoryForget`, `MemoryCompact`, `MemoryLink`; resource `ScriniaMcpResources.ReadMemory`.
- Produces: explicit inventory rows for every address parameter and resource `name`, including `/temp/x`, `~x`, valid persistent, blank, and invalid cases.

**Ref:** `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/ScriniaMcpResources.cs`; `docs/superpowers/specs/2026-08-27-imp-02-address-surface-conformance-design.md` (MCP/resource rows).

- [ ] **Step 1: Extend `EveryMcpToolTakingAnAddress_IsCoveredByThisFixture` and `KnownAddressTools`** to enumerate `[McpServerTool]` methods and assert the exact current address-bearing method set and parameter names.
- [ ] **Step 2: Add a resource inventory test** that asserts `ReadMemory(string name, CancellationToken)` is present and included in the policy table.
- [ ] **Step 3: Add behavior rows** invoking public MCP methods with a temporary store, asserting canonical persistent resolution, valid ephemeral behavior, refusal/error envelopes where applicable, missing-memory distinction, and no unintended files.
- [ ] **Step 4: Run the focused tests before filling the complete table.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AddressSurfaceCoverageTests"`

Expected: FAIL if the inventory is incomplete; the failure names the unrecorded surface or policy row.

- [ ] **Step 5: Commit the extended MCP/resource coverage fixture.**

```bash
git add tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs
git commit -m "test: inventory MCP address surfaces"
```

### Task 2: Inventory CLI commands and direct bypasses

**Files:**
- Modify: `tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs`
- Read: `src/Scrinia/Commands/ScriniaCommands.cs`, `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, command partials, `src/Scrinia/Program.cs`

**Interfaces:**
- Consumes: CLI address-taking commands (`store`, `show`, `forget`, `append`, `compact`, `link`) and their direct internal calls.
- Produces: rows that distinguish CLI refusal/exit behavior from MCP facade behavior and assert shared persistent canonical addresses.

**Ref:** `src/Scrinia/Commands/ScriniaCommands.Memory.cs`; `docs/superpowers/specs/2026-08-27-imp-02-address-surface-conformance-design.md` (CLI bypass and ownership distinction).

- [ ] **Step 1: Add source/reflection inventory checks** for each CLI command parameter that carries a memory address, plus the registered command-type guard pattern used by `CliSurfaceConformanceTests`.
- [ ] **Step 2: Add direct-handler rows** that call the existing internal handlers, because the CLI bypasses MCP facade methods; assert CLI-unavailable ephemeral inputs fail with the existing exit/error behavior.
- [ ] **Step 3: Add cross-surface persistent-address rows** proving MCP and CLI resolve the same canonical store address without asserting identical envelopes.
- [ ] **Step 4: Run the entire address suite.**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AddressSurfaceCoverageTests|FullyQualifiedName~CliSurfaceConformanceTests|FullyQualifiedName~CliEphemeralRefusalTests"`

Expected: PASS with every current MCP, resource, CLI, and direct-handler address surface named and every intentional policy difference explained in test data.

- [ ] **Step 5: Commit CLI coverage.**

```bash
git add tests/Scrinia.Tests/Naming/AddressSurfaceCoverageTests.cs
git commit -m "test: cover CLI address route conformance"
```

## Self-review

- Spec coverage: the existing address fixture is extended for MCP methods, resource, CLI commands, direct bypass, canonicalization/refusal/ephemeral behavior, invalid-vs-missing distinction, filesystem side-effect checks, and future-surface inventory failure.
- Placeholder scan: no generic exclusions; each exception row names its owner and reason.
- Type consistency: rows call the existing `Task<string>` MCP methods and CLI handler signatures discovered from source/reflection; no new production signature is invented.
