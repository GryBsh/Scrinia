# Unified Memory Addressing and Write Validation Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Replace raw memory names and filesystem URIs with one canonical `MemoryAddress` contract, one core write-validation boundary, and portable store-generated artifact keys across Core, adapters, plugins, bundles, serialization, and tests.

**Architecture:** Parse raw addresses exactly once at each adapter boundary into an immutable `MemoryAddress`; apply adapter policy to the parsed kind; convert raw content into validated `MemoryWrite`; then pass typed values through stores, events, plugins, and search/list requests. `FileMemoryStore` alone maps validated logical addresses and opaque `ArtifactKey` values to local paths, while bundle import regenerates local physical locations and keys.

**Tech Stack:** C#/.NET 10, xUnit, FluentAssertions, `System.Text.Json` source generation, ModelContextProtocol, Spectre.Console, filesystem-backed `FileMemoryStore`, NMP/2 encoding.

**Spec:** `docs/superpowers/specs/2026-08-29-unified-memory-addressing-design.md`

## Global Constraints

- This is one public breaking release track; do not retain aliases for removed URI APIs or add a compatibility phase.
- `MemoryAddress` is the only supported address-construction path; it exposes canonical logical data and never exposes a physical path or URI.
- `/temp/foo` and `~foo` are equal ephemeral addresses whose canonical public form is `~foo`; all other valid logical paths, including `/skill`, `/sessions`, and `/findings`, are persistent.
- Direct address parsing reports `MalformedAddress`; recall form detection reports `UnsupportedInputForm` for URI-shaped input and invalid inline NMP/2.
- Core validates content presence, element UTF-8 bytes, total bytes, chunk count/encoded size, address length, attributes, and keywords before any store or plugin mutation.
- CLI memory commands are persistent-only; MCP tools and resources support only their approved ephemeral operations; policy checks happen after canonicalization and before storage.
- `ArtifactKey` is opaque, store-relative, slash-separated, non-rooted, non-URI data; only `FileMemoryStore` maps it to a path beneath its configured root.
- Bundles contain logical addresses, content, metadata, and portable artifact keys; import regenerates local keys and locations and never trusts source physical paths.
- No durable transaction redesign, plugin state-machine redesign, retrieval indexing/ranking redesign, embedding behavior redesign, new server/HTTP surface, remote/URI CLI options, unrelated documentation, or historical planning/review rewrites.
- Every changed public request, response, error, enum, and value type must be registered in the applicable source-generated JSON context; reflection-based serialization is not an escape hatch.
- Every invalid-input test must assert no store/plugin mutation occurred, and every adapter must preserve the core error category when translating it to its native protocol.

## File Map

### Core model and validation
- Create: `src/Scrinia.Core/Models/MemoryAddress.cs` — `MemoryAddressKind`, `MemoryAddress`, parser errors, canonical rendering/equality.
- Create: `src/Scrinia.Core/Models/MemoryError.cs` — `MemoryErrorCategory` and `MemoryError`.
- Create: `src/Scrinia.Core/Models/MemoryValidationOptions.cs` — one shared limit policy and deterministic `MemoryWrite` validation.
- Modify: `src/Scrinia.Core/Models/MemoryWrite.cs` — replace `(Name, Scope, string[])` raw payload with typed address/content and shared validation.
- Modify: `src/Scrinia.Core/Models/ArtifactEntry.cs` — replace `Uri` with `Address` and opaque `ArtifactKey`.
- Modify: `src/Scrinia.Core/Models/ScopedArtifact.cs`, `src/Scrinia.Core/Search/SearchQuery.cs`, and new typed request models — remove raw address-bearing filters.
- Modify: `src/Scrinia.Core/PathParser.cs`, `src/Scrinia.Core/MemoryNaming.cs`, `src/Scrinia.Core/PathRouter.cs`, `src/Scrinia.Core/PathContainment.cs` — reuse address parsing and remove duplicate URI/name resolution.

