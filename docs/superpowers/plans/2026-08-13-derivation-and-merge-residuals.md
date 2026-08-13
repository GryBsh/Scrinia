# Derivation and Merge Residuals Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Close the seven defects in `docs/superpowers/specs/2026-08-13-derivation-and-merge-residuals-design.md` — four sidecar-merge divergences, one missing derivation input, and two write seams that drop what they cannot represent.

**Architecture:** Every *field-level* `.meta.json` merge rule moves into `Scrinia.Core.Merge`, is force-duplicated into the AOT `Scrinia.Merge` driver, and is pinned by a parity test that drives both copies through identical cases. `MemoryDerivation.ForAppend` gains the `fileHasher` input `ForStore` already has. `EphemeralEntry` gains `FileHashes`, and the three write seams that project ephemeral state stop dropping fields.

**Tech Stack:** C# / .NET 10, xunit 2.9.3, FluentAssertions 6.12.2, `Xunit.SkippableFact` 1.5.61. No mocking frameworks — hand-written doubles in `tests/Scrinia.TestDoubles`.

**Baseline:** `main` at `4170858`. Suite is **2040 passed, 0 failed, 4 skipped**. Build is clean.

## Global Constraints

- **`src/Scrinia.Merge/Scrinia.Merge.csproj` must not gain a `ProjectReference`.** Hard constraint. It is a standalone AOT executable. The duplication this plan creates is forced by that constraint, not chosen.
- `TreatWarningsAsErrors=true` for every project not ending in `.Tests` or `.Benchmarks`. A warning is a build failure.
- No mocking frameworks. Hand-written test doubles only.
- Behavioural questions about derivation are governed by `docs/superpowers/analysis/2026-08-11-derivation-inventory.md` (rulings D-01…D-29). Cite the ruling id at the line that implements it.
- **Every duplicated rule must name its Core original in a comment**, following `src/Scrinia.Merge/MetaJsonMerger.cs:38-47`.
- Prefix every shell command with `rtk`, including inside `&&` chains.
- **Do not write Scrinia memories** (`remember()`, `link()`, `forget()`). Findings go in your report file.
- **Never `git add .` or `git add -A`.** Stage explicit paths only. `cmds.txt` is untracked and not part of this work; `.scrinia/memories/findings/facts.json` shows as modified and is a derived cache — never stage, revert or mention it.

## Tooling Notes (read before your first command)

- **`rtk dotnet build Scrinia.sln` is a reliable gate.** On success it prints a condensed one-line summary. On failure it falls back to raw MSBuild output where `Build FAILED` and `N Error(s)` appear **above** the trailing `Time Elapsed` line. **Never judge by the last line — the exit code is authoritative.**
- **The C# language server emits phantom errors** (CS0101/CS1061/CS0234/CS1503), sometimes for files that do not exist. Trust `rtk dotnet build`, not the diagnostic stream.
- **Stale-binary trap.** A test file that fails to compile lets `dotnet test --no-build` re-run stale assemblies and report green. The tell is a project's test count not rising after you added a test. **Always build before testing, and gate on an explicit error count.**
- `rtk grep` and `rtk proxy grep … | head -N` both silently drop matches. Use the Grep tool.

## File Structure

**Created — `Scrinia.Core.Merge`, the home of every field-level rule:**

| File | Responsibility |
|---|---|
| `src/Scrinia.Core/Merge/KeywordJaccard.cs` | Jaccard similarity over two keyword arrays. Moved from `Scrinia.Merge`. |
| `src/Scrinia.Core/Merge/SidecarAdmission.cs` | Whether two sidecars are similar enough to auto-merge. |
| `src/Scrinia.Core/Merge/SidecarTermFrequencyMerge.cs` | Term-frequency union: key union, max on shared keys. |
| `src/Scrinia.Core/Merge/SidecarTimestampMerge.cs` | Latest `updatedAt`, earliest `createdAt`, compared as instants. |

**Created — parity tests, one per rule:**

| File |
|---|
| `tests/Scrinia.Merge.Tests/SidecarTermFrequencyMergeParityTests.cs` |
| `tests/Scrinia.Merge.Tests/SidecarTimestampMergeParityTests.cs` |
| `tests/Scrinia.Merge.Tests/KeywordJaccardParityTests.cs` |
| `tests/Scrinia.Tests/EphemeralLifecycleTests.cs` |

**Modified:**

| File | Change |
|---|---|
| `src/Scrinia.Merge/KeywordJaccard.cs` | Becomes a documented forced duplicate of the Core original. |
| `src/Scrinia.Merge/MetaJsonMerger.cs` | Carries duplicates of the three new rules and calls them; gains the term-frequency merge it never had. |
| `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` | `TryComputeAutoResolvedMetaJson` calls the shared rules and gains the admission gate; `Compact` handles ephemerals. |
| `src/Scrinia.Core/MemoryDerivation.cs` | `ForAppend` gains `fileHasher`. |
| `src/Scrinia.Core/Models/EphemeralEntry.cs` | Gains `FileHashes`. |
| `src/Scrinia.Core/FileMemoryStore.cs` | Passes the hasher to `ForAppend`; three ephemeral projections carry `FileHashes`; `CopyMemory` carries metadata both cross-kind directions. |
| `src/Scrinia/HttpMemoryStore.cs` | Optional workspace root; stale comment corrected. |
| `src/Scrinia/Commands/ScriniaCommands.Serve.cs` | Passes the resolved root. |
| `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs` | Term frequencies survive a real git merge. |
| `tests/Scrinia.Tests/ReconcileScanTests.cs` | Gate refusal, `createdAt`, one-sided term frequencies. |
| `tests/Scrinia.Tests/MemoryDerivationTests.cs` | Append hashes new refs, preserves existing baseline. |
| `tests/Scrinia.Tests/FileMemoryStoreTests.cs` | `CopyMemory` field-for-field both directions. |
| `CHANGELOG.md`, `docs/architecture/core.md` | Behaviour change and record shape. |

---

### Task 1: Move `KeywordJaccard` to Core

Foundation task. It establishes the duplicate-and-pin pattern the next three tasks repeat, and it forces the namespace collision into the open on the smallest possible rule.

**Files:**
- Create: `src/Scrinia.Core/Merge/KeywordJaccard.cs`
- Modify: `src/Scrinia.Merge/KeywordJaccard.cs`
- Test: `tests/Scrinia.Merge.Tests/KeywordJaccardParityTests.cs`

**Interfaces:**
- Produces: `Scrinia.Core.Merge.KeywordJaccard.Compute(string[]? a, string[]? b) -> double`. Task 4 consumes it through `SidecarAdmission`.
- Note: `Scrinia.Merge.KeywordJaccard.Compute` keeps its exact current signature. **Both assemblies will declare the type.** Any test file referencing both needs a `using` alias, as `tests/Scrinia.Tests/MergeConfigRoundTripTests.cs:3` already does for `MergeConfig`.

- [ ] **Step 1: Create the Core original**

Create `src/Scrinia.Core/Merge/KeywordJaccard.cs`. The body is copied verbatim from `src/Scrinia.Merge/KeywordJaccard.cs` — behaviour must not change in this task.

```csharp
namespace Scrinia.Core.Merge;

/// <summary>
/// Jaccard similarity over two keyword lists — <b>the authoritative copy</b>.
///
/// <para>Used as an admission gate before two sidecars are auto-merged: below a configured
/// threshold the two are treated as different memories that happen to share a filename, and the
/// conflict is surfaced rather than resolved. See <see cref="SidecarAdmission"/>.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> <c>Scrinia.Merge</c> is a standalone AOT
/// executable with zero project references by hard constraint, so it cannot call this. It carries
/// a copy that names this type as the original, and
/// <c>Scrinia.Merge.Tests.KeywordJaccardParityTests</c> runs the same cases through both.</para>
/// </summary>
public static class KeywordJaccard
{
    /// <summary>
    /// The intersection-over-union of two keyword sets, compared case-insensitively.
    ///
    /// <para>Two empty sides are <b>identical</b> (1.0), not disjoint: a pair of sidecars that
    /// carry no keywords at all agree perfectly about what they carry, and refusing to merge them
    /// would strand every keyword-free memory. Exactly one empty side is disjoint (0.0).</para>
    /// </summary>
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
}
```

- [ ] **Step 2: Mark the AOT copy as a duplicate**

Modify `src/Scrinia.Merge/KeywordJaccard.cs`. **Do not change the code** — only add the header comment. The class body stays byte-identical to what is there now.

```csharp
namespace Scrinia.Merge;

/// <summary>
/// DUPLICATE, AND THE DUPLICATION IS FORCED. The original is
/// <c>Scrinia.Core.Merge.KeywordJaccard</c>. This project is a standalone AOT executable with zero
/// project references by hard constraint, so it cannot call it. Change one and you must change the
/// other: <c>Scrinia.Merge.Tests.KeywordJaccardParityTests</c> runs the same cases through both and
/// fails when they disagree. The rule and its reasoning live in the Core copy.
/// </summary>
public static class KeywordJaccard
{
    // ... existing body, unchanged ...
}
```

- [ ] **Step 3: Write the parity test**

Create `tests/Scrinia.Merge.Tests/KeywordJaccardParityTests.cs`. Note the aliases — without them `KeywordJaccard` is ambiguous and will not compile.

```csharp
using FluentAssertions;
using Xunit;
using CoreJaccard = Scrinia.Core.Merge.KeywordJaccard;
using DriverJaccard = Scrinia.Merge.KeywordJaccard;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The Jaccard rule exists twice and must compute once. Both assemblies declare a type named
/// KeywordJaccard, so both are aliased here — an unqualified name does not compile.
/// </summary>
public sealed class KeywordJaccardParityTests
{
    public static TheoryData<string, string[]?, string[]?> Cases() => new()
    {
        { "both null",              null,                 null },
        { "both empty",             [],                   [] },
        { "one empty",              ["auth"],             [] },
        { "one null",               null,                 ["auth"] },
        { "identical",              ["auth", "jwt"],      ["auth", "jwt"] },
        { "disjoint",               ["auth"],             ["remote"] },
        { "partial overlap",        ["auth", "jwt"],      ["auth", "remote"] },
        { "casing differs",         ["Auth", "JWT"],      ["auth", "jwt"] },
        { "duplicates within side", ["auth", "auth"],     ["auth"] },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(string caseName, string[]? a, string[]? b) =>
        DriverJaccard.Compute(a, b).Should().Be(CoreJaccard.Compute(a, b),
            because: $"[{caseName}] the AOT copy must compute exactly what the Core original does");
}
```

