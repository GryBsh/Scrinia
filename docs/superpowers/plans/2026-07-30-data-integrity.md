# Data integrity hardening — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the four data-integrity findings (merge-driver metadata destruction + Jaccard chain, orphan sidecars, Nmp2 CRC/length) and fold in the three Stream-1 deferred items — deferring nothing.

**Architecture:** Rewrite the `.meta.json` git merge driver on the JSON DOM so it reads camelCase and preserves every field (no `Scrinia.Core` dependency — `Scrinia.Merge` stays standalone/AOT). Fix the Jaccard gate it depends on. Make `SaveIndexUnsafe` delete orphaned sidecars. Make Nmp2 `Decode` verify the header's length + CRC. Plus two small residual fixes (PathContainment, RequestContext).

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, `System.Text.Json.Nodes` (DOM), `System.IO.Hashing.Crc32`.

## Global Constraints

- **Threat/scope:** data-integrity correctness; **defer nothing** — a final-review finding is fixed in-branch.
- **Build/test ONLY with** `-p:NuGetAudit=false`. Never bare `dotnet build Scrinia.sln` (pre-existing AppHost NuGet-audit blocker).
- `src/` projects are `TreatWarningsAsErrors=true` → production code warning-clean. **`src/Scrinia.Merge` is also `PublishAot=true`/`PublishTrimmed=true`** — no reflection-over-types, trim-clean (the JSON DOM is trim-safe; do NOT add a `Scrinia.Core` ProjectReference).
- Test projects are exempt from warnings-as-errors (CS8019/CA1707 in test files are not findings). LSP may show phantom CS8019/CS0117 on edited src files while `dotnet build` is 0/0 — trust the build, not the LSP stream.
- Conventional-commit messages (`fix:`/`feat:`). End every commit message with:
  `Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>`
- **Do not touch / never stage** the user's uncommitted working-tree files: `.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `src/Scrinia.Mcp/prompts/guide.md`. Stage only each task's own files.
- Test commands:
  - Merge: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false`
  - Core/MCP: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
  - Server: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`
- A pre-existing flaky benchmark test (`UpdateCorrectnessBenchmarkTests.UpdatedFact_OldContentNotReturned`, parallel-load) is unrelated — passes in isolation; don't chase it.

## File Structure

- Modify `src/Scrinia.Merge/KeywordJaccard.cs` (T1)
- Modify `src/Scrinia.Merge/MetaJsonMerger.cs` (T2) — DOM rewrite; removes `MetaEntry`/`MetaEntryJsonContext`
- Modify `src/Scrinia.Core/FileMemoryStore.cs` (T3) — `SaveIndexUnsafe` orphan cleanup
- Modify `src/Scrinia.Core/Encoding/Nmp2Strategy.cs` (T4) — `Decode` verification
- Modify `src/Scrinia.Core/PathContainment.cs` (T5) — dead branch + IOException
- Modify `src/Scrinia.Server/Auth/RequestContext.cs` (T6) — `CanAccessStore` delegate

Tests: `tests/Scrinia.Merge.Tests/KeywordJaccardTests.cs`, `.../MetaJsonMergerTests.cs` (rewrite), `tests/Scrinia.Tests/SaveIndexOrphanSidecarTests.cs`, `tests/Scrinia.Tests/Nmp2IntegrityTests.cs`, `tests/Scrinia.Tests/PathContainmentHardeningTests.cs` (extend), `tests/Scrinia.Server.Tests/*` (T6).