### Contracts, stores, and persistence
- Modify: `src/Scrinia.Core/IMemoryStore.cs`, `src/Scrinia.Core/IPluginStore.cs`, `src/Scrinia.Core/IMemoryEventSink.cs` — typed address/write/search/list/event contracts and removal of `ArtifactUri`/URI resolution.
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` — validate typed inputs, generate/validate `ArtifactKey`, map only at the physical boundary, and remove `file://` resolution.
- Modify: `src/Scrinia.Core/CoreSinkStore.cs`, `src/Scrinia.Core/EntityTypingEventHandler.cs`, `src/Scrinia.Core/MaintenanceEventSink.cs`, `src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs`, `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs` — typed callback propagation.
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs`, `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs`, `src/Scrinia.Core/Formats/DurableFormats.cs` — portable key serialization and local key regeneration.

### Adapters and plugin surfaces
- Modify: `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, `src/Scrinia.Mcp/MemoryTools.Bundling.cs`, `src/Scrinia.Mcp/MemoryTools.Skills.cs`, `src/Scrinia.Mcp/ScriniaMcpResources.cs`, `src/Scrinia.Mcp/McpResponse.cs` — parse/form detection, policy, categorized errors, and typed calls.
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`, `src/Scrinia/Commands/CliJsonContext.cs`, `src/Scrinia/Commands/WorkspaceSetup.cs` — persistent-only CLI policy and shared operation path.
- Modify: `src/Scrinia.Plugin.Abstractions/HookContexts.cs`, `src/Scrinia.Plugin.Abstractions/IMemoryOperationHook.cs`, `src/Scrinia.Plugin.Abstractions/IScriniaPlugin.cs`, `src/Scrinia.Plugin.Abstractions/ScriniaPluginBase.cs`, `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`, `src/Scrinia/Services/McpPluginHost.cs`, `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`, and `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs` — typed plugin and event contracts.
- Audit: repository search confirms there is currently no `src/Scrinia.Server` or `AgentLoop.cs` surface; verify that remains true and do not create one. If an address-bearing route/agent surface exists in the implementation branch, include every such read/write entry point without adding a server surface in this track.

### Tests, docs, and release communication
- Modify/add tests under `tests/Scrinia.Tests`, `tests/Scrinia.TestDoubles`, `tests/Scrinia.Plugin.Embeddings.Tests`, `tests/Scrinia.Benchmarks`, and `tests/Scrinia.Merge.Tests` to compile only against typed contracts and exhaustively cover each adapter, store, plugin, bundle, and JSON surface.
- Modify: `docs/getting-started.md`, `docs/cli-reference.md`, `docs/plugin-authoring.md`, `docs/security.md`, and `CHANGELOG.md` — living guidance and breaking-release notes only; do not rewrite `docs/superpowers/` historical artifacts.

---

### Task 1: Establish the public address and error model

**Files:**
- Create: `src/Scrinia.Core/Models/MemoryAddress.cs`
- Create: `src/Scrinia.Core/Models/MemoryError.cs`
- Test: `tests/Scrinia.Tests/MemoryAddressTests.cs`

**Interfaces:**
- Consumes: existing `MemoryNaming.IsEphemeralName`, `MemoryNaming.StripEphemeralPrefix`, `PathParser` validation conventions, and UTF-8 APIs.
- Produces: `public enum MemoryAddressKind { Persistent, Ephemeral }`; `public readonly record struct MemoryAddress` with `string Value`, `MemoryAddressKind Kind`, `static MemoryAddress Parse(string raw)`, and `static bool TryParse(string? raw, out MemoryAddress address, out MemoryAddressError error)`; `MemoryAddressError` carrying the `MalformedAddress` category; `MemoryErrorCategory` and `MemoryError` from the spec.

- [ ] **Step 1: Write the failing tests.** Use a table-driven test with these exact cases: `"/notes/design"` → persistent `/notes/design`; `"/temp/foo"` and `"~foo"` → equal ephemeral value `foo` and canonical `~foo`; `"/temp"`, `"/temp/foo/bar"`, `"~"`, empty, whitespace, `/`, `.`/`..` segments, `a/../b`, `a\\b`, `C:\\x`, `\\\\server\\share`, `file:///x`, `https://x`, `scheme:value`, `//authority`, `/x?q`, `/x#f`, percent-encoded URI/traversal forms, NUL/control characters, empty segments, invalid logical characters, and a value over the shared UTF-8 limit must fail with `MalformedAddress`. Assert `Parse` throws the same category, `TryParse` returns `false` with no usable value, equal values have equal hashes, and JSON/string output uses `~foo` for ephemeral and `/notes/design` for persistent.
- [ ] **Step 2: Run the focused test to verify it fails.** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MemoryAddressTests`; expected: compile failure because `MemoryAddress`, `MemoryAddressError`, and `MemoryErrorCategory` do not exist.
- [ ] **Step 3: Implement the minimal parser.** Make construction private/controlled by `Parse`/`TryParse`; trim only the explicitly allowed surrounding whitespace, reject URI-shaped and physical-path forms before logical normalization, count UTF-8 bytes, require legal non-empty segments, classify `/temp/<name>` and `~<name>` before policy, and never normalize a traversal or invalid separator into a different accepted address. Implement canonical `ToString()` and immutable record-struct equality over kind/value.
- [ ] **Step 4: Run the focused test to verify it passes.** Run the same command; expected: all address tests pass.
- [ ] **Step 5: Commit.** Run `git add src/Scrinia.Core/Models/MemoryAddress.cs src/Scrinia.Core/Models/MemoryError.cs tests/Scrinia.Tests/MemoryAddressTests.cs && git commit -m "feat: add canonical memory addresses"`.

### Task 2: Move write validation into a shared core policy

**Files:**
- Create: `src/Scrinia.Core/Models/MemoryValidationOptions.cs`
- Modify: `src/Scrinia.Core/Models/MemoryWrite.cs`
- Test: `tests/Scrinia.Tests/MemoryWriteValidationTests.cs`

**Interfaces:**
- Consumes: `MemoryAddress`, `MemoryError`, existing `MemoryWrite` derivation fields and `InvalidMemoryWriteException` behavior.
- Produces: `public sealed record MemoryWrite(MemoryAddress Address, IReadOnlyList<ReadOnlyMemory<byte>> Content, IReadOnlyDictionary<string,IReadOnlyList<string>> Attributes, IReadOnlyList<string> Keywords)` (with the existing description/provenance inputs retained only where required by derivation); `MemoryValidationOptions` containing the one address/content/total/chunk/attribute/keyword policy; a core validation method that returns or throws categorized `MemoryError` before side effects.

- [ ] **Step 1: Write failing tests.** Construct writes for null/empty content, a null element, one element over the UTF-8 per-element limit, total content over the total limit, too many chunks, an encoded chunk over its limit, an overlong address, invalid attribute key/value, and invalid keyword. Assert `InvalidWriteContent`, field, limit, and actual values; assert a valid multi-chunk remember and exactly-one-chunk append are accepted and that validation does not mutate a spy store.
- [ ] **Step 2: Run tests to verify failure.** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~MemoryWriteValidationTests`; expected: compile failures against the old constructor/shape and missing policy.
- [ ] **Step 3: Implement the shared policy and typed payload.** Preserve raw caller metadata needed for `MemoryDerivation`, but convert content at the adapter/core boundary to UTF-8 `ReadOnlyMemory<byte>`; apply guard clauses in deterministic order; enforce append cardinality in the shared validator; remove the old name/scope validation as a second address rule.
- [ ] **Step 4: Run tests to verify pass.** Run the focused command; expected: all validation and no-side-effect assertions pass.
- [ ] **Step 5: Commit.** `git add src/Scrinia.Core/Models/MemoryValidationOptions.cs src/Scrinia.Core/Models/MemoryWrite.cs tests/Scrinia.Tests/MemoryWriteValidationTests.cs && git commit -m "feat: centralize memory write validation"`.

