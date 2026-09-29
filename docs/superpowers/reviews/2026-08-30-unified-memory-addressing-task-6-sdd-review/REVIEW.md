# SDD Review Package — Task 6

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**FIX_BASE:** `41ca7a4`  
**HEAD:** `c516eeab35ea9a30b46343967f818b2eee9886e6`  
**Scope:** Task 6 MCP tools, recall form detection, resources, categorized input errors, and chat-agent recall audit

## Files Reviewed

- `src/Scrinia.Mcp/MemoryTools.cs`
- `src/Scrinia.Mcp/MemoryTools.Core.cs`
- `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`
- `src/Scrinia.Mcp/ScriniaMcpResources.cs`
- `src/Scrinia.Mcp/McpResponse.cs`
- `tests/Scrinia.Tests/McpAddressSurfaceTests.cs`
- `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`
- Task 6 requirements in the specified plan
- Complete `41ca7a4..c516eeab35ea9a30b46343967f818b2eee9886e6` diff

## Overall Assessment

**REQUEST_CHANGES**

The fix adds boundary classification for MCP tool recall and introduces the planned address-surface
test file. Task 6 remains incomplete because the MCP resource route was not migrated: it still accepts
raw values, uses the obsolete resolver, and does not apply the shared unsupported-input policy.

## Findings

### Major issues

#### T6-001 — MCP resource reads bypass the Task 6 address parser and policy

**Confidence:** 100%

**Location:** `src/Scrinia.Mcp/ScriniaMcpResources.cs:28-55` (HEAD)

`ReadMemory` continues to accept `string name`, canonicalizes only the ephemeral alias, and calls
`store.ResolveArtifactAsync` directly. It does not reject URI-shaped, traversal, malformed, or
encoded inputs through the shared `TryParseLogicalAddress`/`MemoryAddress` boundary. The resource
therefore does not implement the plan's requirement that `scrinia://memory/{name}` use the same
form detector and policy matrix as recall, and it retains the URI-resolution contract that Task 6
is explicitly meant to remove.

The new resource test expects a `McpException` containing `UNSUPPORTED_INPUT_FORM`, but this
implementation has no such guard before store access. Migrate the resource method to the shared
parser, pass the typed address to the typed read boundary, and preserve the categorized error before
requiring a store or mutating anything. Cover both registered resource transports as required by
the plan.

### Minor issues

#### T6-002 — Resource registration coverage is only presence-based

**Confidence:** 95%

**Location:** `tests/Scrinia.Tests/McpAddressSurfaceTests.cs:88-98` (HEAD)

`ActiveMcpSurface_IsExplicitlyEnumerated` verifies that `ReadMemory` exists, but does not inspect
both resource transport registrations or exercise their address/form policy. This leaves the
resource gap in T6-001 undetected by the claimed surface inventory. Extend the test to enumerate
the registrations and run canonical persistent/ephemeral, URI, traversal, malformed, and no-store
cases through each route.

## Positive Observations

- `TryClassifyArtifactInput` now rejects recognized non-NMP headers, truncated NMP/2, and payload-like
  values before logical address parsing, while valid inline NMP/2 remains store-independent.
- `TryParseLogicalAddress` centralizes URI rejection, ephemeral alias canonicalization, and parser
  error translation for the MCP tool facade.
- Internal address-bearing operations are checked by reflection to require `MemoryAddress`, and the
  implicit string conversion is explicitly prohibited by the new surface test.
- The no-side-effect test confirms an invalid MCP remember address is rejected before workspace
  mutation, and the repository audit found no `AgentLoop.cs` or `recall_memory` surface to migrate.

## Verification Evidence

- Reviewed the complete `41ca7a4..c516eeab35ea9a30b46343967f818b2eee9886e6` diff and Task 6 plan text.
- `git diff --check 41ca7a4 c516eeab35ea9a30b46343967f818b2eee9886e6`: passed.
- Focused test command completed compilation, but the checked-out workspace test assembly reported no
  matching `McpAddressSurfaceTests` tests; the reviewed HEAD file was inspected directly instead.
- Source and test implementations were not modified by package generation.

## Philosophy Check

- Early exit: **PARTIAL** — tool routes classify invalid forms early, but the resource route does not.
- Parse don't validate: **PARTIAL** — MCP tools parse once at the facade; resources still pass raw strings.
- Atomic predictability: **PARTIAL** — tool behavior is consistent, while resource behavior depends on
  a separate resolver and can consult the store for forbidden forms.
- Fail fast: **FAIL** for T6-001 — resource input is not rejected at the shared boundary.
- Intentional naming: **PASS** — the new classifier/parser names clearly express their roles.
