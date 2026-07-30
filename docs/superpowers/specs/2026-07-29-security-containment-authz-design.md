# Security containment & authz hardening — design

**Date:** 2026-07-29
**Status:** Approved (design); implementation plan pending
**Stream:** 1 of 7 from the 2026-07-29 whole-codebase security review remediation

## Context

An independent whole-codebase review returned `REQUEST_CHANGES`. Every code finding
was independently re-verified against the actual source (five parallel read-only
verifiers) before any design work. The review's headline "Build: FAIL / Tests: FAIL"
was **not** a real failure — it was the known pre-existing `NuGetAudit` restore gate
(`MessagePack` etc. in `Scrinia.AppHost`); it is bypassed with `-p:NuGetAudit=false`.
The code findings, however, held up: nearly all confirmed.

The confirmed work was decomposed into seven streams, each with its own
spec → plan → implement cycle. **This spec covers Stream 1 only: security containment
and server authorization.** The other streams (data integrity, supply chain / CI-CD,
retrieval correctness, secrets & transport, web frontend, docs) are explicit non-goals
here.

## Threat model

Untrusted **multi-tenant HTTP server**: multiple orgs/users (SSO + API keys) who do not
trust each other share a server. Consequences for this stream:

- Tenant isolation (key management, chat, read-only access levels, rate limiting) is a
  **hard boundary** that must hold, not defense-in-depth.
- Path containment must resist **hostile content authored by another tenant** —
  imported bundles, `merge.config`, skill/agent names.

## Group A — Path containment & traversal

### A1. Harden `PathContainment.IsWithin` (foundational)

**File:** `src/Scrinia.Core/PathContainment.cs`

**Current:** lexical `Path.GetFullPath` normalization on both operands, then an
unconditional `OrdinalIgnoreCase` `StartsWith`. No symlink/junction resolution.

**Problems:**
- On case-sensitive filesystems (Linux) a case-differing path outside the base is
  wrongly accepted.
- A symlink/junction under the base pointing outward passes the purely lexical check.

**Change:**
- Resolve **real** paths on **both** base and candidate before comparison. Because the
  candidate frequently does not exist yet (it is a write destination), canonicalize the
  longest existing ancestor via `File.ResolveLinkTarget` / `DirectoryInfo.ResolveLinkTarget(returnFinalTarget: true)`
  and append the not-yet-existing remainder.
- Compare with **OS-appropriate** case sensitivity: `StringComparison.Ordinal` on Linux,
  `OrdinalIgnoreCase` on Windows and macOS (selected via `OperatingSystem.IsLinux()`).

**Callers this affects (must not regress):** zip-slip guard on bundle extraction
(`BundleFormatService.cs`), workspace containment (`MemoryTools.Core.cs`,
`FileMemoryStore.cs`), bundle import (`MemoryTools.Bundling.cs`).

**Risk:** if a store/base dir is itself reached through a symlink (macOS
`/var`→`/private/var`, Docker overlay/volume mounts), resolving only one operand would
reject legitimate paths. Mitigation: resolve **both** operands consistently; add tests
for a symlinked-base case.

**Residual (accepted, documented):** resolve-then-check leaves a TOCTOU window (a symlink
swapped between the containment check and the subsequent file open). Portable
TOCTOU-safe open (`O_NOFOLLOW`-equivalent) is not available in .NET; we document the
residual rather than chase it.

### A2. Validate `/skill` and `/agent` names

**Files:** `src/Scrinia.Mcp/MemoryTools.Skills.cs` (skill read/write),
`src/Scrinia.Mcp/MemoryTools.cs` (name parse), `src/Scrinia.Mcp/MemoryTools.Core.cs`
(agent read/write).

**Current:** the name parsed from `/skill/<name>` / `/agent/<name>` flows straight into
`Path.Combine($"{name}.md")` with no validation; `../../evil` escapes the skills/agent dir
on both read (`File.ReadAllTextAsync`) and write (`File.WriteAllTextAsync`).