### Task 3: Migrate `ArtifactEntry` and all core contracts to typed addressing

**Files:**
- Modify: `src/Scrinia.Core/Models/ArtifactEntry.cs`, `src/Scrinia.Core/Models/ScopedArtifact.cs`, `src/Scrinia.Core/IMemoryStore.cs`, `src/Scrinia.Core/IPluginStore.cs`, `src/Scrinia.Core/IMemoryEventSink.cs`
- Create/modify: typed `MemorySearchRequest` and `MemoryListRequest` in `src/Scrinia.Core/Models/`
- Modify: `src/Scrinia.Core/Search/SearchQuery.cs`
- Test: `tests/Scrinia.Tests/ContractTypingTests.cs`, `tests/Scrinia.Tests/ArtifactEntrySerializationTests.cs`

**Interfaces:**
- Consumes: Tasks 1–2 exact `MemoryAddress`, `MemoryWrite`, and `MemoryError` signatures.
- Produces: `ArtifactEntry.Address` and `ArtifactEntry.ArtifactKey`; `IMemoryStore.ReadAsync(MemoryAddress, CancellationToken)`, `WriteAsync(MemoryAddress, MemoryWrite, CancellationToken)`, `DeleteAsync(MemoryAddress, CancellationToken)`, typed search/list request members, and existing derivation members updated to typed address values; `IPluginStore` callbacks/read methods accepting `MemoryAddress`; event sink methods receiving `MemoryAddress`; no `ArtifactEntry.Uri`, `ArtifactUri`, or URI resolver contract.

- [ ] **Step 1: Write failing contract tests.** Use reflection to assert `ArtifactEntry` has `Address` of `MemoryAddress` and `ArtifactKey` of `string`, has no public `Uri`, and no public `ArtifactUri` member exists on store contracts. Assert every address-bearing search/list/request property is `MemoryAddress` or a typed collection/filter, and compile a fake implementation whose callbacks can only receive `MemoryAddress`.
- [ ] **Step 2: Run tests and compile.** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ContractTypingTests`; expected: failures identify the old raw-string/URI members.
- [ ] **Step 3: Migrate contracts and models.** Replace rather than alias old members, define the request shapes once, update XML documentation to describe logical addresses, and preserve existing non-address search/ranking semantics. Update `ScopedArtifact` so its entry carries the typed address without introducing a second string identity.
- [ ] **Step 4: Update compile consumers minimally.** Change production implementations and all test doubles enough to satisfy the new signatures; do not yet remove every adapter policy or persistence behavior—that is covered by later tasks.
- [ ] **Step 5: Run solution compilation.** Run `dotnet build Scrinia.sln`; expected: only intentionally unfinished adapter/store call sites remain, and no contract test failure remains.
- [ ] **Step 6: Commit.** `git add src/Scrinia.Core/Models src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/IPluginStore.cs src/Scrinia.Core/IMemoryEventSink.cs src/Scrinia.Core/Search/SearchQuery.cs tests/Scrinia.Tests/ContractTypingTests.cs tests/Scrinia.Tests/ArtifactEntrySerializationTests.cs tests/Scrinia.TestDoubles && git commit -m "breaking: type memory store contracts"`.

### Task 4: Remove obsolete URI resolution and make `FileMemoryStore` the physical boundary

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`, `src/Scrinia.Core/PathParser.cs`, `src/Scrinia.Core/MemoryNaming.cs`, `src/Scrinia.Core/PathRouter.cs`, `src/Scrinia.Core/PathContainment.cs`
- Delete obsolete `ArtifactUri`/resolver helpers wherever the repository search locates them
- Modify: `tests/Scrinia.Tests/FileMemoryStoreTests.cs`, `tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs`, `tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs`
- Test: `tests/Scrinia.Tests/ArtifactKeyBoundaryTests.cs`