- [ ] **Step 4: Build and run**

```bash
rtk dotnet build Scrinia.sln
rtk dotnet test tests/Scrinia.Merge.Tests --filter "FullyQualifiedName~KeywordJaccardParity"
```

Expected: build 0 errors; 9 tests pass. If the build fails with CS0104 (ambiguous reference), a test file is missing its alias.

- [ ] **Step 5: Run the vacuity probe**

This is not optional and it is the point of the task. Temporarily change the Core original's `if (aEmpty || bEmpty) return 0.0;` to `return 0.5;`, rebuild, and re-run the filter above.

Expected: **tests FAIL.** If they pass, the parity test is not comparing the two implementations and must be fixed before you proceed. Revert the mutation and confirm green again.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/Merge/KeywordJaccard.cs src/Scrinia.Merge/KeywordJaccard.cs tests/Scrinia.Merge.Tests/KeywordJaccardParityTests.cs
rtk git commit -m "refactor(merge): KeywordJaccard moves to Core, AOT copy pinned by parity"
```

---

### Task 2: The term-frequency union becomes a shared rule

Closes A1 (the driver never merged term frequencies at all) and A4 (`reconcile()` dropped a one-sided map).

**Files:**
- Create: `src/Scrinia.Core/Merge/SidecarTermFrequencyMerge.cs`
- Modify: `src/Scrinia.Merge/MetaJsonMerger.cs`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:480-497`
- Test: `tests/Scrinia.Merge.Tests/SidecarTermFrequencyMergeParityTests.cs`, `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs`

**Interfaces:**
- Consumes: nothing from Task 1.
- Produces: `Scrinia.Core.Merge.SidecarTermFrequencyMerge.Union(IReadOnlyDictionary<string,int>? ours, IReadOnlyDictionary<string,int>? theirs) -> Dictionary<string,int>?`

- [ ] **Step 1: Create the Core original**

Create `src/Scrinia.Core/Merge/SidecarTermFrequencyMerge.cs`.

```csharp
namespace Scrinia.Core.Merge;

/// <summary>
/// The rule for merging two sidecars' term-frequency maps — <b>the authoritative copy</b>.
///
/// <para>Term frequencies are what BM25 scores against, so losing them is a silent ranking change
/// rather than a visible failure. Both <c>.meta.json</c> mergers had a hole here:
/// <c>Scrinia.Merge.MetaJsonMerger</c> never merged the field at all — it preserved OURS verbatim
/// and dropped theirs' contribution on every git merge — and <c>reconcile()</c>'s auto-resolver
/// unioned correctly but only when BOTH sides carried a map, so a one-sided map vanished.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> <c>Scrinia.Merge</c> is a standalone AOT
/// executable with zero project references by hard constraint. Its copy names this type as the
/// original, and <c>Scrinia.Merge.Tests.SidecarTermFrequencyMergeParityTests</c> runs the same
/// cases through both.</para>
/// </summary>
public static class SidecarTermFrequencyMerge
{
    /// <summary>
    /// Unions two term-frequency maps: every key from either side, and the LARGER count on a key
    /// both carry.
    ///
    /// <para><b>Max, not sum.</b> A count is the term's frequency in one memory's content, not a
    /// running total. Two branches that each appended the same chunk hold overlapping counts, and
    /// summing them would inflate a term on every merge, compounding — the same class of bug as
    /// re-boosting a keyword on every append. Max is the smallest answer that loses nothing.</para>
    ///
    /// <para><b>A null side contributes nothing; it does not veto.</b> Null means "this side
    /// recorded no term frequencies", so the other side's map survives whole. Treating null as a
    /// reason to drop everything is exactly the defect this rule replaces.</para>
    /// </summary>
    /// <returns>The merged map, or null when neither side carried one.</returns>
    public static Dictionary<string, int>? Union(
        IReadOnlyDictionary<string, int>? ours,
        IReadOnlyDictionary<string, int>? theirs)
    {
        if (ours is null && theirs is null) return null;

        // OrdinalIgnoreCase, matching the map every producer builds. TextAnalysis
        // .ComputeTermFrequencies uses OrdinalIgnoreCase, and Tokenize lowercases every token, so
        // no producer can emit two keys differing only in case — for real data the comparer is
        // unobservable.
        //
        // It is load-bearing for a hand-edited or foreign sidecar. Merging under Ordinal would let
        // "Auth" and "auth" both survive into the merged map, and
        // MemoryDerivation.ForMetadataUpdate copies that map into an OrdinalIgnoreCase dictionary
        // — which THROWS ArgumentException on exactly that pair. Folding here is what stops a
        // malformed sidecar becoming a crash three calls later.
        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ours is not null)
            foreach (var (term, count) in ours)
                merged[term] = count;
        if (theirs is not null)
            foreach (var (term, count) in theirs)
                merged[term] = merged.TryGetValue(term, out int existing) ? Math.Max(existing, count) : count;

        return merged;
    }
}
```

- [ ] **Step 2: Write the parity test**

Create `tests/Scrinia.Merge.Tests/SidecarTermFrequencyMergeParityTests.cs`. It follows `SidecarKeywordMergeParityTests` exactly: drive the **full merge driver** over sidecars identical but for the field under test, then compare against the Core rule.

```csharp
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The term-frequency merge rule exists twice and must behave once.
///
/// <para>Each case drives the FULL merge driver over sidecars that are identical except for the
/// termFrequencies maps, so the driver's other rules are no-ops and what is compared is exactly
/// the shared rule. The Jaccard threshold is dropped to zero so every case reaches it.</para>
/// </summary>
public sealed class SidecarTermFrequencyMergeParityTests
{
    public static TheoryData<string, Dictionary<string, int>?, Dictionary<string, int>?> Cases() => new()
    {
        { "both null",            null,                                  null },
        { "ours only",            new() { ["auth"] = 3 },                null },
        { "theirs only",          null,                                  new() { ["auth"] = 3 } },
        { "disjoint keys",        new() { ["auth"] = 3 },                new() { ["jwt"] = 5 } },
        { "shared key, ours bigger",   new() { ["auth"] = 7 },           new() { ["auth"] = 2 } },
        { "shared key, theirs bigger", new() { ["auth"] = 2 },           new() { ["auth"] = 7 } },
        { "shared key, equal",    new() { ["auth"] = 4 },                new() { ["auth"] = 4 } },
        { "mixed",                new() { ["auth"] = 3, ["jwt"] = 1 },   new() { ["auth"] = 1, ["oidc"] = 9 } },
        { "empty maps",           new(),                                 new() },
        { "zero counts",          new() { ["auth"] = 0 },                new() { ["auth"] = 0 } },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(
        string caseName, Dictionary<string, int>? ours, Dictionary<string, int>? theirs)
    {
        var expected = SidecarTermFrequencyMerge.Union(ours, theirs);
        var actual = ThroughTheMergeDriver(ours, theirs);

        actual.Should().BeEquivalentTo(expected,
            because: $"[{caseName}] the copy in MetaJsonMerger must union term frequencies exactly " +
                     "as SidecarTermFrequencyMerge does — this field is what BM25 scores against, " +
                     "so a divergence is a ranking divergence nobody sees until two machines disagree");
    }

    private static Dictionary<string, int>? ThroughTheMergeDriver(
        Dictionary<string, int>? ours, Dictionary<string, int>? theirs)
    {
        string dir = Path.Combine(Path.GetTempPath(), "tf-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string oursPath = Path.Combine(dir, "ours.meta.json");
            string theirsPath = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null));
            File.WriteAllText(oursPath, Sidecar(ours));
            File.WriteAllText(theirsPath, Sidecar(theirs));

            MetaJsonMerger.Merge(ancestor, oursPath, theirsPath, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Resolved,
                    because: "the Jaccard gate is disabled for these cases, so every one of them " +
                             "must reach the rule under test");

            using var doc = JsonDocument.Parse(File.ReadAllText(oursPath));
            if (!doc.RootElement.TryGetProperty("termFrequencies", out var tf)
                || tf.ValueKind != JsonValueKind.Object)
                return null;
            return tf.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetInt32(), StringComparer.Ordinal);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Sidecar(Dictionary<string, int>? termFrequencies)
    {
        var fields = new List<string>
        {
            "\"name\":\"m\"", "\"uri\":\"\"", "\"originalBytes\":100", "\"chunkCount\":1",
            "\"keywords\":[\"auth\"]",
            "\"createdAt\":\"2026-01-01T00:00:00Z\"", "\"updatedAt\":\"2026-01-02T00:00:00Z\"",
        };
        if (termFrequencies is not null)
            fields.Add($"\"termFrequencies\":{JsonSerializer.Serialize(termFrequencies)}");
        return "{" + string.Join(",", fields) + "}";
    }
}
```

- [ ] **Step 2a: Run it and watch it fail**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Merge.Tests --filter "FullyQualifiedName~SidecarTermFrequencyMergeParity"
```

Expected: **FAIL** on the cases where theirs carries anything ours does not — the driver has no term-frequency merge yet, so it returns ours unchanged. That failure is the defect, reproduced.

- [ ] **Step 3: Add the forced duplicate and call it from the driver**

In `src/Scrinia.Merge/MetaJsonMerger.cs`, add a private static method carrying the duplicate rule, with the standard header naming its original. Then call it and write the result.

Replace the overlay comment at `:67-68`, which currently lists `termFrequencies` among the fields preserved verbatim — **that statement stops being true in this step and must not be left behind.**

```csharp
        // Overlay the merged fields onto OURS' full object — preserves every unknown property
        // (contentPreview, chunkEntries, codeRefs, attributes, …) verbatim. termFrequencies is NOT
        // among them: it is merged below, because preserving it verbatim silently dropped theirs'
        // contribution on every merge.
```

and after the `agentKeywords` overlay block:

```csharp
        var mergedTf = UnionTermFrequencies(GetIntMap(ours, "termFrequencies"),
                                            GetIntMap(theirs, "termFrequencies"));
        if (mergedTf is null)
            ours.Remove("termFrequencies");
        else
            ours["termFrequencies"] = new JsonObject(
                mergedTf.OrderBy(kv => kv.Key, StringComparer.Ordinal)
                        .Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value))));
