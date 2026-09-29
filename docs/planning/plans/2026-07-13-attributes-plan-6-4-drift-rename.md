# Attributes P6.4 — Drift Rename (`CodeRefs` → `FileHashes`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Rename the `ArtifactEntry.CodeRefs` member to the purpose-named `FileHashes` — the `file` attribute's private drift baseline (path→hash) — repointing every consumer, WITHOUT changing the persisted sidecar key or requiring any data migration.

**Architecture:** This is a code-clarity rename, not a data change. `CodeRefs` is a `Dictionary<string,string>?` (path→SHA hash) that is dead as a display/scoring carrier (P6.1/P6.2/P6.3 moved its *paths* into `Fields["file"]`) but ALIVE as drift-detection's private hash baseline. The sidecar serializer uses a CamelCase naming policy, so the member currently persists as JSON key `"codeRefs"`. Renaming the C# member to `FileHashes` would change the key to `"fileHashes"` and silently drop every existing entry's drift baseline on load — so the renamed member carries `[JsonPropertyName("codeRefs")]` to keep the disk key exactly `"codeRefs"`. Zero migration (decision 1). All consumers (the write-time hash write, the two drift readers, the forward-fill `→ Fields["file"]`, the copy/preserve paths) repoint from `.CodeRefs` to `.FileHashes` atomically.

**Tech Stack:** C# / .NET 10 (STJ source-gen), xUnit + FluentAssertions.

## Global Constraints

- **No data migration, stable disk format.** The persisted sidecar key MUST remain `"codeRefs"` — the renamed `FileHashes` member carries `[JsonPropertyName("codeRefs")]`. An existing sidecar written before this change (JSON `"codeRefs": {…}`) MUST still deserialize its hash map into `FileHashes`. This is a hard backward-compat requirement, tested.
- **`file` attribute values stay paths-only.** `Fields["file"]` continues to carry only the path KEYS (searchable); the hashes stay private in `FileHashes`. The forward-fill `entry.CodeRefs.Keys → Fields["file"]` becomes `entry.FileHashes.Keys → Fields["file"]` — same behavior, renamed source.
- **Behavior-preserving.** Drift detection (`[drift]` marker + `list mode:'drift'`) must behave identically — same hashes, same comparison, just read from `FileHashes`. No scoring change, no display change beyond the (unchanged) drift marker.
- **Atomic rename.** A rename does not compile until all sites are updated — do it in one task. Use the exact new name `FileHashes` (decision 1, verbatim).
- **Scope:** `ArtifactEntry` only (`EphemeralEntry` has no `CodeRefs`/drift). Contained to `Scrinia.Core` + `Scrinia.Mcp` + tests. Do NOT touch `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/*`, `.superpowers/**`. The MCP tool's file-paths input param and its `[Description]` are the API surface — leave them unchanged.
- **Branch** off `main` @ current HEAD, named `feature/attributes-drift-rename`. **Never push.** Product projects build with `TreatWarningsAsErrors`.
- **Commit trailer, exactly** (every commit):
  ```
  Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
  Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
  ```

---

## Task 1: Rename `CodeRefs` → `FileHashes` (+ `[JsonPropertyName("codeRefs")]`) and repoint all consumers