**Interfaces:**
- Consumes: typed store contracts and canonical address model from Tasks 1 and 3.
- Produces: store-generated portable `ArtifactKey`; physical mapping confined to `FileMemoryStore`; typed read/write/delete/list/search paths; no `ResolveArtifactAsync`, `ArtifactUri`, or `file://` lookup path.

- [ ] **Step 1: Write failing boundary tests.** Store persistent and ephemeral addresses and assert returned entries carry canonical `Address` and non-empty opaque keys using `/` only; reject rooted, drive, UNC, URI-shaped, traversal, backslash, and host/workspace-containing keys. Configure two workspace roots and assert the same logical imported entry gets locally regenerated keys/paths beneath each root. Assert `ResolveArtifactAsync`/`ArtifactUri` are absent and `file://` input cannot read, write, forget, or delete.
- [ ] **Step 2: Run focused tests.** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ArtifactKeyBoundaryTests|FullyQualifiedName~FileMemoryStoreTests`; expected: failures for old URI fields/resolution and physical key generation.
- [ ] **Step 3: Implement typed mapping.** Generate a store-relative key from logical scope/address and artifact identity without concatenating an absolute path; validate the key again immediately before physical mapping; use `PathContainment` to prove the result remains below the configured store root. Keep ephemeral data in the existing in-memory store keyed by canonical value.
- [ ] **Step 4: Remove obsolete URI resolution.** Delete `ArtifactUri`, `ResolveArtifactAsync`, file-URI branches, and any `ArtifactUri()` helper; update `MemoryArtifactSource` consumers to use typed reads. Do not alter durable transaction sequencing.
- [ ] **Step 5: Run tests and build.** Run the focused command followed by `dotnet build Scrinia.sln`; expected: boundary tests pass and all production code compiles without URI APIs.
- [ ] **Step 6: Commit.** `git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/PathParser.cs src/Scrinia.Core/MemoryNaming.cs src/Scrinia.Core/PathRouter.cs src/Scrinia.Core/PathContainment.cs tests/Scrinia.Tests/FileMemoryStoreTests.cs tests/Scrinia.Tests/FileMemoryStoreWriteSurfaceTests.cs tests/Scrinia.Tests/EphemeralAliasRoundTripTests.cs tests/Scrinia.Tests/ArtifactKeyBoundaryTests.cs && git commit -m "breaking: remove URI artifact resolution"`.

### Task 5: Migrate plugin callbacks, hooks, event payloads, and test doubles

**Files:**
- Modify: `src/Scrinia.Plugin.Abstractions/HookContexts.cs`, `src/Scrinia.Plugin.Abstractions/IMemoryOperationHook.cs`, `src/Scrinia.Plugin.Abstractions/IScriniaPlugin.cs`, `src/Scrinia.Plugin.Abstractions/ScriniaPluginBase.cs`
- Modify: `src/Scrinia.Core/CompositeEventSink.cs`, `src/Scrinia.Core/CoreSinkStore.cs`, `src/Scrinia.Core/EntityTypingEventHandler.cs`, `src/Scrinia.Core/MaintenanceEventSink.cs`, `src/Scrinia.Core/Embeddings/MemoryArtifactSource.cs`, `src/Scrinia.Core/Embeddings/EmbeddingReindexer.cs`
- Modify: `src/Scrinia.Plugin.Embeddings/EmbeddingsPlugin.cs`, `src/Scrinia/Services/McpPluginHost.cs`, `src/Scrinia/Services/CoreEmbeddingEventHandler.cs`, `src/Scrinia.Plugin.Embeddings.Cli/EmbeddingsTools.cs`
- Modify: `tests/Scrinia.TestDoubles/FakeMemoryStore.cs` and all plugin/event test doubles
- Test: `tests/Scrinia.Tests/PluginAddressPropagationTests.cs`, `tests/Scrinia.Plugin.Embeddings.Tests/PluginAddressPropagationTests.cs`

**Interfaces:**
- Consumes: typed `IPluginStore`, `IMemoryEventSink`, `IMemoryOperationHook`, and `MemoryAddress` from Tasks 3–4.
- Produces: every callback/context/mutation payload contains `MemoryAddress`; plugin code uses canonical `~name` equivalence without reparsing strings; test doubles cannot implement raw-string contracts.

- [ ] **Step 1: Write failing propagation tests.** Exercise stored, appended, forgotten, before/after hook, embedding, and maintenance callbacks with `/temp/probe` and `~probe`; assert each receives one `MemoryAddress` with `Kind == Ephemeral` and `Value == "probe"`. Use a compile-time fake that has no string reinterpretation helper and assert invalid writes never invoke it.
- [ ] **Step 2: Run focused tests.** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~PluginAddressPropagationTests`; expected: signature mismatch failures against current string callbacks.
- [ ] **Step 3: Migrate payloads and handlers.** Replace qualified-name strings in public callback contracts with `MemoryAddress`; retain qualified display formatting only at adapter/rendering boundaries. Update all four `CoreSinkStore` cast sites and enumerate every event callback implementation, including the embedding CLI host.
- [ ] **Step 4: Run plugin suites.** Run `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj`; expected: all callback and reindex tests pass with typed propagation.
- [ ] **Step 5: Commit.** `git add src/Scrinia.Plugin.Abstractions src/Scrinia.Core src/Scrinia.Plugin.Embeddings src/Scrinia/Services src/Scrinia.Plugin.Embeddings.Cli tests/Scrinia.TestDoubles tests/Scrinia.Tests/PluginAddressPropagationTests.cs tests/Scrinia.Plugin.Embeddings.Tests/PluginAddressPropagationTests.cs && git commit -m "breaking: type plugin memory addresses"`.