**Change:** a shared validator that (1) rejects names containing `/`, `\`, `..`, or that
are rooted, via a strict slug check, and (2) asserts
`PathContainment.IsWithin(dir, resolvedPath)` before the combine is used. This reuses the
containment primitive already applied to bundle import — the guard simply was not applied
on these two paths.

### A3. Contain `merge.config` `ConflictDir`

**File:** `src/Scrinia.Merge/Nmp2ConflictHandler.cs` (`ConflictDir` derivation),
config source `src/Scrinia.Merge/MergeConfig.cs`.

**Current:** `Path.Combine(dir, config.ConflictDir)` with no validation; `ConflictDir` is
loaded from `.scrinia/merge.config` (attacker-influenceable, and plantable via A4). An
absolute value discards `dir`; a `../../..` value escapes the artifact dir.

**Change:** reject rooted or `..`-bearing `ConflictDir`; require a single relative segment,
or containment-check `Path.GetFullPath(conflictDir)` under `dir`.

### A4. Allowlist bundle import categories

**File:** `src/Scrinia.Core/Bundles/BundleFormatService.cs` (import path).

**Current:** import containment holds (cannot escape `.scrinia`), but the category key
comes straight from the manifest with **no allowlist** — the `FileEntityCategories`
(`workflows`, `skills`, `agent`) allowlist is applied only on **export**. A crafted
manifest with an empty/other category and `fileName: "merge.config"` can write/overwrite
`.scrinia/merge.config` (which then feeds A3).

**Change:** on import, restrict category keys to the same `FileEntityCategories` set;
reject any other category key.

### A5. Contain `bundle --output` (low severity)

**File:** `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`.

**Current:** `bundleName = output` → `Path.Combine(exportsDir, bundleName)` with no
containment check; an absolute or `../..` `--output` escapes the exports dir.

**Change:** containment-check the resolved bundle path under `exportsDir` (or reduce
`output` to `Path.GetFileName`). Lowest priority — a local CLI arg the invoking user
already controls — but a cheap consistency fix.

## Group B — Server authorization

### B1. Gate the `facts` MCP tool

**File:** `src/Scrinia.Server/Auth/McpPermissionFilter.cs`
(`RequiredPermission` switch).

**Current:** `facts` (a real `[McpServerTool]`, an aggregate query over entity data) is
absent from the `tools/call` map, so it hits the `_ => null` arm — no gate. It is the only
*query* tool not mapped; `guide`/`restore` are intentionally ungated.

**Change:** add `"facts" => "search"` — facts is an aggregate query, so it rides the
`search` gate alongside `search`/`list`.

### B2. Key-management isolation

**File:** `src/Scrinia.Server/Endpoints/KeyEndpoints.cs`
(`ListKeys`, `GetKey`, `RevokeKey`).

**Current:** the `ManageKeys` policy only requires `permission=manage_keys`; the three
read/revoke handlers inject only `ApiKeyStore` and apply no store-scope check. Any
manage-keys holder can enumerate/read/revoke **every** key globally. (`CreateKey` already
enforces store scope; these do not.)

**Admin mechanism (existing):** `RequestContext.CanAccessStore` treats a key whose
`Stores` contains `"*"` as global; the bootstrap admin key holds broad store access +
`manage_keys`.

**Change:** inject `RequestContext` and define

> **manageable(K)** = caller has `"*"` store access (global admin)
> **OR** every store in `K.Stores` is accessible to the caller
> (`K.Stores.All(ctx.CanAccessStore)` — *all*, not *any*, so a key granting an
> out-of-scope store stays hidden).

- `ListKeys` returns only manageable keys.
- `GetKey` / `RevokeKey` return **404** (not 403) when the key exists but is not
  manageable — no cross-tenant existence leak.

### B3. Chat requires read + search

**File:** `src/Scrinia.Server/Program.cs` (the `"Chat"` authorization policy);
`src/Scrinia.Server/Endpoints/ChatEndpoints.cs` gates on that policy.

**Current:** the `Chat` policy requires only `permission=chat`, yet `AgentLoop` runs
`search_memory` → `store.SearchAll` and `recall_memory` → `MemoryOrchestrator.ShowAsync`
with no permission recheck. A key holding only `chat` can drive the agent to search and
read full memory content.

**Change:** extend the `Chat` policy to `RequireClaim("permission","chat")` **and**
`"read"` **and** `"search"` (each `RequireClaim` ANDs). A chat-only key is rejected at the
endpoint; no `AgentLoop` change needed. Decision rationale: chat answers inherently
surface memory content, so "chat" without read/search is not a coherent grant.

**Behavior change:** any existing chat-only keys stop working — changelog note required.

### B4. Enforce read-only store access on writes

**Files:** `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs` (write handlers);
accessor `RequestContext.GetStoreAccessLevel` already exists but has **zero callers**.

**Current:** write handlers (`StoreMemory`, `AppendMemory`, `ForgetMemory`, `CopyMemory`
destination, `ImportBundle`) check only `HasPermission`, never the access level. A store
scoped read-only via the SSO `store_access` claim can still be written.

**Change:** in each write handler, reject with **403** when
`ctx.GetStoreAccessLevel(store) != "read-write"`. Scope note: this bites the
SSO/auth-plugin path only; API keys default to `read-write` for backward compat, so this
is closing a latent SSO gap rather than an API-key break.

### B5. Rate-limit after authentication

**File:** `src/Scrinia.Server/Program.cs` (middleware order + `"api"` partition).

**Current:** `app.UseRateLimiter()` is registered **before**
`UseAuthentication()`/`UseAuthorization()`; the `"api"` partition key
(`context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? RemoteIpAddress ?? "anonymous"`)
therefore always falls back to IP because `context.User` is not yet populated —
per-user limiting never engages.

**Change:** move `app.UseRateLimiter()` to **after** `UseAuthentication()` /
`UseAuthorization()` (and before endpoint execution), so the partition resolves to the
authenticated user id.

## Testing strategy

Each fix ships with a regression test proving the hole is closed:

- **A1:** containment rejects a case-differing path on Linux; accepts a legitimately
  symlinked base (both operands resolved); handles a non-existent candidate; rejects a
  symlink-under-base pointing outward.
- **A2:** `/skill/../../evil` and `/agent/..\evil` rejected on both read and write.
- **A3:** rooted / `..` `ConflictDir` rejected.
- **A4:** import manifest with a non-allowlisted category (and a `merge.config` filename)
  rejected.
- **A5:** absolute / `..` `--output` rejected or reduced to a filename.
- **B1:** `facts` denied for a key without `search`; allowed with it.
- **B2:** tenant A's manage-keys key cannot list/get/revoke tenant B's key (404 on
  get/revoke; absent from list); global `"*"` admin still sees all.
- **B3:** chat-only key gets 403; a key with chat+read+search succeeds.
- **B4:** a read-only-scoped store write returns 403; read-write succeeds.
- **B5:** two authenticated users get independent rate-limit partitions (not merged by IP).

Full suite must pass via
`dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false` (and the
server/merge test projects). Note: `dotnet build Scrinia.sln` fails at restore on the
pre-existing `Scrinia.AppHost` NuGet-audit; build in-scope projects individually or use
`-p:NuGetAudit=false`.

## Implementation order

1. **A1** first — the shared primitive that A2/A3/A5 lean on.
2. Remaining Group A (A2, A3, A4, A5) — independent of each other once A1 lands.
3. Group B (B1–B5) — independent of Group A; B2 is the largest.

## Non-goals (deferred to other streams)

- **Stream 2 — Data integrity:** `MetaJsonMerger` metadata destruction + `KeywordJaccard`
  null-vs-populated chain, orphan sidecars on `SaveIndexUnsafe`, Nmp2 decode CRC/length
  verification.
- **Stream 3 — Supply chain & CI/CD:** dependency upgrades (SQLitePCLRaw → 3.50.3+,
  Microsoft.OpenApi → 2.7.5, MessagePack → 2.5.301), full-solution CI, release test gate,
  Dockerfile checksum + `Directory.Build.props`.
- **Stream 4 — Retrieval correctness:** filter-before-top-K, MMR chunk TF, embeddings
  provider/model signature validation, plugin write-path chunk shrink.
- **Stream 5 — Secrets & transport:** API keys via child-process argv → env, non-HTTPS
  bearer refusal, silent remote write failures.
- **Stream 6 — Web frontend:** login persists invalid keys, health fallback masking,
  settings `NaN`→null, SSE final-buffer flush, `response.body` guard, empty-search clear,
  a11y labels.
- **Stream 7 — Docs/minor:** "two MCP tools" doc drift, `McpResponse` truncation
  suggestion.

## Risks

- **A1** is the one change that could regress a legitimate deployment (symlinked base);
  mitigated by resolving both operands + explicit symlinked-base tests.
- **B3** is an intentional behavior change: existing chat-only keys stop working.
  Acceptable under the multi-tenant model; requires a changelog note.