**Order:** T1 first (T2's Jaccard gate depends on it). T2 next. T3–T6 independent.

---

### Task T1: Fix KeywordJaccard null-vs-populated

**Files:**
- Modify: `src/Scrinia.Merge/KeywordJaccard.cs`
- Test: `tests/Scrinia.Merge.Tests/KeywordJaccardTests.cs` (create, or add to an existing Jaccard test file if present)

**Interfaces:**
- Produces: `double KeywordJaccard.Compute(string[]? a, string[]? b)` — same signature; corrected semantics.

- [ ] **Step 1: Write failing tests**

```csharp
using Scrinia.Merge;
using FluentAssertions;
using Xunit;

namespace Scrinia.Merge.Tests;

public sealed class KeywordJaccardTests
{
    [Fact] public void BothEmpty_IsIdentical() {
        KeywordJaccard.Compute(null, null).Should().Be(1.0);
        KeywordJaccard.Compute([], []).Should().Be(1.0);
        KeywordJaccard.Compute(null, []).Should().Be(1.0);
    }
    [Fact] public void OneEmptyOnePopulated_IsDisjoint() {
        KeywordJaccard.Compute(null, ["auth"]).Should().Be(0.0);
        KeywordJaccard.Compute(["auth"], null).Should().Be(0.0);
        KeywordJaccard.Compute([], ["auth"]).Should().Be(0.0);
    }
    [Fact] public void Overlap_IsRatio() {
        KeywordJaccard.Compute(["a","b"], ["a","b"]).Should().Be(1.0);
        KeywordJaccard.Compute(["a","b"], ["a","c"]).Should().BeApproximately(1.0/3.0, 1e-9);
    }
}
```

- [ ] **Step 2: Run — verify `OneEmptyOnePopulated_IsDisjoint` FAILS**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj --filter KeywordJaccardTests -p:NuGetAudit=false`
Expected: FAIL (null-vs-populated currently returns 1.0).

- [ ] **Step 3: Fix the guard**

Replace the body of `Compute`:

```csharp
public static double Compute(string[]? a, string[]? b)
{
    bool aEmpty = a is null || a.Length == 0;
    bool bEmpty = b is null || b.Length == 0;
    if (aEmpty && bEmpty) return 1.0;   // both empty = identical
    if (aEmpty || bEmpty) return 0.0;   // exactly one empty = disjoint

    var setA = new HashSet<string>(a!, StringComparer.OrdinalIgnoreCase);
    var setB = new HashSet<string>(b!, StringComparer.OrdinalIgnoreCase);
    int intersection = setA.Count(x => setB.Contains(x));
    int union = setA.Count + setB.Count - intersection;
    return union == 0 ? 1.0 : (double)intersection / union;
}
```

- [ ] **Step 4: Run — GREEN**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj --filter KeywordJaccardTests -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Merge/KeywordJaccard.cs tests/Scrinia.Merge.Tests/KeywordJaccardTests.cs
git commit -m "fix(merge): KeywordJaccard treats null-vs-populated as disjoint, not identical"
```

---

### Task T2: Rewrite MetaJsonMerger on the JSON DOM (lossless, camelCase)

**Files:**
- Modify: `src/Scrinia.Merge/MetaJsonMerger.cs` (full rewrite; remove `MetaEntry` + `MetaEntryJsonContext`)
- Test: `tests/Scrinia.Merge.Tests/MetaJsonMergerTests.cs` (rewrite PascalCase fixtures → camelCase; add preserve-fields regression)

**Interfaces:**
- Consumes: `KeywordJaccard.Compute` (T1, corrected), `MergeConfig` (`JaccardThreshold`).
- Produces: `MetaJsonMerger.Merge(string ancestorPath, string oursPath, string theirsPath, MergeConfig) -> MergeResult` (unchanged signature/enum); now reads camelCase and preserves all sidecar fields.

**Context:** the current merger deserializes camelCase sidecars with a default PascalCase/case-sensitive context → all-null `MetaEntry` → false Jaccard 1.0 → rewrites the file as PascalCase nulls, dropping `contentPreview`/`termFrequencies`/`chunkEntries`/`codeRefs`/`attributes`. The existing tests only pass because they feed PascalCase fixtures — those must be rewritten to camelCase.

- [ ] **Step 1: Write the failing regression test** (and rewrite existing fixtures to camelCase)

Add this test; also convert every existing test in the file that writes a `.meta.json` fixture from PascalCase (`"Name"`, `"Keywords"`) to camelCase (`"name"`, `"keywords"`) so they exercise the real on-disk shape:

```csharp
[Fact]
public void Merge_PreservesUnknownFields_AndUnionsKeywords()
{
    string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string a = Path.Combine(dir, "a.meta.json");
    string ours = Path.Combine(dir, "ours.meta.json");
    string theirs = Path.Combine(dir, "theirs.meta.json");

    // Real-shape camelCase sidecars with fields the old MetaEntry never modeled.
    string oursJson = """
    {"name":"m","uri":"","originalBytes":100,"chunkCount":1,"keywords":["auth","jwt"],
     "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-02T00:00:00Z",
     "contentPreview":"PRESERVE ME","termFrequencies":{"auth":3},"attributes":{"topic":["x"]}}
    """;
    string theirsJson = """
    {"name":"m","uri":"","originalBytes":100,"chunkCount":1,"keywords":["auth","jwt","oauth"],
     "createdAt":"2026-01-01T00:00:00Z","updatedAt":"2026-01-03T00:00:00Z",
     "contentPreview":"PRESERVE ME","termFrequencies":{"auth":3},"attributes":{"topic":["x"]}}
    """;
    File.WriteAllText(a, oursJson);       // ancestor == ours (only theirs changed keywords)
    File.WriteAllText(ours, oursJson);
    File.WriteAllText(theirs, theirsJson);

    var result = MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig(JaccardThreshold: 0.5));
    result.Should().Be(MetaJsonMerger.MergeResult.Resolved);

    using var doc = JsonDocument.Parse(File.ReadAllText(ours));
    var root = doc.RootElement;
    // Unknown fields preserved verbatim:
    root.GetProperty("contentPreview").GetString().Should().Be("PRESERVE ME");
    root.GetProperty("termFrequencies").GetProperty("auth").GetInt32().Should().Be(3);
    root.GetProperty("attributes").GetProperty("topic")[0].GetString().Should().Be("x");
    // Keywords unioned:
    root.GetProperty("keywords").EnumerateArray().Select(e => e.GetString())
        .Should().BeEquivalentTo(["auth", "jwt", "oauth"]);
    // updatedAt = latest:
    root.GetProperty("updatedAt").GetString().Should().Be("2026-01-03T00:00:00Z");
}

[Fact]
public void Merge_DissimilarKeywords_IsConflict_FileUntouched()
{
    string dir = Path.Combine(Path.GetTempPath(), "mm-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(dir);
    string a = Path.Combine(dir, "a.meta.json"), ours = Path.Combine(dir, "ours.meta.json"), theirs = Path.Combine(dir, "theirs.meta.json");
    string oursJson = """{"name":"m","keywords":["auth","jwt"],"contentPreview":"OURS"}""";
    string theirsJson = """{"name":"m","keywords":["billing","invoice"],"contentPreview":"THEIRS"}""";
    File.WriteAllText(a, """{"name":"m","keywords":[]}"""); File.WriteAllText(ours, oursJson); File.WriteAllText(theirs, theirsJson);

    MetaJsonMerger.Merge(a, ours, theirs, new MergeConfig()).Should().Be(MetaJsonMerger.MergeResult.Conflict);
    File.ReadAllText(ours).Should().Be(oursJson); // untouched
}
```

Add `using System.Text.Json;` if the test file lacks it.

- [ ] **Step 2: Run — verify RED**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj --filter MetaJsonMergerTests -p:NuGetAudit=false`
Expected: the preserve-fields test FAILS (old merger drops `contentPreview` etc. and mis-reads camelCase). Some rewritten camelCase fixtures may also fail against the old PascalCase merger — that is the bug, expected RED.

- [ ] **Step 3: Replace `MetaJsonMerger.cs` with the DOM implementation**

```csharp
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Scrinia.Merge;

public static class MetaJsonMerger
{
    public enum MergeResult { Resolved, Conflict }

    private static readonly JsonNodeOptions NodeOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    public static MergeResult Merge(string ancestorPath, string oursPath, string theirsPath, MergeConfig config)
    {
        JsonObject? ours = Parse(oursPath);
        JsonObject? theirs = Parse(theirsPath);
        JsonObject? ancestor = File.Exists(ancestorPath) ? Parse(ancestorPath) : null;

        // A missing/invalid side can't be auto-merged safely — let git surface the conflict.
        if (ours is null || theirs is null)
            return MergeResult.Conflict;

        double jaccard = KeywordJaccard.Compute(GetStrings(ours, "keywords"), GetStrings(theirs, "keywords"));
        if (jaccard < config.JaccardThreshold)
            return MergeResult.Conflict;

        // Per-field 3-way merge of scalar metadata: only-one-side-changed → take it; both changed → conflict.
        if (!TryMergeField(GetString(ancestor, "name"), GetString(ours, "name"), GetString(theirs, "name"), out var name)) return MergeResult.Conflict;
        if (!TryMergeField(GetString(ancestor, "uri"), GetString(ours, "uri"), GetString(theirs, "uri"), out var uri)) return MergeResult.Conflict;
        if (!TryMergeField(GetString(ancestor, "description"), GetString(ours, "description"), GetString(theirs, "description"), out var description)) return MergeResult.Conflict;
        if (!TryMergeField(GetInt(ancestor, "originalBytes"), GetInt(ours, "originalBytes"), GetInt(theirs, "originalBytes"), out var originalBytesN)) return MergeResult.Conflict;
        if (!TryMergeField(GetInt(ancestor, "chunkCount"), GetInt(ours, "chunkCount"), GetInt(theirs, "chunkCount"), out var chunkCountN)) return MergeResult.Conflict;
        int originalBytes = originalBytesN ?? GetInt(ours, "originalBytes") ?? 0;
        int chunkCount = chunkCountN ?? GetInt(ours, "chunkCount") ?? 0;

        // Union keywords (already known similar by Jaccard).
        var keywords = new HashSet<string>(GetStrings(ours, "keywords") ?? [], StringComparer.OrdinalIgnoreCase);
        foreach (var kw in GetStrings(theirs, "keywords") ?? []) keywords.Add(kw);

        string? oursUpdated = GetString(ours, "updatedAt"), theirsUpdated = GetString(theirs, "updatedAt");
        string? updatedAt = CompareTimestamps(oursUpdated, theirsUpdated) > 0 ? oursUpdated : theirsUpdated;
        string? oursCreated = GetString(ours, "createdAt"), theirsCreated = GetString(theirs, "createdAt");
        string? createdAt = CompareTimestamps(oursCreated, theirsCreated) < 0 ? oursCreated : theirsCreated;

        // Overlay the merged fields onto OURS' full object — preserves every unknown property
        // (contentPreview, termFrequencies, chunkEntries, codeRefs, attributes, …) verbatim.
        SetString(ours, "name", name);
        SetString(ours, "uri", uri);
        SetString(ours, "description", description);
        ours["originalBytes"] = originalBytes;
        ours["chunkCount"] = chunkCount;
        ours["keywords"] = new JsonArray(keywords.Order(StringComparer.OrdinalIgnoreCase)
            .Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
        SetString(ours, "updatedAt", updatedAt);
        SetString(ours, "createdAt", createdAt);

        File.WriteAllText(oursPath, ours.ToJsonString(WriteOptions));
        return MergeResult.Resolved;
    }

    private static JsonObject? Parse(string path)
    {
        try { return JsonNode.Parse(File.ReadAllText(path), NodeOptions) as JsonObject; }
        catch (JsonException) { return null; }
    }

    private static string? GetString(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    private static int? GetInt(JsonObject? o, string key) =>
        o is not null && o.TryGetPropertyValue(key, out var n) && n is JsonValue v && v.TryGetValue<int>(out var i) ? i : null;

    private static string[]? GetStrings(JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is not JsonArray arr) return null;
        var list = new List<string>(arr.Count);
        foreach (var el in arr)
            if (el is JsonValue v && v.TryGetValue<string>(out var s)) list.Add(s);
        return list.ToArray();
    }

    private static void SetString(JsonObject o, string key, string? value)
    {
        if (value is null) o.Remove(key);
        else o[key] = value;
    }

    // Returns false when both sides changed the field to different values (real conflict).
    private static bool TryMergeField<T>(T ancestor, T ours, T theirs, out T result)
    {
        var cmp = EqualityComparer<T>.Default;
        if (cmp.Equals(ours, theirs)) { result = ours; return true; }
        if (cmp.Equals(ours, ancestor)) { result = theirs; return true; }
        if (cmp.Equals(theirs, ancestor)) { result = ours; return true; }
        result = ours; return false;
    }

    private static int CompareTimestamps(string? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        return string.Compare(a, b, StringComparison.Ordinal);
    }
}
```

- [ ] **Step 4: Run — GREEN (merger tests) then the whole Merge suite**

Run: `dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false`
Expected: PASS (all merger tests, including the rewritten camelCase fixtures and the preserve-fields regression). Also build `dotnet build src/Scrinia.Merge/Scrinia.Merge.csproj -p:NuGetAudit=false` → 0/0 (trim-clean, no Core dep).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Merge/MetaJsonMerger.cs tests/Scrinia.Merge.Tests/MetaJsonMergerTests.cs
git commit -m "fix(merge): DOM-based meta.json merge preserves all fields and reads camelCase"
```

---

### Task T3: SaveIndexUnsafe deletes orphaned sidecars

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (`SaveIndexUnsafe`, ~L1033)
- Test: `tests/Scrinia.Tests/SaveIndexOrphanSidecarTests.cs`

**Interfaces:**
- Consumes: `SanitizeName`, `GetStoreDirForScope`, `WriteSidecars`, `LoadEntriesFromSidecars` (existing private members).

- [ ] **Step 1: Write the failing test**

Drive a shrink through the same public API `FileMemoryStoreTests` uses to save an index (read that test file first — reuse its store setup + save entry-point). The test: save entries for `[a, b]`, then save `[a]` (shrunk), then assert (1) `b.meta.json` no longer exists on disk, and (2) a fresh `FileMemoryStore` over the same dir does NOT return `b` (no resurrection). Sketch:

```csharp
using Scrinia.Core;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class SaveIndexOrphanSidecarTests
{
    [Fact]
    public void SaveIndex_Shrink_DeletesOrphanSidecar_NoResurrection()
    {
        string root = Path.Combine(Path.GetTempPath(), "orphan-" + Guid.NewGuid().ToString("N"));
        // ... construct FileMemoryStore over `root`, matching FileMemoryStoreTests setup ...
        // save two entries (a, b) for a scope via the public save path,
        // then save only (a).
        // Assert the on-disk b.meta.json is gone:
        // File.Exists(Path.Combine(<scope store dir>, "b.meta.json")).Should().BeFalse();
        // Assert a cold reload doesn't resurrect b:
        // new FileMemoryStore(root).LoadIndex(scope).Select(e => e.Name).Should().NotContain("b");
    }
}
```

Fill the sketch in against the real `FileMemoryStore` public API and the scope/store-dir helpers used by the existing `FileMemoryStoreTests` (e.g. `SaveIndex_InvalidatesAndUpdatesCache`). If the only public save path adds/replaces without shrink, use the same one twice with a smaller set the second time.

- [ ] **Step 2: Run — verify it FAILS (orphan `b.meta.json` survives / resurrects)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter SaveIndexOrphanSidecarTests -p:NuGetAudit=false`
Expected: FAIL.

- [ ] **Step 3: Add orphan deletion to `SaveIndexUnsafe`**

Replace `SaveIndexUnsafe` body:

```csharp
private void SaveIndexUnsafe(List<ArtifactEntry> entries, string scope)
{
    string storeDir = GetStoreDirForScope(scope);
    Directory.CreateDirectory(storeDir);

    // Delete orphaned sidecars: any *.meta.json whose entry is no longer present. Runs under the
    // caller's write lock; storeDir holds only artifact sidecars, so this is scope-local and safe.
    var expected = new HashSet<string>(
        entries.Select(e => SanitizeName(e.Name) + ".meta.json"),
        StringComparer.OrdinalIgnoreCase);
    foreach (string metaFile in Directory.EnumerateFiles(storeDir, "*.meta.json").ToList())
        if (!expected.Contains(Path.GetFileName(metaFile)))
            File.Delete(metaFile);

    // Write per-artifact sidecar metadata files
    WriteSidecars(entries, storeDir);

    // Update the in-memory cache
    _indexCache[scope] = new CachedIndex(entries);

    // Invalidate topic discovery cache when saving a topic scope
    if (scope.StartsWith("local:", StringComparison.Ordinal))
        _cachedTopics = null;
}
```

- [ ] **Step 4: Run — GREEN, then the full Core/MCP suite**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS (new test green; existing FileMemoryStore/index tests still green — regression gate).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/SaveIndexOrphanSidecarTests.cs
git commit -m "fix(store): SaveIndexUnsafe deletes orphaned sidecars on shrink"
```

---

### Task T4: Nmp2 Decode verifies length + CRC32

**Files:**
- Modify: `src/Scrinia.Core/Encoding/Nmp2Strategy.cs` (`Decode`, ~L123)
- Test: `tests/Scrinia.Tests/Nmp2IntegrityTests.cs`

**Interfaces:**
- Consumes: existing `ParseHeader` (returns `ArtifactMetadata { OriginalBytes, Crc32 }`), `Crc32.HashToUInt32` (`System.IO.Hashing`, already imported).
- Produces: `Decode(string)` now throws `InvalidDataException` on a length or CRC mismatch.

- [ ] **Step 1: Write the failing tests**

```csharp
using System.Text;
using Scrinia.Core.Encoding;
using FluentAssertions;
using Xunit;

namespace Scrinia.Tests;

public sealed class Nmp2IntegrityTests
{
    private static string Encode(string text) =>
        Nmp2Strategy.Instance.Encode(Encoding.UTF8.GetBytes(text), new EncodingOptions()).Artifact;

    [Fact]
    public void RoundTrip_Succeeds()
    {
        string art = Encode("hello world, this is a compressible payload payload payload");
        Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(art)).Should().StartWith("hello world");
    }

    [Fact]
    public void TamperedCrc_Throws()
    {
        string art = Encode("integrity matters");
        // Corrupt the CRC hex in the header ("CRC32:XXXXXXXX").
        int i = art.IndexOf("CRC32:", StringComparison.Ordinal);
        string tampered = art[..(i + 6)] + "DEADBEEF" + art[(i + 14)..];
        Action act = () => Nmp2Strategy.Instance.Decode(tampered);
        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public void TamperedLength_Throws()
    {
        string art = Encode("length matters too");
        // Replace the declared "{N}B" with a wrong, larger N.
        int b = art.IndexOf('B');           // end of the {N}B token on the header line
        int sp = art.IndexOf(' ') + 1;      // start of {N}
        string tampered = art[..sp] + "999999" + art[b..];
        Action act = () => Nmp2Strategy.Instance.Decode(tampered);
        act.Should().Throw<InvalidDataException>();
    }
}
```

(Adjust the `EncodingOptions` construction to the real ctor/defaults if different — read `EncodingOptions`.)

- [ ] **Step 2: Run — verify `TamperedCrc_Throws` / `TamperedLength_Throws` FAIL**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter Nmp2IntegrityTests -p:NuGetAudit=false`
Expected: the tamper tests FAIL (Decode currently accepts corrupt data).

- [ ] **Step 3: Add verification to `Decode`**

Rename the current `public byte[] Decode(string artifact)` body to a private `DecodeCore`, and add a verifying wrapper:

```csharp
public byte[] Decode(string artifact)
{
    byte[] decoded = DecodeCore(artifact);
    VerifyIntegrity(artifact, decoded);
    return decoded;
}

private byte[] DecodeCore(string artifact)
{
    // ... the entire existing Decode body, unchanged ...
}

/// <summary>Verifies decoded output against the header's declared length and CRC32.
/// Length is checked when the header declares one (&gt; 0); CRC when present.</summary>
private static void VerifyIntegrity(string artifact, byte[] decoded)
{
    var meta = Instance.ParseHeader(artifact);
    if (meta.OriginalBytes > 0 && decoded.Length != meta.OriginalBytes)
        throw new InvalidDataException(
            $"NMP/2 length mismatch: header declares {meta.OriginalBytes}B, decoded {decoded.Length}B.");
    if (meta.Crc32 is uint expected)
    {
        uint actual = Crc32.HashToUInt32(decoded);
        if (actual != expected)
            throw new InvalidDataException(
                $"NMP/2 CRC32 mismatch: header {expected:X8}, decoded {actual:X8}.");
    }
}
```

(`ParseHeader` is an instance method; call it via `Instance` or make `VerifyIntegrity` non-static and call `ParseHeader(artifact)`. Either is fine — match the file's style.)

- [ ] **Step 4: Run — GREEN, then the full Core/MCP suite (multi-chunk round-trips must stay green)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS. If any existing multi-chunk decode test fails, the multi-chunk header does not declare length/CRC in the same positions — in that case the `> 0` length guard already skips it; investigate before weakening, and confirm multi-chunk round-trips pass.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Encoding/Nmp2Strategy.cs tests/Scrinia.Tests/Nmp2IntegrityTests.cs
git commit -m "fix(encoding): Nmp2 Decode verifies declared length and CRC32"
```

---

### Task T5: PathContainment deferred residuals (dead branch + fail-closed IOException)

**Files:**
- Modify: `src/Scrinia.Core/PathContainment.cs`
- Test: `tests/Scrinia.Tests/PathContainmentHardeningTests.cs` (extend)

**Interfaces:**
- Produces: `IsWithin` returns `false` (not throw) when path resolution hits an `IOException`; `ResolveReal` no longer contains the dead ternary branch.

- [ ] **Step 1: Write the failing/■ test**

The IOException path (symlink loop) is environment-dependent and hard to trigger portably. Add a test that at least asserts the fail-closed contract is exercised where creatable, and otherwise soft-skips (mirroring the existing symlink tests' `TryCreateDirSymlink` skip pattern). Add to `PathContainmentHardeningTests`:

```csharp
[Fact]
public void SymlinkLoop_FailsClosed_ReturnsFalse()
{
    string baseDir = Path.Combine(Path.GetTempPath(), "pc-loop-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(baseDir);
    string loopA = Path.Combine(baseDir, "a");
    string loopB = Path.Combine(baseDir, "b");
    try
    {
        // a -> b, b -> a : resolving either throws IOException (too many links / loop).
        Directory.CreateSymbolicLink(loopA, loopB);
        Directory.CreateSymbolicLink(loopB, loopA);
    }
    catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
    { return; } // environment can't create symlinks; skip
    // Must fail closed (return false), never throw.
    PathContainment.IsWithin(baseDir, Path.Combine(loopA, "x")).Should().BeFalse();
}
```

- [ ] **Step 2: Run — verify it FAILS (currently an unhandled IOException propagates)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter PathContainmentHardeningTests -p:NuGetAudit=false`
Expected: FAIL where symlinks are creatable (the IOException escapes instead of returning false); soft-skip otherwise.

- [ ] **Step 3: Fail closed + remove the dead branch**

In `IsWithin`, wrap the two `ResolveReal` calls:

```csharp
public static bool IsWithin(string baseDir, string candidate)
{
    string baseFull, candFull;
    try
    {
        baseFull = ResolveReal(baseDir);
        candFull = ResolveReal(candidate);
    }
    catch (IOException)
    {
        return false; // symlink loop / too many links: containment unprovable → deny (fail closed)
    }
    if (string.Equals(baseFull, candFull, PathComparison))
        return true;
    string withSep = baseFull.EndsWith(Path.DirectorySeparatorChar)
        ? baseFull
        : baseFull + Path.DirectorySeparatorChar;
    return candFull.StartsWith(withSep, PathComparison);
}
```

In `ResolveReal`, replace the dead ternary. The current line is:

```csharp
if (target is not null)
    resolved = Path.IsPathRooted(target)
        ? target
        : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(resolved)!, target));
```

Replace with (ResolveLinkTarget(returnFinalTarget: true).FullName is always an absolute path):

```csharp
// FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true).FullName is always absolute,
// so no relative-combine branch is reachable here.
if (target is not null)
    resolved = target;
```

- [ ] **Step 4: Run — GREEN, then full Core/MCP suite (containment regression)**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/PathContainment.cs tests/Scrinia.Tests/PathContainmentHardeningTests.cs
git commit -m "fix(security): PathContainment fails closed on symlink-loop IOException; drop dead branch"
```

---

### Task T6: RequestContext.CanAccessStore delegates to IsGlobalAdmin

**Files:**
- Modify: `src/Scrinia.Server/Auth/RequestContext.cs`
- Test: `tests/Scrinia.Server.Tests/` — add a small unit test (new `RequestContextTests.cs`) if none covers `CanAccessStore`; otherwise the existing `KeyIsolationTests` already exercise the behavior (note that in the report).

**Interfaces:**
- Produces: `CanAccessStore` unchanged behavior (`"*"` → all; named → match), now delegating to `IsGlobalAdmin`.

- [ ] **Step 1: Write the test**

```csharp
using Scrinia.Server.Auth;
using FluentAssertions;
using Xunit;

namespace Scrinia.Server.Tests;

public sealed class RequestContextTests
{
    [Fact] public void WildcardStore_IsGlobalAdmin_AccessesAny() {
        var ctx = new RequestContext { Stores = ["*"] };
        ctx.IsGlobalAdmin.Should().BeTrue();
        ctx.CanAccessStore("anything").Should().BeTrue();
    }
    [Fact] public void NamedStores_MatchOnlyThemselves() {
        var ctx = new RequestContext { Stores = ["alpha"] };
        ctx.IsGlobalAdmin.Should().BeFalse();
        ctx.CanAccessStore("alpha").Should().BeTrue();
        ctx.CanAccessStore("beta").Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run — verify it passes on current code (behavior-preserving refactor) or fails to compile if `IsGlobalAdmin` visibility is an issue**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter RequestContextTests -p:NuGetAudit=false`
Expected: PASS (this is a refactor; the test pins behavior before and after).

- [ ] **Step 3: Delegate to IsGlobalAdmin**

Replace `CanAccessStore`:

```csharp
public bool CanAccessStore(string storeName) =>
    IsGlobalAdmin || Stores.Contains(storeName, StringComparer.OrdinalIgnoreCase);
```

(Leave `IsGlobalAdmin` as the single wildcard check.)

- [ ] **Step 4: Run — GREEN, then full server suite**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`
Expected: PASS (KeyIsolationTests + all authz tests still green).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Auth/RequestContext.cs tests/Scrinia.Server.Tests/RequestContextTests.cs
git commit -m "refactor(server): CanAccessStore delegates to IsGlobalAdmin (single wildcard source)"
```

---

## Final verification

- [ ] Run all three suites green:

```bash
dotnet test tests/Scrinia.Merge.Tests/Scrinia.Merge.Tests.csproj -p:NuGetAudit=false
dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false
dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false
```

Expected: all green (allowing the one pre-existing parallel-load benchmark flake, which passes in isolation).

- [ ] `git status` shows only this plan's files staged/committed — the user's four working-tree files remain untouched and unstaged.