### Task 6: Migrate MCP tools, recall form detection, resources, and chat-agent recall

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs`, `src/Scrinia.Mcp/MemoryTools.Core.cs`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, `src/Scrinia.Mcp/MemoryTools.Bundling.cs`, `src/Scrinia.Mcp/MemoryTools.Skills.cs`, `src/Scrinia.Mcp/ScriniaMcpResources.cs`, `src/Scrinia.Mcp/McpResponse.cs`
- Audit: repository search confirms there is currently no `AgentLoop.cs`; do not create a chat-agent surface in this track. If the implementation branch contains `recall_memory`, apply the same form detector and policy matrix to it.
- Modify: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`, `tests/Scrinia.Tests/CaseSensitiveMcpAppendTests.cs`, `tests/Scrinia.Tests/McpAddressSurfaceTests.cs`
- Test: `tests/Scrinia.Tests/McpAddressSurfaceTests.cs`

**Interfaces:**
- Consumes: `MemoryAddress.Parse/TryParse`, `MemoryError`, validated `MemoryWrite`, typed store/plugin contracts, and the existing NMP/2 strategy.
- Produces: raw MCP path/name inputs parsed once; recall accepts only logical address or inline NMP/2; `file://`, all URI-shaped values, and invalid artifact text return `UnsupportedInputForm`; MCP policy matrix consistently permits supported ephemeral operations in tools/resources/chat recall and rejects unsupported combinations categorically.

- [ ] **Step 1: Write exhaustive failing adapter tests.** Reflect over every `[McpServerTool]` method and resource method with an address/input parameter. For each, test `/temp/probe` and `~probe` canonicalization, persistent success, `file:///tmp/x`, `https://x`, `//host/x`, query/fragment, encoded traversal, malformed address, inline valid NMP/2, and invalid inline NMP/2. Assert categorized responses and that no persistent store receives a forbidden ephemeral operation. Include `recall_memory` if present and both resource transport registrations.
- [ ] **Step 2: Run focused tests.** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~McpAddressSurfaceTests`; expected: failures from facade-only canonicalization, file-URI branches, and missing resource/chat coverage.
- [ ] **Step 3: Implement one form detector and policy matrix.** Detect URI-shaped input before `MemoryAddress` parsing for recall; decode only complete NMP/2 inline text; otherwise call `MemoryAddress.TryParse`. Canonicalize before skill routing/policy, map `MemoryErrorCategory` to `McpResponse` without unsafe raw-input echoing, and route CLI-independent work through the same core operation path.
- [ ] **Step 4: Apply resource and agent policies.** Parse `scrinia://memory/{name}` as a logical address value after template decoding, reject encoded traversal/URI forms, and use the same MCP policy matrix. Audit all resource registrations and the chat-agent `recall_memory` path rather than relying on existing `NameRefusal` call sites.
- [ ] **Step 5: Run MCP tests and build.** Run the focused suite and `dotnet build Scrinia.sln`; expected: every enumerated MCP/resource/agent case passes and no `file://` public description remains.
- [ ] **Step 6: Commit.** `git add src/Scrinia.Mcp src/Scrinia/Services/AgentLoop.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs tests/Scrinia.Tests/CaseSensitiveMcpAppendTests.cs tests/Scrinia.Tests/McpAddressSurfaceTests.cs && git commit -m "feat: unify MCP address parsing"`.