```

with these two helpers:

```csharp
    private static Dictionary<string, int>? GetIntMap(JsonObject? o, string key)
    {
        if (o is null || !o.TryGetPropertyValue(key, out var n) || n is not JsonObject obj) return null;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (name, value) in obj)
            if (value is JsonValue v && v.TryGetValue<int>(out int i)) map[name] = i;
        return map;
    }

    // DUPLICATE, AND THE DUPLICATION IS FORCED. The original is
    // Scrinia.Core.Merge.SidecarTermFrequencyMerge.Union, which reconcile()'s auto-resolver calls;
    // this project is a standalone AOT executable with zero project references by hard constraint,
    // so it cannot call it. Change one and you must change the other:
    // Scrinia.Merge.Tests.SidecarTermFrequencyMergeParityTests runs the same cases through both and
    // fails when they disagree. The rule and its reasoning live there. In summary: union the keys
    // case-insensitively, take the MAX on a shared key (a count is a frequency, not a running
    // total — summing would inflate a term on every merge), and a null side contributes nothing
    // rather than vetoing.
    private static Dictionary<string, int>? UnionTermFrequencies(
        Dictionary<string, int>? ours, Dictionary<string, int>? theirs)
    {
        if (ours is null && theirs is null) return null;

        var merged = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ours is not null)
            foreach (var (term, count) in ours)
                merged[term] = count;
        if (theirs is not null)
            foreach (var (term, count) in theirs)
                merged[term] = merged.TryGetValue(term, out int existing) ? Math.Max(existing, count) : count;

        return merged;
    }
```

- [ ] **Step 4: Verify the parity test now passes**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Merge.Tests --filter "FullyQualifiedName~SidecarTermFrequencyMergeParity"
```

Expected: build 0 errors; 10 tests pass.

- [ ] **Step 5: Run the vacuity probe**

Temporarily change the **Core** original's `Math.Max(existing, count)` to `existing`. Rebuild and re-run.

Expected: **FAIL** on `shared key, theirs bigger`. If every case still passes, the parity test is not reaching the rule. Revert and confirm green.

- [ ] **Step 6: Fix `reconcile()`'s one-sided hole**

In `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, replace the block at `:480-497` — the `if (baseNode["termFrequencies"] is JsonObject baseTf && otherNode["termFrequencies"] is JsonObject otherTf)` guard is exactly the bug, because it requires both sides.

```csharp
            // Union the term frequencies through the ONE rule
            // (Scrinia.Core.Merge.SidecarTermFrequencyMerge). This used to require BOTH sides to
            // carry a map, so a memory whose base recorded none and whose other side did lost them
            // whole — a silent ranking change, since term frequencies are what BM25 scores against.
            var mergedTf = SidecarTermFrequencyMerge.Union(
                ReadIntMap(baseNode, "termFrequencies"), ReadIntMap(otherNode, "termFrequencies"));
            if (mergedTf is null)
                (baseNode as JsonObject)?.Remove("termFrequencies");
            else
                baseNode["termFrequencies"] = ToJsonObject(mergedTf);
