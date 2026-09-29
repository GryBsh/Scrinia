# SDD Review Package — Task 5

**Plan:** `docs/superpowers/plans/2026-08-29-unified-memory-addressing.md`  
**BASE:** `63e87e5`  
**HEAD:** `55bf547`  
**Scope:** Task 5, plugin callbacks, hooks, event payloads, embedding propagation, and test doubles

## Files Reviewed

- `src/Scrinia.Plugin.Abstractions/HookContexts.cs`
- `src/Scrinia.Core/CompositeEventSink.cs`
- `src/Scrinia.Core/CoreSinkStore.cs`
- `src/Scrinia.Core/EntityTypingEventHandler.cs`
- `src/Scrinia.Core/MaintenanceEventSink.cs`
- `src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs`
- `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs`
- `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`
- `src/Scrinia/Services/McpPluginHost.cs`
- `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`
- `tests/Scrinia.TestDoubles/FakeMemoryStore.cs` and changed plugin/event doubles
- `tests/Scrinia.Tests/PluginAddressPropagationTests.cs`
- `tests/Scrinia.Plugin.Embeddings.Tests/PluginAddressPropagationTests.cs`
- Task 5 requirements in the specified plan and the complete `63e87e5..55bf547` diff

## Overall Assessment

**REQUEST_CHANGES**

The change successfully carries `MemoryAddress` through the public hook contexts, event sink
callbacks, embedding plugin callbacks, and MCP plugin host. The Task 5 migration is not fully
closed, however: maintenance still compares an extracted raw alias to the canonical callback
address, allowing an ephemeral self-reference to be treated as an external reference, and the
new propagation tests do not exercise the callback families explicitly required by the plan.

## Findings

### Major issues

#### T5-001 — Maintenance self-reference detection is not alias-equivalent

**Confidence:** 95%

**Location:** `src/Scrinia.Core/MaintenanceEventSink.cs:89-104` (HEAD)

`AutoLinkAndDetectOrphans` renders the typed callback address with `address.ToString()` but compares
each extracted reference directly against that canonical string. Thus a callback for `/temp/probe`
has canonical address `~probe`, while content containing `[[/temp/probe]]` yields the raw
`/temp/probe` reference; the self-reference guard does not match. The code then resolves and updates
the same memory as though it were an outbound reference to another entry. This violates Task 5's
requirement that canonical `/temp/probe` and `~probe` equivalence survive maintenance callbacks and
that callback consumers do not reinterpret addresses as unrelated strings.

Parse each extracted logical reference once, compare `MemoryAddress` values, and use the parsed
canonical value for the persisted ref attribute/display boundary. Add both alias directions to the
maintenance propagation test.

### Minor issues

#### T5-002 — Propagation coverage omits required hook, embedding, maintenance, and reindex paths

**Confidence:** 100%

**Location:** `tests/Scrinia.Tests/PluginAddressPropagationTests.cs:10-59` and
`tests/Scrinia.Plugin.Embeddings.Tests/PluginAddressPropagationTests.cs:8-25` (HEAD)

The core test invokes only `CompositeEventSink` with a capturing sink and directly checks
`FakeMemoryStore.ResolveAddress`. The embeddings test only reflects over context properties. Neither
test exercises stored/appended/forgotten callbacks on `EntityTypingEventHandler` or
`MaintenanceEventSink`, the embedding plugin's before/after hook methods, `McpPluginHost`,
`CoreEmbeddingEventHandler`, or `EmbeddingReindexer`. The plan's Step 1 explicitly requires stored,
appended, forgotten, before/after hook, embedding, and maintenance callbacks, including invalid-write
non-invocation coverage. A regression in any of those adapters could pass the new tests.

Add focused seam tests with address-capturing fakes for every listed callback family, including both
`/temp/probe` and `~probe`, and assert that invalid writes never invoke a callback.

## Positive Observations

- `HookContexts` now exposes `MemoryAddress Address` and removes the old `Name`/
  `QualifiedName` context properties.
- `CompositeEventSink`, `EntityTypingEventHandler`, `MaintenanceEventSink`, `EmbeddingsPlugin`,
  and `McpPluginHost` use typed callback signatures, preserving the canonical address object across
  the primary event and plugin paths.
- `MemoryArtifactSource` and `EmbeddingReindexer` consume typed entry addresses and the embedding
  plugin passes the address directly into the typed indexer/removal paths instead of reparsing it.

## Verification Evidence

- Reviewed the complete `63e87e5..55bf547` diff and the Task 5 requirements in the plan.
- `git diff --check 63e87e5 55bf547`: passed.
- The diff is limited to Task 5 implementation/test-double/test additions; no source or test files
  were modified by this review package.
- Repository inspection confirms the event sink callback surface at HEAD is typed, while the
  maintenance alias comparison and the unexercised callback families remain as described above.

## Philosophy Check

- Early exit: **PASS** for plugin capability guards and the embedding callbacks' no-op paths.
- Parse don't validate: **PARTIAL**; typed callback payloads are preserved, but maintenance compares
  a raw extracted address string with a canonical typed address rather than parsing the reference
  once at the boundary.
- Atomic predictability: **PARTIAL**; most propagation is direct, but alias-form self-references can
  trigger an unintended maintenance mutation.
- Fail fast: **PASS** for the typed callback contract; malformed callback payloads cannot be supplied
  through the public signatures.
- Intentional naming: **PASS** for `Address` in contexts and callback methods; the remaining
  `qualifiedName` terminology is confined to the content-reference maintenance boundary.