### Task 7: Migrate CLI commands and enforce persistent-only policy

**Files:**
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs`, `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`, `src/Scrinia/Commands/WorkspaceSetup.cs`, `src/Scrinia/Commands/CliJsonContext.cs`
- Modify: `tests/Scrinia.Tests/CliEphemeralRefusalTests.cs`, `tests/Scrinia.Tests/CliSurfaceConformanceTests.cs`, `tests/Scrinia.Tests/CliAddressValidationTests.cs`

**Interfaces:**
- Consumes: shared parser/validator and the internal/core operation path from Tasks 1–6.
- Produces: `store`, `show`, `forget`, `append`, `compact`, `link`, and every other memory-targeting CLI command parse addresses through `MemoryAddress`; valid ephemeral inputs return `ForbiddenAddressKind`; file-URI/URI/traversal inputs return categorized errors; persistent commands use the same validation limits as MCP.

- [ ] **Step 1: Write failing CLI tests.** Enumerate every CLI memory command method rather than only existing refusal tests. For each, run persistent success, `~scratch`, `/temp/scratch`, `file:///x`, URI-shaped, traversal/rooted, per-element, total, chunk-count, chunk-size, and address-length cases. Assert exit code/error category, no mutation on rejection, and identical measured limits to MCP. Assert CLI calls the shared internal/core operation path, not only the public facade.
- [ ] **Step 2: Run focused tests.** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~CliAddressValidationTests|FullyQualifiedName~CliEphemeralRefusalTests`; expected: failures where `RefuseEphemeral` and direct calls bypass typed/core validation.
- [ ] **Step 3: Implement boundary policy.** Parse and canonicalize immediately after workspace setup; reject ephemeral before plugin loading or store mutation; translate `ForbiddenAddressKind`, `MalformedAddress`, `UnsupportedInputForm`, and `InvalidWriteContent` into text/JSON exits. Ensure `Store`, `Show`, `Forget`, `Append`, `Compact`, and `Link` all enumerate the same guard and do not duplicate validation logic.
- [ ] **Step 4: Update CLI JSON source generation.** Register any new error/category/address/output types in `CliJsonContext`; maintain stable machine-readable category fields.
- [ ] **Step 5: Run CLI tests.** Expected: all CLI address, policy, and limit tests pass with persistent-only semantics.
- [ ] **Step 6: Commit.** `git add src/Scrinia/Commands tests/Scrinia.Tests/CliEphemeralRefusalTests.cs tests/Scrinia.Tests/CliSurfaceConformanceTests.cs tests/Scrinia.Tests/CliAddressValidationTests.cs && git commit -m "feat: enforce typed CLI address policy"`.

### Task 8: Make bundles portable and regenerate artifact keys on import

**Files:**
- Modify: `src/Scrinia.Core/Bundles/BundleFormatService.cs`, `src/Scrinia.Core/Bundles/BundleLegacyKeys.cs`, `src/Scrinia.Core/Formats/DurableFormats.cs`, `src/Scrinia.Mcp/MemoryTools.Bundling.cs`, `src/Scrinia/Commands/ScriniaCommands.Bundle.cs`
- Modify: `tests/Scrinia.Tests/BundleV2Tests.cs`, `tests/Scrinia.Tests/BundleRoundTripTotalityTests.cs`, `tests/Scrinia.Tests/BundleZipSlipTests.cs`, `tests/Scrinia.Tests/BundleOutputContainmentTests.cs`, `tests/Scrinia.Tests/BundleImportCategoryAllowlistTests.cs`
- Test: `tests/Scrinia.Tests/BundleArtifactKeyPortabilityTests.cs`

**Interfaces:**
- Consumes: `ArtifactEntry.Address`, opaque `ArtifactKey`, typed store import/write contracts, source-generated bundle context, and core address/key boundary validation.
- Produces: bundle serialization of logical address/content/metadata/portable key; import validation of address/key data before writes; local key/path regeneration and rebuilt index associations.

- [ ] **Step 1: Write failing portability tests.** Export from workspace A, inspect zip JSON to prove no absolute path, drive, UNC, host, or `file://` value; import into workspace B and assert every entry has B-local key/path and the same logical address/content/metadata. Handcraft malicious rooted, URI-shaped, traversal, and separator-mixed keys and assert the complete import is refused before any topic write. Assert all `ArtifactEntry` members survive the round trip except intentionally regenerated physical identity.
- [ ] **Step 2: Run focused bundle tests.** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~BundleArtifactKeyPortabilityTests|FullyQualifiedName~BundleRoundTripTotalityTests`; expected: old URI persistence/rewrite assertions fail and malicious key cases expose the old boundary.
- [ ] **Step 3: Implement portable representation.** Serialize only logical `MemoryAddress`, metadata, content, and store-relative opaque key; on import discard source physical identity, parse/validate every address and key in the build pass, regenerate the importing store’s key, and rebuild index associations before any side effect. Keep current bundle version rules and legacy metadata migration only where the new representation permits it.
- [ ] **Step 4: Update existing migration tests.** Replace assertions about imported `Uri` pointing at a workspace with assertions about `ArtifactKey` portability and local regeneration; preserve tests for legacy sidecar field migration without re-exposing physical URI data.
- [ ] **Step 5: Run bundle tests and commit.** Run the focused command, then `git add src/Scrinia.Core/Bundles src/Scrinia.Core/Formats src/Scrinia.Mcp/MemoryTools.Bundling.cs src/Scrinia/Commands/ScriniaCommands.Bundle.cs tests/Scrinia.Tests/Bundle*Tests.cs && git commit -m "feat: regenerate portable bundle artifact keys"`.

### Task 9: Complete source-generated JSON registration and serialization compatibility

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs`, `src/Scrinia.Core/Bundles/BundleFormatService.cs`, `src/Scrinia.Mcp/MemoryTools.Skills.cs`, `src/Scrinia/Commands/CliJsonContext.cs`, `src/Scrinia/Commands/ConfigJsonContext.cs`, `src/Scrinia/Services/PluginClientJsonContext.cs`
- Modify/add: applicable source-generated contexts for new Core request/error/address types
- Test: `tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs`