```

with these helpers beside `ReadStrings`:

```csharp
    /// <summary>A JSON object of string→int as a dictionary, or null when the key is absent or is
    /// not an object. Null means the side recorded no term frequencies.</summary>
    private static Dictionary<string, int>? ReadIntMap(JsonNode? node, string key)
    {
        if (node?[key] is not JsonObject obj) return null;
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        // TryGetValue, not GetValue<int>() — the latter throws on a non-integer value, and a
        // hand-edited sidecar is exactly the input this method has to survive.
        foreach (var (name, value) in obj)
            if (value is JsonValue v && v.TryGetValue<int>(out int i)) map[name] = i;
        return map;
    }

    private static JsonObject ToJsonObject(Dictionary<string, int> map)
    {
        var obj = new JsonObject();
        foreach (var (term, count) in map.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            obj[term] = count;
        return obj;
    }
```

Add `using Scrinia.Core.Merge;` if it is not already present.

- [ ] **Step 7: Prove it end to end through a real git merge**

Add to `tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs`. Use `[Fact]` — **not `[SkippableFact]`**: that project has no `Xunit.SkippableFact` reference and every test in the file uses `[Fact]`.

The file's setup goes through `GitMergeHarness` (`RegisterDriver`, `WriteFile`, `SeedThreeWay`, `Merge`, `ReadFile`), and its file-private `Meta(string[] keywords, string updatedAt)` helper at `:66-71` emits no `termFrequencies`. Extend it with an optional parameter rather than adding a second helper:

```csharp
    private static string Meta(string[] keywords, string updatedAt,
        Dictionary<string, int>? termFrequencies = null)
    {
        string kw = string.Join(",", keywords.Select(k => $"\"{k}\""));
        string tf = termFrequencies is null
            ? ""
            : ",\"termFrequencies\":{" +
              string.Join(",", termFrequencies.Select(p => $"\"{p.Key}\":{p.Value}")) + "}";
        return $"{{\"name\":\"auth-flow\",\"keywords\":[{kw}],\"originalBytes\":10," +
               $"\"chunkCount\":1,\"updatedAt\":\"{updatedAt}\"{tf}}}\n";
    }
```

Then the test, modelled on `MetaDriver_WithSimilarKeywords_AutoResolves` at `:45-63`:

```csharp
    [Fact]
    public void TermFrequenciesFromBothSidesSurviveAMerge()
    {
        // The defect this closes: the driver preserved OURS' termFrequencies verbatim and dropped
        // theirs, so a branch that appended content lost its ranking contribution on merge with no
        // conflict raised. Asserted through a real `git merge` rather than against the merger
        // directly, because that is the level a user meets it at.
        using var repo = new GitMergeHarness();
        repo.RegisterDriver("meta");
        repo.WriteFile(".scrinia/.gitattributes", NmpAttributes);
        repo.SeedThreeWay(
            Sidecar,
            baseContent:   Meta(["a", "b", "c", "d"], "2026-01-01T00:00:00+00:00"),
            oursContent:   Meta(["a", "b", "c", "d"], "2026-02-01T00:00:00+00:00",
                                new() { ["auth"] = 5 }),
            theirsContent: Meta(["a", "b", "c", "d"], "2026-03-01T00:00:00+00:00",
                                new() { ["jwt"] = 3 }));

        repo.Merge("other").ExitCode.Should().Be(0);

        // The neighbouring tests assert on the raw merged text rather than a parsed object; parse
        // here because the assertion is about a numeric value, not the presence of a token.
        using var doc = JsonDocument.Parse(repo.ReadFile(Sidecar));
        var tf = doc.RootElement.GetProperty("termFrequencies");
        tf.GetProperty("auth").GetInt32().Should().Be(5);
        tf.GetProperty("jwt").GetInt32().Should().Be(3,
            because: "theirs' term frequencies must survive — dropping them was the defect");
    }
```

Keywords are identical on both sides, so the Jaccard gate scores 1.0 and admits the merge regardless of the configured threshold. Add `using System.Text.Json;` if the file lacks it.

- [ ] **Step 8: Full build and suite**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test
```

Expected: 0 errors; **2040 + 11 = 2051** passed, 0 failed, 4 skipped. If the count is lower than 2051, a test file failed to compile and you are reading stale assemblies — check the build output for errors.

- [ ] **Step 9: Commit**

```bash
rtk git add src/Scrinia.Core/Merge/SidecarTermFrequencyMerge.cs src/Scrinia.Merge/MetaJsonMerger.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Merge.Tests/SidecarTermFrequencyMergeParityTests.cs tests/Scrinia.Merge.Tests/GitMergeIntegrationTests.cs
rtk git commit -m "fix(merge): term frequencies merge in both mergers, through one rule"
```

---

### Task 3: The timestamp rule becomes shared

Closes A3. The two implementations already differ, and `reconcile()` can move a memory's `createdAt` forward.

**Files:**
- Create: `src/Scrinia.Core/Merge/SidecarTimestampMerge.cs`
- Modify: `src/Scrinia.Merge/MetaJsonMerger.cs:62-65,131-137`, `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:447-461`
- Test: `tests/Scrinia.Merge.Tests/SidecarTimestampMergeParityTests.cs`, `tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs`

**Interfaces:**
- Produces: `Scrinia.Core.Merge.SidecarTimestampMerge.Select(string? oursCreated, string? oursUpdated, string? theirsCreated, string? theirsUpdated) -> (string? CreatedAt, string? UpdatedAt)`

- [ ] **Step 1: Create the Core original**

Create `src/Scrinia.Core/Merge/SidecarTimestampMerge.cs`.

```csharp
using System.Globalization;

namespace Scrinia.Core.Merge;

/// <summary>
/// The rule for picking two sidecars' timestamps — <b>the authoritative copy</b>.
///
/// <para>The two <c>.meta.json</c> mergers had already diverged here.
/// <c>Scrinia.Merge.MetaJsonMerger</c> compared ISO strings with <c>StringComparison.Ordinal</c>
/// and kept the earliest <c>createdAt</c>; <c>reconcile()</c>'s auto-resolver parsed
/// <c>DateTimeOffset</c>, selected one whole node by <c>updatedAt</c>, and never considered
/// <c>createdAt</c> at all — so an automatic reconcile could move a memory's creation time
/// FORWARD, losing when it was actually created.</para>
///
/// <para>Ordinal comparison is independently wrong across offsets:
/// <c>2026-08-13T10:00:00+10:00</c> and <c>2026-08-13T00:00:00Z</c> are the same instant and do
/// not compare equal as strings.</para>
///
/// <para><b>One duplicate survives, and it is forced.</b> See
/// <c>Scrinia.Merge.Tests.SidecarTimestampMergeParityTests</c>.</para>
/// </summary>
public static class SidecarTimestampMerge
{
    /// <summary>
    /// Picks the merged pair: the LATEST <c>updatedAt</c> and the EARLIEST <c>createdAt</c>.
    ///
    /// <para>The asymmetry is deliberate and is the whole rule. A memory was created once, at the
    /// earlier of the two recorded moments; taking the later one would claim it did not exist
    /// during a period it demonstrably did. It was last updated at the later of the two, because
    /// both updates really happened.</para>
    ///
    /// <para><b>Unparseable values are treated as absent for comparison, and the parseable side
    /// wins.</b> When neither parses, OURS is returned unchanged — an unrecognized timestamp is
    /// never invented, reformatted, or destroyed. The returned strings are always one of the four
    /// inputs verbatim, so a merge cannot silently renormalize a format.</para>
    /// </summary>
    public static (string? CreatedAt, string? UpdatedAt) Select(
        string? oursCreated, string? oursUpdated, string? theirsCreated, string? theirsUpdated) =>
        (Pick(oursCreated, theirsCreated, earliest: true),
         Pick(oursUpdated, theirsUpdated, earliest: false));

    private static string? Pick(string? ours, string? theirs, bool earliest)
    {
        bool oursOk = TryParse(ours, out var o);
        bool theirsOk = TryParse(theirs, out var t);

        if (!oursOk && !theirsOk) return ours;
        if (!oursOk) return theirs;
        if (!theirsOk) return ours;

        return (earliest ? o <= t : o >= t) ? ours : theirs;
    }

    private static bool TryParse(string? value, out DateTimeOffset parsed)
    {
        parsed = default;
        return !string.IsNullOrWhiteSpace(value)
            && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);
    }
}
```

- [ ] **Step 2: Write the parity test**

Create `tests/Scrinia.Merge.Tests/SidecarTimestampMergeParityTests.cs`, same shape as Task 2's: drive the full driver, compare against the Core rule.

```csharp
using System.Text.Json;
using FluentAssertions;
using Scrinia.Core.Merge;
using Xunit;

namespace Scrinia.Merge.Tests;

/// <summary>
/// The timestamp rule exists twice and must pick once. These cases include the two the old
/// implementations disagreed about: equal instants written in different offsets, and a createdAt
/// that differs between sides.
/// </summary>
public sealed class SidecarTimestampMergeParityTests
{
    public static TheoryData<string, string?, string?, string?, string?> Cases() => new()
    {
        // name                      oursCreated               oursUpdated               theirsCreated             theirsUpdated
        { "ours older, newer update", "2026-01-01T00:00:00Z",  "2026-03-01T00:00:00Z",   "2026-02-01T00:00:00Z",   "2026-02-05T00:00:00Z" },
        { "theirs older",             "2026-02-01T00:00:00Z",  "2026-02-05T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-03-01T00:00:00Z" },
        { "identical",                "2026-01-01T00:00:00Z",  "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-02-01T00:00:00Z" },
        { "same instant, different offset", "2026-01-01T10:00:00+10:00", "2026-02-01T00:00:00Z", "2026-01-01T00:00:00Z", "2026-02-01T00:00:00Z" },
        { "ours missing created",     null,                    "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-02-01T00:00:00Z" },
        { "theirs missing updated",   "2026-01-01T00:00:00Z",  "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   null },
        { "both missing updated",     "2026-01-01T00:00:00Z",  null,                     "2026-01-01T00:00:00Z",   null },
        { "unparseable ours",         "not-a-date",            "2026-02-01T00:00:00Z",   "2026-01-01T00:00:00Z",   "2026-02-01T00:00:00Z" },
        { "unparseable both",         "not-a-date",            "also-not",               "still-not",              "nope" },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void BothImplementationsAgree(
        string caseName, string? oursCreated, string? oursUpdated,
        string? theirsCreated, string? theirsUpdated)
    {
        var expected = SidecarTimestampMerge.Select(oursCreated, oursUpdated, theirsCreated, theirsUpdated);
        var actual = ThroughTheMergeDriver(oursCreated, oursUpdated, theirsCreated, theirsUpdated);

        actual.CreatedAt.Should().Be(expected.CreatedAt,
            because: $"[{caseName}] createdAt must be the EARLIEST of the two — a merge that moves " +
                     "it forward claims the memory did not exist during a period it did");
        actual.UpdatedAt.Should().Be(expected.UpdatedAt,
            because: $"[{caseName}] updatedAt must be the latest of the two");
    }

    private static (string? CreatedAt, string? UpdatedAt) ThroughTheMergeDriver(
        string? oursCreated, string? oursUpdated, string? theirsCreated, string? theirsUpdated)
    {
        string dir = Path.Combine(Path.GetTempPath(), "ts-parity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string ancestor = Path.Combine(dir, "a.meta.json");
            string ours = Path.Combine(dir, "ours.meta.json");
            string theirs = Path.Combine(dir, "theirs.meta.json");

            File.WriteAllText(ancestor, Sidecar(null, null));
            File.WriteAllText(ours, Sidecar(oursCreated, oursUpdated));
            File.WriteAllText(theirs, Sidecar(theirsCreated, theirsUpdated));

            MetaJsonMerger.Merge(ancestor, ours, theirs, new MergeConfig(JaccardThreshold: 0.0))
                .Should().Be(MetaJsonMerger.MergeResult.Resolved);

            using var doc = JsonDocument.Parse(File.ReadAllText(ours));
            return (ReadString(doc.RootElement, "createdAt"), ReadString(doc.RootElement, "updatedAt"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string Sidecar(string? createdAt, string? updatedAt)
    {
        var fields = new List<string>
        {
            "\"name\":\"m\"", "\"uri\":\"\"", "\"originalBytes\":100", "\"chunkCount\":1",
            "\"keywords\":[\"auth\"]",
        };
        if (createdAt is not null) fields.Add($"\"createdAt\":{JsonSerializer.Serialize(createdAt)}");
        if (updatedAt is not null) fields.Add($"\"updatedAt\":{JsonSerializer.Serialize(updatedAt)}");
        return "{" + string.Join(",", fields) + "}";
    }

    private static string? ReadString(JsonElement root, string key) =>
        root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
```

- [ ] **Step 3: Run it and watch it fail**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Merge.Tests --filter "FullyQualifiedName~SidecarTimestampMergeParity"
```

Expected: **FAIL** on `same instant, different offset` — the driver's ordinal comparison gets it wrong. Other cases may pass; that is fine and expected.

- [ ] **Step 4: Replace the driver's implementation with the forced duplicate**

In `src/Scrinia.Merge/MetaJsonMerger.cs`, replace lines `62-65` with a call to a new private `SelectTimestamps`, and **delete `CompareTimestamps` (`:131-137`) entirely** — leaving it would leave a second, wrong comparator for someone to call.

```csharp
        var (createdAt, updatedAt) = SelectTimestamps(
            GetString(ours, "createdAt"), GetString(ours, "updatedAt"),
            GetString(theirs, "createdAt"), GetString(theirs, "updatedAt"));
```

Add the duplicate, carrying the standard header naming `Scrinia.Core.Merge.SidecarTimestampMerge.Select` as the original and summarizing the rule (latest `updatedAt`, earliest `createdAt`, parsed as instants, unparseable treated as absent with ours preserved when neither parses). Its body mirrors the Core original, using `System.Globalization.CultureInfo.InvariantCulture` and the same `DateTimeStyles`.

- [ ] **Step 5: Replace `reconcile()`'s implementation**

In `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, the base-node selection at `:447-461` stays — it decides which node's *unknown properties* survive, which is a separate question. But the two timestamps must be written from the shared rule afterwards, because today they ride along on whichever node won:

```csharp
            // The timestamps come from the ONE rule, not from whichever node won the base
            // selection above. Riding along meant createdAt took the winner's value — so an
            // automatic reconcile could move a memory's creation time FORWARD.
            var (mergedCreated, mergedUpdated) = SidecarTimestampMerge.Select(
                oursNode["createdAt"]?.GetValue<string>(), oursUpdated,
                theirsNode["createdAt"]?.GetValue<string>(), theirsUpdated);
            if (mergedCreated is null)
                (baseNode as JsonObject)?.Remove("createdAt");
            else
                baseNode["createdAt"] = mergedCreated;
            if (mergedUpdated is null)
                (baseNode as JsonObject)?.Remove("updatedAt");
            else
                baseNode["updatedAt"] = mergedUpdated;
```

- [ ] **Step 6: Pin the `createdAt` behaviour in `reconcile()`'s own tests**

Add to **`tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs`**, not `ReconcileScanTests.cs` — that file already owns the conflicted-sidecar fixtures and the accessors these tests need.

**Assertion style — read this before writing.** There is no `BeSuccess`/`BeError` helper anywhere in the repo, and `MemoryReconcile` returns a **YAML string**, not an `McpResponse`. The established patterns are `ResponseParser.Parse(result).Status.Should().Be("success")` for the response, and this file's own `ResolvedAsync(scope, conflictId)` + `Array(resolved, "keywords")` for reading the resolved sidecar back. Read scalars with `resolved.GetProperty("createdAt").GetString()`. Match those; do not invent helpers.

```csharp
    [Fact]
    public async Task AutoResolve_KeepsTheEarliestCreatedAt()
    {
        // The defect: reconcile() picked one node whole by updatedAt and never looked at
        // createdAt, so resolving in favour of the newer side moved the memory's creation time
        // forward. The merge driver had always kept the earliest; the two disagreed.
        // ours: createdAt 2026-01-01, updatedAt 2026-01-02
        // theirs: createdAt 2026-02-01, updatedAt 2026-03-01   <- wins updatedAt, must NOT win createdAt
        // Arrange a conflicted .meta.json with those values — copy the Ours/Theirs const strings
        // at :52-58 and change the timestamps — then resolve with no explicit choice.
        var resolved = await ResolvedAsync(scope, conflictId);

        resolved.GetProperty("createdAt").GetString().Should().Be("2026-01-01T00:00:00Z",
            because: "createdAt is the earliest of the two — the memory was created once, and a " +
                     "merge that moves it forward claims it did not exist during a period it did");
        resolved.GetProperty("updatedAt").GetString().Should().Be("2026-03-01T00:00:00Z");
    }

    [Fact]
    public async Task AutoResolve_KeepsAOneSidedTermFrequencyMap()
    {
        // The defect: the union required BOTH sides to carry a map, so a side that recorded term
        // frequencies against a side that did not lost them entirely.
        // Arrange ours with no termFrequencies and theirs with {"auth":4}, keywords equal on both
        // sides so the admission gate Task 4 adds cannot refuse this case.
        var resolved = await ResolvedAsync(scope, conflictId);

        resolved.GetProperty("termFrequencies").GetProperty("auth").GetInt32().Should().Be(4);
    }
```

- [ ] **Step 7: Vacuity probe, then full suite**

Temporarily flip the **Core** rule's `earliest: true` to `earliest: false` for `createdAt`. Rebuild, re-run `SidecarTimestampMergeParity`.

Expected: **FAIL**. Revert, then:

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test
```

Expected: 0 errors; **2051 + 11 = 2062** passed, 0 failed, 4 skipped.

- [ ] **Step 8: Commit**

```bash
rtk git add src/Scrinia.Core/Merge/SidecarTimestampMerge.cs src/Scrinia.Merge/MetaJsonMerger.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Merge.Tests/SidecarTimestampMergeParityTests.cs tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs
rtk git commit -m "fix(merge): one timestamp rule; reconcile stops moving createdAt forward"
```

---

### Task 4: `reconcile()` gains the admission gate

Closes A2. **This is the plan's one user-visible behaviour change** — some conflicts that auto-resolve today will require manual resolution.

**Files:**
- Create: `src/Scrinia.Core/Merge/SidecarAdmission.cs`
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` (`TryComputeAutoResolvedMetaJson`, both call sites at `:125` and `:236`, and a new threshold reader beside `ReadConflictDirName` at `:396-404`)
- Test: `tests/Scrinia.Tests/ReconcileScanTests.cs`, **`tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs`** (five existing test results regress without Step 0 — see below)

**Read Step 0 first. This task breaks five passing tests unless you do.**

**Interfaces:**
- Consumes: `Scrinia.Core.Merge.KeywordJaccard.Compute` (Task 1).
- Produces: `Scrinia.Core.Merge.SidecarAdmission.IsSimilarEnough(string[]? oursKeywords, string[]? theirsKeywords, double threshold) -> bool`
- Changes: `TryComputeAutoResolvedMetaJson(string conflictedContent, double jaccardThreshold, out string? resolvedJson)` — the threshold is a **new second parameter, before the `out`**. Both call sites must pass it.

- [ ] **Step 1: Create the Core rule**

Create `src/Scrinia.Core/Merge/SidecarAdmission.cs`.

```csharp
namespace Scrinia.Core.Merge;

/// <summary>
/// Whether two conflicted sidecars are similar enough to merge automatically — <b>the
/// authoritative copy</b>.
///
/// <para>Two sidecars can collide on a filename while describing genuinely different memories.
/// Merging those produces one entry claiming both sets of keywords and both term-frequency maps,
/// which is worse than a conflict: a conflict asks a human, and a bad automatic merge does not.
/// The git merge driver has refused below a threshold since it was written;
/// <c>reconcile()</c>'s auto-resolver did not, and merged on no evidence of similarity at
/// all.</para>
/// </summary>
public static class SidecarAdmission
{
    /// <summary>
    /// True when the two keyword sets are at least <paramref name="threshold"/> similar by
    /// Jaccard, and the merge may proceed.
    ///
    /// <para>A threshold of 0 admits everything, which is how the parity tests reach the rules
    /// behind this gate. Two keyword-free sidecars score 1.0 and are admitted — see
    /// <see cref="KeywordJaccard.Compute"/> for why that is the right answer rather than a
    /// degenerate one.</para>
    /// </summary>
    public static bool IsSimilarEnough(string[]? oursKeywords, string[]? theirsKeywords, double threshold) =>
        KeywordJaccard.Compute(oursKeywords, theirsKeywords) >= threshold;
}
```

- [ ] **Step 0: Admit the existing fixtures before the gate refuses them**

Every conflicted-sidecar fixture in the suite sits at **Jaccard 0.333** — `ReconcileMetaProvenanceTests.cs:52-58` pairs `["auth","local"]` with `["auth","remote"]` (one shared term over three distinct), and `:111-116` pairs `["auth","portcullis"]` with `["auth","gatehouse"]`. All are below the 0.7 default, so adding the gate makes **five currently-passing test results fail**: `ReconcileScanTests.Reconcile_WithConflictIdOnly_AppliesAutoMergeForMetaJson` and all four in `ReconcileMetaProvenanceTests`.

Those tests are about provenance merging and auto-merge mechanics, not admission — their keyword dissimilarity is incidental. **Disable the gate in their fixtures rather than rewriting their keywords**, which would change the exact unions their assertions encode. There is direct precedent: `SidecarKeywordMergeParityTests.cs:82` passes `new MergeConfig(JaccardThreshold: 0.0)` for exactly this reason, and says so.

In each affected fixture, write a `merge.config` into the fixture's `.scrinia` directory before reconciling:

```csharp
        // This test is about what the merge RULES produce, not about whether the two sides are
        // similar enough to merge at all — its keyword sets are deliberately disjoint to make the
        // provenance question sharp. Drop the admission threshold so every case reaches the rule
        // under test, exactly as SidecarKeywordMergeParityTests does on the driver side.
        File.WriteAllText(Path.Combine(scriniaDir, "merge.config"), "{\"jaccardThreshold\":0.0}");
```

Raising the fixtures' similarity instead is the wrong fix: it would make `Array(resolved, "keywords").Should().Equal(["auth", "local", "remote"])` and its siblings assert over different data, silently weakening tests that encode hard-won provenance rulings.

**Do this before Step 4 lands the gate**, so the suite never goes red for a reason the plan already knows about.

- [ ] **Step 2: Write the failing test**

Add to `tests/Scrinia.Tests/ReconcileScanTests.cs`.

**Assertion style.** `BeSuccess`/`BeError` do not exist anywhere in this repo, and `MemoryReconcile` returns a **YAML string**, not an `McpResponse`. The file's real pattern is `ResponseParser.Parse(result).Status.Should().Be("success")`. Match it.

```csharp
    [Fact]
    public async Task AutoResolve_RefusesTwoDissimilarSidecars()
    {
        // The defect: reconcile() had no admission gate, so it auto-merged two sidecars with
        // nothing in common and reported a clean resolve. The git merge driver has always refused
        // below the configured threshold; this closes the gap between them.
        // ... arrange a conflicted .meta.json where ours has keywords ["auth","jwt"] and theirs
        //     has ["kubernetes","helm"] — Jaccard 0.0, below the 0.7 default ...

        string result = await tools.MemoryReconcile(conflictId: relativePath);

        ResponseParser.Parse(result).Status.Should().Be("error",
            because: "below the threshold the two are different memories sharing a filename, so " +
                     "the conflict must reach a human rather than be merged on no evidence");
        File.ReadAllText(absolutePath).Should().Contain("<<<<<<<",
            because: "a refused auto-merge must leave the conflicted file untouched");
    }

    [Fact]
    public async Task AutoResolve_AdmitsTwoSimilarSidecars()
    {
        // The gate must not refuse the ordinary case. ours ["auth","jwt","oidc"], theirs
        // ["auth","jwt","saml"] — Jaccard 0.5 — with a merge.config setting jaccardThreshold to
        // 0.4, which also proves the threshold is read from config rather than hard-coded.

        string result = await tools.MemoryReconcile(conflictId: relativePath);

        ResponseParser.Parse(result).Status.Should().Be("success");
        File.ReadAllText(absolutePath).Should().NotContain("<<<<<<<");
    }
```

Read `ReconcileScanTests.cs`'s existing fixtures for how it builds a conflicted file and invokes the tool, and use the same names — the call above shows the shape, not necessarily the exact method name.

- [ ] **Step 3: Add the threshold reader**

In `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs`, beside `ReadConflictDirName`, add a reader using the same shape — same file, same broad catch, same default-on-failure policy as `Scrinia.Merge.MergeConfig.Load`:

```csharp
    /// <summary>
    /// The Jaccard admission threshold from <c>merge.config</c>, or 0.7.
    ///
    /// <para>The same file and the same default the git merge driver reads
    /// (<c>Scrinia.Merge.MergeConfig.Load</c>), so one workspace setting governs both routes to a
    /// sidecar merge. Read here rather than through <c>MergeConfig</c> because that type lives in
    /// the AOT driver assembly, which this project does not reference.</para>
    /// </summary>
    private static double GetJaccardThreshold(string scriniaDir)
    {
        string configPath = Path.Combine(scriniaDir, "merge.config");
        if (!File.Exists(configPath)) return 0.7;
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(configPath));
            return node?["jaccardThreshold"]?.GetValue<double>() ?? 0.7;
        }
        catch { return 0.7; }
    }
```

- [ ] **Step 4: Add the gate and thread the threshold**

In `TryComputeAutoResolvedMetaJson`, add the `double jaccardThreshold` parameter before the `out`, and place the gate immediately after both nodes parse and before any merging — refusing early keeps a refused merge free of side effects:

```csharp
            // Admission gate, matching the git merge driver's. Two sidecars can collide on a
            // filename while describing different memories; merging those is worse than a
            // conflict, because a conflict asks a human. Placed before any merge work so a
            // refusal is total.
            if (!SidecarAdmission.IsSimilarEnough(
                    ReadStrings(oursNode, "keywords"), ReadStrings(theirsNode, "keywords"),
                    jaccardThreshold))
                return false;
```

Update both call sites. `scriniaDir` is in scope at each: `ResolveConflictByPath` receives it as its first parameter (`:88`), and `ScanForConflicts` uses it at `:228`.

```csharp
// :125
if (type == "meta.json"
    && TryComputeAutoResolvedMetaJson(fileContent, GetJaccardThreshold(scriniaDir), out string? autoResolvedJson))

// :236
if (TryComputeAutoResolvedMetaJson(fileContent, GetJaccardThreshold(scriniaDir), out _))
```

- [ ] **Step 5: Make the scan's refusal message honest**

At `:239` the scan currently reports every non-auto-resolvable `.meta.json` as `"(.meta.json — auto-resolve failed)"`. A gate refusal is not a failure — it is a deliberate decision — and reporting it as one sends the reader looking for a parse bug. Distinguish the two:

```csharp
                if (TryComputeAutoResolvedMetaJson(fileContent, threshold, out _))
                    autoResolved.Add(relativePath);
                else if (!MetaSidecarsAreSimilarEnough(fileContent, threshold))
                    needsManual.Add($"{relativePath} (.meta.json — sides too dissimilar to merge automatically)");
                else
                    needsManual.Add($"{relativePath} (.meta.json — auto-resolve failed)");
```

Add `MetaSidecarsAreSimilarEnough(string conflictedContent, double threshold)` as a small private helper that extracts the two sides the same way `TryComputeAutoResolvedMetaJson` does and returns `SidecarAdmission.IsSimilarEnough(...)`, returning `true` when the content cannot be parsed — an unparseable file is a genuine auto-resolve failure, not a dissimilarity refusal.

- [ ] **Step 6: Build, test, vacuity probe**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test
```

Expected: 0 errors; **2062 + 2 = 2064** passed, **0 failed**, 4 skipped. The five fixtures Step 0 admitted must still be passing — if any of them fails, Step 0 was applied to the wrong fixture or missed one.

Then confirm the gate is load-bearing: temporarily change `IsSimilarEnough` to `=> true`, rebuild, re-run `ReconcileScanTests`. Expected: `AutoResolve_RefusesTwoDissimilarSidecars` **FAILS**. Revert and confirm green.

- [ ] **Step 7: Commit**

```bash
rtk git add src/Scrinia.Core/Merge/SidecarAdmission.cs src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Tests/ReconcileScanTests.cs tests/Scrinia.Tests/ReconcileMetaProvenanceTests.cs
rtk git commit -m "fix(reconcile): refuse to auto-merge sidecars below the similarity threshold"
```

---

### Task 5: `ForAppend` hashes newly-referenced files

Closes B1.

**Files:**
- Modify: `src/Scrinia.Core/MemoryDerivation.cs:177-182,250`, `src/Scrinia.Core/FileMemoryStore.cs:1318-1319`
- Test: `tests/Scrinia.Tests/MemoryDerivationTests.cs`

**Interfaces:**
- Changes: `MemoryDerivation.ForAppend(string[] fullContent, string? description = null, IReadOnlyDictionary<string,string[]>? attributes = null, string[]? keywords = null, ExistingMetadata? existing = null, Func<string,string?>? fileHasher = null)`. **`fileHasher` is added LAST** so no existing positional argument rebinds — the previous branch's riskiest task was a positional-rebinding hazard of exactly this shape, and appending is the safe direction.
- Existing private helper reused: `HashFileRefs(Dictionary<string,string[]>? attributes, Func<string,string?>? fileHasher) -> Dictionary<string,string>?` at `:516-533`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/MemoryDerivationTests.cs`. **Both assertions are required** — the second is the one that fails if someone later "simplifies" the rule to re-hash everything, which would silently destroy drift detection while the first assertion stayed green.

```csharp
    [Fact]
    public void ForAppend_HashesAFileTheAppendedChunkReferencesForTheFirstTime()
    {
        // D-21 carried the existing baseline forward but never added to it, so a file first cited
        // by an appended chunk landed in the searchable `file` attribute with no hash behind it —
        // and list(mode:"drift") then reported it clean forever, having nothing to compare.
        var derived = MemoryDerivation.ForAppend(
            ["first chunk", "second chunk, see file:src/new.cs"],
            existing: new ExistingMetadata(
                Attributes: new() { ["file"] = ["src/old.cs"] },
                FileHashes: new() { ["src/old.cs"] = "aaa" }),
            fileHasher: path => path == "src/new.cs" ? "bbb" : "ignored-should-not-be-called");

        derived.FileHashes.Should().ContainKey("src/new.cs").WhoseValue.Should().Be("bbb",
            because: "a file first referenced by the appended chunk must gain a drift baseline");
    }

    [Fact]
    public void ForAppend_LeavesAnAlreadyBaselinedFileAtItsOriginalHash()
    {
        // THE LOAD-BEARING ASSERTION. Re-hashing on every append would re-baseline the file to its
        // CURRENT content each time, so it could never be reported as drifted — the drift signal
        // would be destroyed by the very operation meant to maintain it. The hasher here returns a
        // different value on purpose: if the rule re-hashes, this fails.
        var derived = MemoryDerivation.ForAppend(
            ["first chunk", "second chunk mentioning file:src/old.cs again"],
            existing: new ExistingMetadata(
                Attributes: new() { ["file"] = ["src/old.cs"] },
                FileHashes: new() { ["src/old.cs"] = "original-hash" }),
            fileHasher: _ => "file-has-since-changed");

        derived.FileHashes.Should().ContainKey("src/old.cs").WhoseValue.Should().Be("original-hash",
            because: "the baseline is what drift compares AGAINST — re-hashing it on append " +
                     "silently destroys drift detection");
    }

    [Fact]
    public void ForAppend_WithoutAHasherKeepsTheCarriedBaselineUnchanged()
    {
        var derived = MemoryDerivation.ForAppend(
            ["first chunk", "second chunk, see file:src/new.cs"],
            existing: new ExistingMetadata(FileHashes: new() { ["src/old.cs"] = "aaa" }));

        derived.FileHashes.Should().BeEquivalentTo(new Dictionary<string, string> { ["src/old.cs"] = "aaa" },
            because: "no resolver means no baseline can be computed — D-04 — and the carried one survives");
    }
```

- [ ] **Step 2: Run and watch it fail**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~ForAppend_Hashes|FullyQualifiedName~ForAppend_Leaves|FullyQualifiedName~ForAppend_Without"
```

Expected: the first test **fails to compile** (no `fileHasher` parameter). That is the correct starting point. If it compiles, you are on the wrong baseline.

- [ ] **Step 3: Add the parameter and the rule**

In `src/Scrinia.Core/MemoryDerivation.cs`, add to the signature at `:177-182`:

```csharp
    /// <param name="fileHasher">
    /// Resolves a <c>file</c> attribute value to a content hash, or null when it cannot be hashed.
    /// Supplied by the store; null means no new baseline is computed. Same delegate
    /// <see cref="ForStore"/> takes, and the pipeline's only contact with the filesystem (D-04).
    /// </param>
    public static DerivedMetadata ForAppend(
        string[] fullContent,
        string? description = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string[]? keywords = null,
        ExistingMetadata? existing = null,
        Func<string, string?>? fileHasher = null)
```

Replace `:250` — note this must sit **after** `attrs` is assembled at `:237-245`, since the newly-referenced paths arrive through the appended chunk's extracted attributes:

```csharp
        // D-21 carried the baseline forward, which stopped a REST append destroying it. This adds
        // to it: a file the appended chunk references for the first time gets hashed now, because
        // otherwise its path reaches the searchable `file` attribute through the carrier below
        // while drift detection has nothing to compare it against and reports it clean forever.
        //
        // ONLY NEW PATHS ARE HASHED. An entry already in the baseline keeps its original hash —
        // re-hashing would re-baseline the file to its current content on every append, so it
        // could never be reported as drifted, and the append would destroy the very signal the
        // baseline exists to provide. A path present in `file` but absent from the baseline
        // because it was unhashable at store time is hashed here if it now resolves: it was never
        // baselined, and this is the first opportunity.
        var fileHashes = existing?.FileHashes;
        var newHashes = HashFileRefs(attrs, fileHasher);
        if (newHashes is not null)
        {
            var combined = fileHashes is null
                ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(fileHashes, StringComparer.OrdinalIgnoreCase);
            foreach (var (path, hash) in newHashes)
                if (!combined.ContainsKey(path)) combined[path] = hash;
            fileHashes = combined;
        }
```

`HashFileRefs` takes `Dictionary<string, string[]>?`. Read `attrs`'s declared type at that point (it
is the result of `KnownAttributes.Merge`) and, if it is not already that type, convert at the call
rather than changing `HashFileRefs` — `ForStore` shares that helper and must not be disturbed.

**Ordering caution:** `fileHashes` is read at `:257-259` to build the carrier. Your new code must run before that line, and the block currently at `:250` is above it — keep the same position.

- [ ] **Step 4: Pass the hasher from the store**

`src/Scrinia.Core/FileMemoryStore.cs:1318-1319`:

```csharp
        var derived = MemoryDerivation.ForAppend(
            fullContent, write.Description, write.Attributes, write.Keywords, existing,
            fileHasher: HashWorkspaceFile);
```

This is `ForAppend`'s only call site, and the server's append routes through the same store — so local and remote inherit the fix together with no wire change.

- [ ] **Step 5: Verify**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test
```

Expected: 0 errors; **2064 + 3 = 2067** passed, 0 failed, 4 skipped.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/MemoryDerivation.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/MemoryDerivationTests.cs
rtk git commit -m "fix(core): an append baselines the files its new chunk references"
```

---

### Task 6: `EphemeralEntry` gains `FileHashes`, and the remote helper gets a real workspace root

Closes C-a and C-b. **These two must land in one commit.** Adding the field without fixing the helper's root converts a discarded wrong answer into a persisted one: every remote ephemeral write would store a baseline recording each referenced file as unhashable.

**Files:**
- Modify: `src/Scrinia.Core/Models/EphemeralEntry.cs`, `src/Scrinia.Core/FileMemoryStore.cs:326-344,1388-1423`, `src/Scrinia/HttpMemoryStore.cs:225-244`, `src/Scrinia/Commands/ScriniaCommands.Serve.cs:62`
- Test: `tests/Scrinia.Tests/EphemeralLifecycleTests.cs` (create)

**Interfaces:**
- Produces: `EphemeralEntry.FileHashes` (`Dictionary<string,string>?`, JSON key `codeRefs`), positioned to match `ArtifactEntry`'s member of the same name.
- Changes: `HttpMemoryStore(HttpClient http, string store = "default", TimeSpan? timeout = null, CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null, string? workspaceRoot = null)`. **Added LAST**, defaulting to null, so every existing construction compiles unchanged.

- [ ] **Step 1: Add the field**

`src/Scrinia.Core/Models/EphemeralEntry.cs` — mirror `ArtifactEntry`'s member exactly, including the JSON name:

```csharp
    /// <inheritdoc cref="ArtifactEntry.FileHashes"/>
    [property: System.Text.Json.Serialization.JsonPropertyName("codeRefs")]
    Dictionary<string, string>? FileHashes = null,
```

Place it before `AgentKeywords` so the two records' member order matches. Because these are positional records, **check every `new EphemeralEntry(...)` call site that passes arguments positionally past this point** — a positional call would silently rebind. Named arguments are used at `FileMemoryStore.cs:1396-1408`; verify the others.

- [ ] **Step 2: Carry it through all three projections**

`PersistEphemeral` (`:1388-1423`) writes **two** objects — the stored `EphemeralEntry` and the returned `ArtifactEntry` — and `ListEphemeral` (`:326-344`) writes a third. All three currently agree by omitting `FileHashes`; the comment at `:338-344` states the invariant explicitly: *"this projection and PersistEphemeral's are two views of ONE memory, and a field present in one and null in the other means the answer depends on which read you came through."*

Add `FileHashes: derived.FileHashes` to both objects in `PersistEphemeral`, and `FileHashes: e.FileHashes` to `ListEphemeral`'s. Missing one reintroduces exactly the defect that comment warns about.

- [ ] **Step 3: Give the remote helper a real root**

`src/Scrinia/HttpMemoryStore.cs`. Add the parameter and replace the comment, which currently asserts a safety property the code does not have:

```csharp
    public HttpMemoryStore(HttpClient http, string store = "default", TimeSpan? timeout = null,
        CircuitBreaker? circuitBreaker = null, RetryOptions? retryOptions = null,
        string? workspaceRoot = null)
    {
        // ... unchanged assignments ...

        // The helper store serves two purposes: the pure string operations (naming, parsing,
        // sanitizing) that a remote store answers locally, and — see WriteEphemeralInProcessAsync
        // — ephemeral writes, which are addressed to this process and must be derived by a real
        // store rather than sent to one.
        //
        // Its root is the WORKSPACE root when the caller supplies one, and that matters: an
        // ephemeral write is derived by FileMemoryStore.StoreAsync, which hashes `file` attribute
        // values (MemoryDerivation.ForStore's fileHasher) BEFORE it branches on ephemerality. A
        // throwaway root resolves every reference to nothing, so the memory's drift baseline
        // records each referenced file as unhashable. That was invisible only while EphemeralEntry
        // had no FileHashes member to store the wrong answer in. The comment that used to sit here
        // claimed the write "returns before any path is resolved" — it does not, and that claim is
        // how this stayed hidden.
        //
        // The temp directory remains the fallback for callers with no workspace (tests, and any
        // construction that predates this parameter), and nothing is ever written under it.
        _tempDir = Path.Combine(Path.GetTempPath(), $"scrinia-http-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
        _localHelper = new FileMemoryStore(workspaceRoot ?? _tempDir);
    }
```

- [ ] **Step 4: Supply it at the composition root**

`src/Scrinia/Commands/ScriniaCommands.Serve.cs:62`. The `else` branch does **not** construct a `FileMemoryStore` directly — it calls `WorkspaceSetup.Configure(workspaceRoot)` at `:67`, and the resolution happens one level down at `WorkspaceSetup.cs:51`. Use that same expression:

```csharp
            MemoryStoreContext.Current = new HttpMemoryStore(
                httpClient, store ?? "default",
                workspaceRoot: WorkspaceRoot.Resolve(workspaceRoot, Directory.GetCurrentDirectory()));
```

`WorkspaceRoot` is in `Scrinia.Core`, so no new dependency. **It never throws for a missing workspace** — `WorkspaceRoot.cs:28` falls back to the start directory when no repository root is found — so `scri serve --remote` outside a workspace resolves to the current directory rather than failing. That is the right answer: `HashWorkspaceFile` already guards every path with `PathContainment.IsWithin` and `File.Exists`, so an unresolvable reference simply yields no baseline, exactly as it does locally.

- [ ] **Step 5: Write the tests**

Create `tests/Scrinia.Tests/EphemeralLifecycleTests.cs`:

```csharp
    [Fact]
    public void EphemeralWrite_RecordsADriftBaselineForItsFileReferences()
    {
        // EphemeralEntry had no FileHashes member, so a `file` reference on an ephemeral memory
        // was derived and then discarded — the entry looked clean while carrying no baseline.
        // ... arrange a store rooted at a temp workspace containing src/thing.cs ...

        store.StoreAsync(new MemoryWrite("~scratch", "local", ["see file:src/thing.cs"])).Result
             .FileHashes.Should().ContainKey("src/thing.cs");

        store.GetEphemeral("scratch")!.FileHashes.Should().ContainKey("src/thing.cs",
            because: "the stored entry and the returned entry are two views of one memory");
    }

    [Fact]
    public void ListEphemeral_ProjectsFileHashesLikeTheWriteResponseDoes()
    {
        // Three projections of one ephemeral memory exist (PersistEphemeral's stored entry, its
        // returned entry, and ListEphemeral's). A field present in some and null in others means
        // the answer depends on which read you came through.
        // ... store the same memory, then ...

        var listed = store.ListEphemeral().Single().Entry;
        listed.FileHashes.Should().BeEquivalentTo(store.GetEphemeral("scratch")!.FileHashes);
    }
```

Follow the fixture style of `tests/Scrinia.Tests/FileMemoryStoreTests.cs` for temp-workspace setup and disposal.

- [ ] **Step 6: Verify**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test
```

Expected: 0 errors; **2067 + 2 = 2069** passed, 0 failed, 4 skipped.

- [ ] **Step 7: Commit — one commit, both halves**

```bash
rtk git add src/Scrinia.Core/Models/EphemeralEntry.cs src/Scrinia.Core/FileMemoryStore.cs src/Scrinia/HttpMemoryStore.cs src/Scrinia/Commands/ScriniaCommands.Serve.cs tests/Scrinia.Tests/EphemeralLifecycleTests.cs
rtk git commit -m "fix(core): ephemeral memories carry a drift baseline, hashed against the real workspace"
```

---

### Task 7: `CopyMemory` carries metadata across both cross-kind directions

Closes C1.

**Files:**
- Modify: `src/Scrinia.Core/FileMemoryStore.cs:1894-1902,1952-1960`
- Test: `tests/Scrinia.Tests/FileMemoryStoreTests.cs`

**Interfaces:**
- Consumes: `EphemeralEntry.FileHashes` (Task 6).

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/FileMemoryStoreTests.cs`. Assert **field-for-field**, not just the four fields this task names — the local/remote parity suite established that style, and it is why that suite caught fields nobody had thought to list.

```csharp
    [Fact]
    public void CopyMemory_PromotingAnEphemeral_CarriesEveryMetadataField()
    {
        // The defect: promotion built a fresh ArtifactEntry populated with a subset of fields,
        // dropping Keywords, TermFrequencies, AgentKeywords and UpdatedAt. TermFrequencies is what
        // BM25 scores against, so a promoted memory kept its bytes and preview and became
        // UNFINDABLE BY SEARCH.
        // ... store ~source with curated keywords and a file reference, then ...

        store.CopyMemory("~source", "promoted", overwrite: false, out _).Should().BeTrue();

        var source = store.GetEphemeral("source")!;
        var promoted = store.LoadIndex("local:default").Single(e => e.Name == "promoted");

        promoted.Keywords.Should().BeEquivalentTo(source.Keywords);
        promoted.TermFrequencies.Should().BeEquivalentTo(source.TermFrequencies,
            because: "this is what BM25 scores against — dropping it makes the copy unfindable");
        promoted.AgentKeywords.Should().BeEquivalentTo(source.AgentKeywords,
            because: "null means UNKNOWN, so losing a recorded list demotes curated keywords later");
        promoted.UpdatedAt.Should().Be(source.UpdatedAt);
        promoted.FileHashes.Should().BeEquivalentTo(source.FileHashes);
        promoted.ContentPreview.Should().Be(source.ContentPreview);
        promoted.Attributes.Should().BeEquivalentTo(source.Attributes);
        promoted.ChunkEntries.Should().BeEquivalentTo(source.ChunkEntries);
        promoted.OriginalBytes.Should().Be(source.OriginalBytes);
        promoted.ChunkCount.Should().Be(source.ChunkCount);
    }

    [Fact]
    public void CopyMemory_DemotingToAnEphemeral_CarriesEveryMetadataField()
    {
        // Same defect, opposite direction, same subset dropped.
        // ... store a persistent memory with curated keywords and a file reference, then ...

        store.CopyMemory("source", "~demoted", overwrite: false, out _).Should().BeTrue();

        var source = store.LoadIndex("local:default").Single(e => e.Name == "source");
        var demoted = store.GetEphemeral("demoted")!;

        demoted.Keywords.Should().BeEquivalentTo(source.Keywords);
        demoted.TermFrequencies.Should().BeEquivalentTo(source.TermFrequencies);
        demoted.AgentKeywords.Should().BeEquivalentTo(source.AgentKeywords);
        demoted.UpdatedAt.Should().Be(source.UpdatedAt);
        demoted.FileHashes.Should().BeEquivalentTo(source.FileHashes);
        demoted.ContentPreview.Should().Be(source.ContentPreview);
        demoted.Attributes.Should().BeEquivalentTo(source.Attributes);
        demoted.ChunkEntries.Should().BeEquivalentTo(source.ChunkEntries);
    }
```

- [ ] **Step 2: Run and watch it fail**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~CopyMemory_Promoting|FullyQualifiedName~CopyMemory_Demoting"
```

Expected: **FAIL** on `Keywords`, `TermFrequencies`, `AgentKeywords` and `UpdatedAt` in both.

- [ ] **Step 3: Carry the fields on promotion**

`FileMemoryStore.cs:1894-1902`:

```csharp
            // A copy carries; it does not re-derive. The artifact bytes are identical by
            // definition, so the derived metadata is identical — carrying it is correct by
            // construction, and re-deriving would re-hash files against the current workspace and
            // could produce an entry that differs from its byte-identical source. This branch used
            // to populate a subset, which dropped TermFrequencies — what BM25 scores against — and
            // left the promoted memory unfindable by search. CreatedAt is deliberately NOT carried:
            // the promoted memory is created now.
            var destEntry = new ArtifactEntry(
                Name: dstSubject,
                Uri: ArtifactUri(dstSubject, dstScope),
                OriginalBytes: entry.OriginalBytes,
                ChunkCount: entry.ChunkCount,
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: entry.ContentPreview,
                Keywords: entry.Keywords,
                TermFrequencies: entry.TermFrequencies,
                UpdatedAt: entry.UpdatedAt,
                ChunkEntries: entry.ChunkEntries,
                FileHashes: entry.FileHashes,
                Attributes: entry.Attributes,
                AgentKeywords: entry.AgentKeywords);
```

- [ ] **Step 4: Carry the fields on demotion**

`FileMemoryStore.cs:1952-1960` — same rule, and keep the existing `sourceEntry?.` null-tolerance, since the source entry may be absent when only the artifact file exists:

```csharp
            var ephEntry = new EphemeralEntry(
                Name: dstKey,
                Artifact: artifact,
                OriginalBytes: sourceEntry?.OriginalBytes ?? 0,
                ChunkCount: sourceEntry?.ChunkCount ?? Nmp2ChunkedEncoder.GetChunkCount(artifact),
                CreatedAt: DateTimeOffset.UtcNow,
                ContentPreview: sourceEntry?.ContentPreview ?? $"Loaded from {sourceName}",
                Keywords: sourceEntry?.Keywords,
                TermFrequencies: sourceEntry?.TermFrequencies,
                UpdatedAt: sourceEntry?.UpdatedAt,
                ChunkEntries: sourceEntry?.ChunkEntries,
                FileHashes: sourceEntry?.FileHashes,
                Attributes: sourceEntry?.Attributes,
                AgentKeywords: sourceEntry?.AgentKeywords);
```

- [ ] **Step 5: Verify**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test
```

Expected: 0 errors; **2069 + 2 = 2071** passed, 0 failed, 4 skipped.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/FileMemoryStoreTests.cs
rtk git commit -m "fix(core): a cross-kind copy carries the metadata that makes it findable"
```

---

### Task 8: `compact()` works on ephemerals

Closes C2.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:532-641`
- Test: `tests/Scrinia.Tests/EphemeralLifecycleTests.cs`

**Interfaces:**
- Consumes: `IMemoryStore.GetEphemeral(string key) -> EphemeralEntry?` (`FileMemoryStore.cs:317`), `MemoryNaming.StripEphemeralPrefix`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/EphemeralLifecycleTests.cs`:

```csharp
    [Fact]
    public async Task Compact_OnAMultiChunkEphemeral_MergesItsChunks()
    {
        // Compact never checked IsEphemeral and went straight to ReadArtifactAsync, which is
        // disk-only — so it reported "Memory '~scratch' not found." for a memory list() will show,
        // and suggested running list() to find it.
        // ... store ~scratch, append twice so it has three chunks ...

        // Assertion style: there is no BeSuccess helper in this repo. MCP tool methods here return
        // a YAML string; the established pattern is ResponseParser.Parse(result).Status. Check what
        // Compact's own existing tests do and match them.
        string result = await tools.Compact("~scratch");

        ResponseParser.Parse(result).Status.Should().Be("success");
        store.GetEphemeral("scratch")!.ChunkCount.Should().Be(1);
    }

    [Fact]
    public async Task Compact_OnAnEphemeral_PreservesItsCuratedMetadata()
    {
        // Compaction rewrites content, not metadata. The persistent path proves this already;
        // the ephemeral path must not be a second implementation with different behaviour.
        // ... store ~scratch with Description "curated" and keywords ["vulpine"], append twice ...

        await tools.Compact("~scratch");

        var compacted = store.GetEphemeral("scratch")!;
        compacted.ContentPreview.Should().Be("curated");
        compacted.Keywords.Should().Contain("vulpine");
        compacted.AgentKeywords.Should().Contain("vulpine",
            because: "only the agent's own keywords travel back into a rewrite — D-29");
    }
```

- [ ] **Step 2: Run and watch it fail**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test tests/Scrinia.Tests --filter "FullyQualifiedName~Compact_OnA"
```

Expected: **FAIL** with the not-found error.

- [ ] **Step 3: Read from the ephemeral map**

In `Compact`, replace the artifact read at `:540-552` so an ephemeral name reads from the map:

```csharp
        bool ephemeral = store.IsEphemeral(name);
        string ephemeralKey = ephemeral ? MemoryNaming.StripEphemeralPrefix(name) : name;
        EphemeralEntry? ephemeralEntry = ephemeral ? store.GetEphemeral(ephemeralKey) : null;

        string artifact;
        if (ephemeral)
        {
            // Ephemerals live in memory, not on disk. ReadArtifactAsync is disk-only, so this
            // method used to report a memory list() will happily show as "not found" — and then
            // suggest running list() to find it.
            if (ephemeralEntry is null)
                return ResponseBuilder.Error(
                    $"Memory '{name}' not found.",
                    ErrorCodes.NotFound,
                    "list() to see available memories",
                    "remember({ path: '...', content: [...] }) to create it");
            artifact = ephemeralEntry.Artifact;
        }
        else
        {
            try
            {
                artifact = await store.ReadArtifactAsync(subject, scope, cancellationToken);
            }
            catch (FileNotFoundException)
            {
                return ResponseBuilder.Error(
                    $"Memory '{name}' not found.",
                    ErrorCodes.NotFound,
                    "list() to see available memories",
                    "remember({ path: '...', content: [...] }) to create it");
            }
        }
```

- [ ] **Step 4: Read the existing entry from the right place**

At `:603-607`, the index lookup finds nothing for an ephemeral, which sends the curated-keyword recovery down the heuristic path even though the real answer is in the map. Source the four values it feeds from whichever entry exists:

```csharp
        // The existing entry, read BEFORE the write for the reason the persistent path documents:
        // compaction rewrites content, not metadata. For an ephemeral it comes from the map, not
        // the index — an index lookup finds nothing and would send D-29's provenance read to its
        // fallback heuristic with the real answer sitting in the map.
        string? existingPreview;
        Dictionary<string, string[]>? existingAttributes;
        string[]? existingAgentKeywords;
        string[]? existingKeywords;

        if (ephemeral)
        {
            existingPreview = ephemeralEntry!.ContentPreview;
            existingAttributes = ephemeralEntry.Attributes;
            existingAgentKeywords = ephemeralEntry.AgentKeywords;
            existingKeywords = ephemeralEntry.Keywords;
        }
        else
        {
            var entries = store.LoadIndex(scope);
            var existingEntry = entries.FirstOrDefault(
                e => e.Name.Equals(subject, StringComparison.OrdinalIgnoreCase));
            if (existingEntry is not null)
                subject = existingEntry.Name;
            existingPreview = existingEntry?.ContentPreview;
            existingAttributes = existingEntry?.Attributes;
            existingAgentKeywords = existingEntry?.AgentKeywords;
            existingKeywords = existingEntry?.Keywords;
        }

        string[]? curatedKeywords = existingAgentKeywords
            ?? MemoryDerivation.CuratedKeywords(existingKeywords, priorChunks);
```

Then feed the `MemoryWrite` at `:636-641` from these locals, and pass the **original `name`** (not `subject`) as the write's name when ephemeral, so `StoreAsync`'s `IsEphemeral` check sees the prefix and routes to `PersistEphemeral`.

**Check the "original archived" message** at `:645`: it is conditional on there having been an indexed entry, which is already false for an ephemeral, so it needs no change. Confirm that by reading it rather than assuming.

- [ ] **Step 5: Verify**

```bash
rtk dotnet build Scrinia.sln && rtk dotnet test
```

Expected: 0 errors; **2071 + 2 = 2073** passed, 0 failed, 4 skipped.

- [ ] **Step 6: Commit**

```bash
rtk git add src/Scrinia.Mcp/MemoryTools.Lifecycle.cs tests/Scrinia.Tests/EphemeralLifecycleTests.cs
rtk git commit -m "fix(mcp): compact works on ephemeral memories instead of denying they exist"
```

---

### Task 9: Documentation

**Files:**
- Modify: `CHANGELOG.md`, `docs/architecture/core.md:133-144`

- [ ] **Step 1: CHANGELOG**

Read the Unreleased section and match its heading style. Record, in this order:

1. **The behaviour change first, marked as such** — `reconcile()` now refuses to auto-merge two sidecars whose keywords are less than 70% similar by Jaccard, matching the git merge driver. Conflicts that resolved automatically before may now require a choice. The threshold is `jaccardThreshold` in `merge.config`.
2. Term frequencies merge on both routes: a git merge no longer drops theirs', and `reconcile()` no longer drops a one-sided map.
3. `reconcile()` no longer moves a memory's `createdAt` forward.
4. An append now records a drift baseline for files its new chunk references for the first time.
5. Ephemeral memories carry a drift baseline, and a remote ephemeral write hashes against the real workspace rather than a temporary directory.
6. Copying a memory into or out of ephemeral storage keeps its keywords, term frequencies, provenance and timestamps — previously the copy lost them and became unfindable by search.
7. `compact()` works on ephemeral memories instead of reporting them as not found.

- [ ] **Step 2: Architecture doc**

`docs/architecture/core.md:133-144` shows `EphemeralEntry`'s shape. Add the `FileHashes` member with the same annotation `ArtifactEntry`'s carries at `:108`, and check whether surrounding prose claims the two records differ in this respect — if it does, correct it.

- [ ] **Step 3: Commit**

```bash
rtk git add CHANGELOG.md docs/architecture/core.md
rtk git commit -m "docs: record the merge-parity behaviour change and the ephemeral drift baseline"
```

---

## Plan Verification Pass

A falsification pass ran against this plan before any implementer was dispatched, and found **five
false claims** — all corrected above. Recorded because each is a trap the next reader could fall
back into:

1. **`[SkippableFact]` does not exist in `Scrinia.Merge.Tests`.** No `Xunit.SkippableFact` package
   reference; every test in that project uses `[Fact]`.
2. **`GitMergeIntegrationTests` has no typed sidecar reader.** Its neighbours assert on raw merged
   text via `repo.ReadFile(Sidecar).Should().Contain(...)`. Setup is `GitMergeHarness`
   (`SeedThreeWay`, `RegisterDriver`, `WriteFile`, `ReadFile`, `Merge`), and its file-private
   `Meta(keywords, updatedAt)` had no term-frequency support.
3. **`BeError` / `BeSuccess` do not exist anywhere in the repo** — zero matches across all `*.cs`.
   `MemoryReconcile` returns a YAML **string**, not an `McpResponse`; the real pattern is
   `ResponseParser.Parse(result).Status`.
4. **`ScriniaCommands.Serve.cs`'s local branch constructs no store** — it delegates to
   `WorkspaceSetup.Configure`, and the resolution lives at `WorkspaceSetup.cs:51`.
5. **The admission gate regressed five existing green tests.** Every conflicted-sidecar fixture in
   the suite sits at Jaccard 0.333, and `ReconcileMetaProvenanceTests.cs` was not in any task's
   file list. Task 4 Step 0 exists because of this.

## Self-Review Notes

**Spec coverage.** A1 → Task 2. A2 → Task 4. A3 → Task 3. A4 → Task 2 (step 6). B1 → Task 5. C1 → Task 7. C2 → Task 8. C-a and C-b → Task 6, deliberately one task. Testing section → the vacuity probes in Tasks 1-4 and the behavioural tests throughout. The spec's two named risks → Task 4's behaviour-change note and Task 6's single-commit requirement.

**Task 1 gates Task 4 and cannot be skipped.** `SidecarAdmission` calls `Scrinia.Core.Merge.KeywordJaccard`, which does not exist until Task 1 lands.

**Task 6 gates Task 7.** `CopyMemory` cannot carry `FileHashes` across an ephemeral boundary before `EphemeralEntry` has the member.

**The riskiest task is 6, not 4.** Task 4 changes behaviour loudly and is covered by two direct tests. Task 6 adds a positional member to a record with multiple construction sites — a positional call past the insertion point silently rebinds and still compiles. Step 1 says so explicitly. The second risk in that task is partial application: three projections must gain the field, and the one in `ListEphemeral` is 1,000 lines away from the other two.

**Test counts are predicted per task** because each task's tests are enumerated. If a count comes in low, suspect a compile failure and stale assemblies before suspecting the plan — see the Tooling Notes.

**Deliberately not done:** unifying the two mergers' orchestration (the ancestor asymmetry is real — see the spec), and auditing `restore`/`versions` for the disk-only assumption `compact()` had (unscoped; if one shares the defect it is a new residual).