**Files:**
- Modify: `src/Scrinia.Core/Models/ArtifactEntry.cs` (the member @19) — rename + `[JsonPropertyName]`
- Modify: `src/Scrinia.Core/Migration/AttributeMigration.cs` (@33-34) — forward-fill `CodeRefs.Keys → Fields["file"]`
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (@856) — copy/serialize map
- Modify: `src/Scrinia.Core/Search/KnownFields.cs` (@8,18) — doc-comment references
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (@386 store-write, @674/724/727 drift marker in `BuildFullList`, @1267 append-preserve, @1486/1495 `BuildDriftList`)
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs` (@67,142,279 — comment references only)
- Modify tests: `tests/Scrinia.Tests/AttributeMigrationTests.cs`, `ReferenceTests.cs`, `ScriniaMcpToolsTests.cs`, `WriterCutoverTests.cs` (16 `CodeRefs:` construction/assertion sites → `FileHashes:`)
- Test: add backward-compat + drift-still-works assertions (see Steps)

**Interfaces:**
- Produces: `ArtifactEntry.FileHashes` (`Dictionary<string,string>?`, JSON key `"codeRefs"`) replacing `ArtifactEntry.CodeRefs`. Every `.CodeRefs` read/write in product + test code becomes `.FileHashes`.

- [ ] **Step 1: Write the failing backward-compat test**

The load-bearing test — an old-format sidecar (JSON key `"codeRefs"`) must deserialize into `FileHashes`. Add to `tests/Scrinia.Tests/` (a new `DriftRenameTests.cs`, or extend `FileMemoryStoreTests.cs` which already exercises sidecar round-trips):
```csharp
[Fact]
public void FileHashes_deserializes_from_legacy_codeRefs_json_key()
{
    // A sidecar written BEFORE the rename uses the "codeRefs" JSON key. It must still load into FileHashes.
    const string legacyJson = """
    {"name":"n","uri":"u","originalBytes":1,"chunkCount":1,"createdAt":"2026-01-01T00:00:00+00:00",
     "description":"d","codeRefs":{"src/a.cs":"HASH_A","src/b.cs":"HASH_B"}}
    """;
    var entry = System.Text.Json.JsonSerializer.Deserialize(
        legacyJson, FileStoreJsonContext.Default.ArtifactEntry);   // use the same source-gen context the store uses
    entry!.FileHashes.Should().NotBeNull();
    entry.FileHashes!["src/a.cs"].Should().Be("HASH_A");
    entry.FileHashes!["src/b.cs"].Should().Be("HASH_B");
}