**Interfaces:**
- Consumes: final public types from Tasks 1–8.
- Produces: trim-safe serialization for `MemoryAddress`, `MemoryAddressKind`, `MemoryError`, `MemoryWrite`, typed search/list requests, `ArtifactEntry`, bundle DTOs, MCP/CLI responses, and plugin payloads.

- [ ] **Step 1: Write failing serialization tests.** Serialize/deserialize canonical persistent and ephemeral addresses, every new request/response/error/category, `ArtifactEntry` with `ArtifactKey`, and bundle indexes using each production context. Assert JSON has no `uri`/`artifactUri`, ephemeral round-trips as canonical `~name`, and deserialization cannot create a noncanonical address. Run the tests with the production `TypeInfoResolver` rather than reflection defaults.
- [ ] **Step 2: Run focused tests.** `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SourceGeneratedAddressSerializationTests`; expected: missing generated metadata or old property-name failures.
- [ ] **Step 3: Register exact types.** Add `[JsonSerializable]` entries to the context that owns each payload, add converters only if needed for the immutable value type, and update all callers to use generated `JsonTypeInfo`. Do not add a reflection fallback.
- [ ] **Step 4: Verify trimmed-compatible build/test.** Run `dotnet build Scrinia.sln -p:PublishTrimmed=true` if supported by the solution and the focused test; expected: no source-generation diagnostics and all serialization assertions pass.
- [ ] **Step 5: Commit.** `git add src/Scrinia.Core/FileMemoryStore.cs src/Scrinia.Core/Bundles/BundleFormatService.cs src/Scrinia.Mcp/MemoryTools.Skills.cs src/Scrinia/Commands/CliJsonContext.cs src/Scrinia/Commands/ConfigJsonContext.cs src/Scrinia/Services/PluginClientJsonContext.cs tests/Scrinia.Tests/SourceGeneratedAddressSerializationTests.cs && git commit -m "feat: register typed memory JSON contracts"`.

### Task 10: Exhaustive migration, public guidance, and release notes

**Files:**
- Modify: all remaining production/test files reported by repository searches for `ArtifactEntry.Uri`, `ArtifactUri`, `ResolveArtifactAsync`, `file://`, raw address parameters, and old `MemoryWrite` constructors; include benchmark fixtures and merge fixtures under `tests/Scrinia.Benchmarks`, `tests/Scrinia.Merge.Tests`, and plugin tests.
- Modify: `docs/getting-started.md`, `docs/cli-reference.md`, `docs/plugin-authoring.md`, `docs/security.md`, `CHANGELOG.md`
- Test: `tests/Scrinia.Tests/NoObsoleteAddressSurfaceTests.cs`, `tests/Scrinia.Tests/AllAddressSurfaceConformanceTests.cs`

**Interfaces:**
- Consumes: final contracts and adapter policies from Tasks 1–9.
- Produces: repository-wide compilation against typed addresses; public guidance describing the breaking release, canonical ephemeral aliases, persistent-only CLI, MCP/resource behavior, opaque keys, and rejected file URIs.

- [ ] **Step 1: Write exhaustive conformance tests/search assertions.** Reflect over every public memory-facing method, plugin callback, event/mutation payload, store implementation, test double, MCP tool/resource, and CLI memory command. Assert no raw memory-address string contract remains; assert no public `Uri`, `ArtifactUri`, URI resolver, or supported `file://` guidance remains. Include every route-bearing surface found by search, not only current guard call sites.
- [ ] **Step 2: Run repository searches before migration cleanup.** Run `git grep -n -E 'ArtifactEntry\.Uri|ArtifactUri|ResolveArtifactAsync|file://|MemoryWrite\(' -- src tests docs CHANGELOG.md`; expected: matches identify all remaining migration sites, including tests that intentionally encode legacy persisted JSON and must be rewritten to assert discard/regeneration rather than public exposure.
- [ ] **Step 3: Migrate every match.** Update constructors, named arguments, fixtures, benchmark seed entries, merge JSON fixtures, comments/descriptions, and plugin implementations to `MemoryAddress.Parse` plus opaque `ArtifactKey`. Preserve historical planning/review files untouched. Remove obsolete tests that assert URI behavior and replace them with explicit unsupported-form/portable-key assertions.
- [ ] **Step 4: Update living docs and release notes.** Add the seven breaking changes listed in spec §9 to `CHANGELOG.md`; update CLI/MCP/plugin/security guidance to show `MemoryAddress.Parse`/`TryParse`, canonical `~name`, persistent-only CLI, typed callbacks, opaque `ArtifactKey`, and no file-URI support. Do not advertise an HTTP/server surface or rewrite historical `docs/superpowers` artifacts.
- [ ] **Step 5: Run conformance tests and searches.** Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~NoObsoleteAddressSurfaceTests|FullyQualifiedName~AllAddressSurfaceConformanceTests`; expected: PASS. Re-run the search and allow only explicitly documented legacy-storage migration literals that are not exposed or accepted as public input; expected: no obsolete public API/guidance matches.
- [ ] **Step 6: Commit.** `git add src tests docs/getting-started.md docs/cli-reference.md docs/plugin-authoring.md docs/security.md CHANGELOG.md && git commit -m "breaking: complete unified addressing migration"`.

### Task 11: Full solution verification and acceptance gate

**Files:**
- Modify: only files required to correct failures introduced by Tasks 1–10; do not broaden scope into transaction, plugin lifecycle, retrieval, embedding, or historical-document redesign.
- Test: complete default solution test suite and all contract/conformance suites.

**Interfaces:**
- Consumes: all final production contracts, generated JSON metadata, adapters, stores, plugins, bundles, docs, and tests.
- Produces: evidence for every spec §10 acceptance criterion and a clean default build/test result.

- [ ] **Step 1: Run formatting/static checks.** Run `dotnet build Scrinia.sln`; expected: PASS with no compiler errors or source-generation warnings. Run `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`; expected: PASS.
- [ ] **Step 2: Run plugin, merge, and benchmark compile/test lanes.** Run `dotnet test tests/Scrinia.Plugin.Embeddings.Tests/Scrinia.Plugin.Embeddings.Tests.csproj`, `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj`, and the default solution test command `dotnet test Scrinia.sln`; expected: PASS. Do not enable the opt-in long-running benchmark category unless diagnosing a failure.
- [ ] **Step 3: Verify no-side-effect and surface inventory acceptance.** Confirm tests cover every MCP tool/resource, CLI memory command, store implementation, plugin callback/event/mutation, typed test double, bundle path, and source-generated context; confirm invalid input is rejected before store/plugin mutation and all four error categories survive adapter translation.
- [ ] **Step 4: Run final diff/search review.** Run `git diff --check`, `git grep -n -E 'ArtifactEntry\.Uri|ArtifactUri|ResolveArtifactAsync|file://';` expected: no public contract, supported input, or living guidance occurrence. Review `git status --short` and `git diff --stat`; expected: only intended implementation files from this plan are changed.
- [ ] **Step 5: Commit verification fixes if any.** Each correction must include a focused failing test, passing test, and focused commit; never squash away the dependency-order history.

## Plan Self-Review

- **Coverage:** Tasks 1–2 cover the public value/error model and every shared write limit; Tasks 3–5 cover `ArtifactEntry`, `IMemoryStore`, `MemoryWrite`, search/list, plugin contracts, events, hooks, stores, and test doubles; Tasks 6–7 cover MCP tools, resources, chat-agent recall, CLI policy, and direct-call bypasses; Task 8 covers bundle key regeneration; Task 9 covers source-generated JSON; Task 10 covers exhaustive migration, living docs, release notes, and obsolete URI removal; Task 11 covers the complete acceptance suite.
- **No placeholders:** The plan contains no unresolved or deferred implementation marker. Every task names files, signatures, tests, commands, expected results, and a commit.
- **Type/signature consistency:** Later tasks consume the exact `MemoryAddress`, `MemoryAddressKind`, `MemoryWrite`, `MemoryError`, `ArtifactEntry.Address`, `ArtifactEntry.ArtifactKey`, and typed callback/request contracts established earlier. No task reintroduces a raw address string or URI alias.
- **Scope:** The plan preserves the approved non-goals: no durable transaction redesign, plugin state-machine redesign, retrieval indexing/ranking redesign, embedding behavior redesign, new server/HTTP surface, remote CLI options, or historical-doc rewrites.
- **Surface completeness:** The plan explicitly enumerates MCP facade, CLI direct internal calls, MCP resources on both transports, chat-agent `recall_memory`, all write routes discovered from route tables, plugin callbacks, stores, bundles, generated JSON, benchmarks, merge fixtures, and public guidance.