[Fact]
public void FileHashes_serializes_back_to_codeRefs_json_key()
{
    var entry = /* MakeEntry-style ArtifactEntry */ with { FileHashes = new() { ["src/a.cs"] = "HASH_A" } };
    string json = System.Text.Json.JsonSerializer.Serialize(entry, FileStoreJsonContext.Default.ArtifactEntry);
    json.Should().Contain("\"codeRefs\"");        // disk key unchanged
    json.Should().NotContain("fileHashes");       // no new key leaked
}
```
`FileStoreJsonContext` is `private partial` inside `FileMemoryStore` (FileMemoryStore.cs:171). If it's not reachable from the test, either (a) test through a real `FileMemoryStore` write→read round-trip (write an entry with `FileHashes`, read the sidecar file's raw bytes, assert the `"codeRefs"` key is present; then read it back through the store and assert `FileHashes` populated), or (b) make the context `internal` (Scrinia.Core → is `InternalsVisibleTo("Scrinia.Tests")` set? if not and you need it, prefer the round-trip approach (a) — do NOT loosen visibility just for this). The round-trip through a real store is the most faithful test — use it if the source-gen context isn't directly reachable.

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~DriftRename"`
Expected: FAIL to COMPILE — `FileHashes` does not exist yet. (For a rename, the compile failure IS the red state; that's expected.)

- [ ] **Step 3: Rename the model member + add `[JsonPropertyName]`**

In `src/Scrinia.Core/Models/ArtifactEntry.cs`, replace the `CodeRefs` member (currently `Dictionary<string, string>? CodeRefs = null,`) with:
```csharp
    /// <summary>
    /// The <c>file</c> attribute's private drift baseline: dependency file path → content SHA hash,
    /// captured at write time. Drift detection re-hashes each path and compares. The path KEYS are
    /// also surfaced as the searchable <c>file</c> attribute (<see cref="Fields"/>); the hashes are
    /// private behavior state, never displayed or scored. Persisted under the legacy JSON key
    /// <c>"codeRefs"</c> (kept stable — no migration; renamed from the old <c>CodeRefs</c> member).
    /// </summary>
    [System.Text.Json.Serialization.JsonPropertyName("codeRefs")]
    Dictionary<string, string>? FileHashes = null,
```
Keep it in the SAME positional slot in the record (ArtifactEntry is a positional record — moving it would break every positional construction; it should already be a named-only slot given callers use `CodeRefs:`). Confirm all constructions use the named arg `CodeRefs:` (they do per the inventory) so only the name changes, not position.

- [ ] **Step 4: Repoint every consumer** (`.CodeRefs` → `.FileHashes`, `CodeRefs:` → `FileHashes:`), product + tests:
  - `AttributeMigration.cs:33-34`: `if (entry.FileHashes is { Count: > 0 }) incoming["file"] = entry.FileHashes.Keys.ToArray();`
  - `FileMemoryStore.cs:856`: `FileHashes = entry.FileHashes?…`
  - `MemoryTools.Core.cs`: `@386 FileHashes: codeRefDict`; `@674 e.FileHashes`; `@724 e.FileHashes`; `@727 foreach (var (path, storedHash) in e.FileHashes)`; `@1267 FileHashes: existingEntry?.FileHashes`; `@1486 sa.Entry.FileHashes`; `@1495 foreach (…​ in sa.Entry.FileHashes)`. (The local `codeRefDict` variable name and the tool's file-paths param may stay as-is — only the MEMBER renames; renaming the local for clarity is optional and allowed but not required.)
  - `KnownFields.cs:8,18` + `MemoryOrchestrator.cs:67,142,279`: update the comment text `CodeRefs` → `FileHashes`.
  - Tests (`AttributeMigrationTests.cs`, `ReferenceTests.cs`, `ScriniaMcpToolsTests.cs`, `WriterCutoverTests.cs`): every `CodeRefs:` / `.CodeRefs` → `FileHashes`. Note `AttributeMigrationTests.MakeEntry` has a `codeRefs:` PARAMETER — rename its usages of the member but the local param name can stay `codeRefs` (maps to `FileHashes:` in the ctor); keep it consistent and readable.

  Tip: `grep -rn "CodeRefs" src/ tests/ --include=*.cs` (excluding obj/bin) must return ZERO product/test `.CodeRefs`/`CodeRefs:` code references after this step (only the JSON string `"codeRefs"` in the `[JsonPropertyName]` and any doc-comment mentioning the legacy key should remain).

- [ ] **Step 5: Run the backward-compat tests to verify they pass** — same filter as Step 2. Expected: PASS (compiles; `"codeRefs"` JSON ↔ `FileHashes`).

- [ ] **Step 6: Run the FULL suite** — `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` AND `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`. Expected: green. In particular the existing drift tests (grep tests for `drift`/`BuildDriftList`) and the migration tests (`AttributeMigrationTests` — `Fields["file"]` from `FileHashes.Keys`) must still pass, proving drift + forward-fill behavior is preserved through the rename.

- [ ] **Step 7: Build product projects warning-clean** — `dotnet build src/Scrinia.Core/Scrinia.Core.csproj` + `src/Scrinia.Mcp/Scrinia.Mcp.csproj` + `src/Scrinia.Server/Scrinia.Server.csproj`. Expected: 0 warnings each.

- [ ] **Step 8: Commit** — `"refactor(core): rename CodeRefs -> FileHashes (drift baseline; JSON key stable)"`

---

## Self-Review (author checklist — completed)

- **Spec coverage:** decision 1 — `CodeRefs` → `FileHashes` as the `file` attribute's private drift baseline; drift repoints to it; no hash migration; `file` values stay paths-only. Covered: member rename + `[JsonPropertyName("codeRefs")]` (no migration), forward-fill `FileHashes.Keys → Fields["file"]` (paths-only), the two drift readers + store hash-write repointed.
- **Backward-compat is the load-bearing risk** and has a dedicated test (legacy `"codeRefs"` JSON → `FileHashes`). Without the `[JsonPropertyName]`, every existing entry's drift baseline would be silently dropped on load (STJ unmapped-member drop) — the test guards exactly this.
- **Behavior-preserving:** the rename changes no hash, no comparison, no display; drift + `Fields["file"]` behavior is verified unchanged by the existing drift/migration suites.
- **Atomicity:** single task — a rename must land whole to compile.
- **Deferred correctly:** scorer legacy-block deletion (P6.5), model legacy-field removal (P6.6) — untouched here. `FileHashes` is ACTIVE state, NOT removed by P6.6 (decision 1: it's behavior state, not dead legacy).
