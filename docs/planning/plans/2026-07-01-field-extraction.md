# Unified Fields Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** One multi-valued `Fields` store on index entries, populated equally from agent-supplied `key:value` keywords, mechanical content extraction, and reference extraction — consumed by ranking, the entity graph, and reference tracking; prefixed-keyword writes retired.

**Architecture:** Readers migrate first (scorer, entity graph, References/orphan tracking gain dual-read of Fields ∪ legacy prefixed keywords), then the write paths switch (MCP `MemoryTools`, server `MemoryOrchestrator`, `UpdateMeta`/`Link`) to stop emitting prefixed keywords. `KnownFields` is the single schema point (merge semantics + graph/ref membership). `CodeRefs` stays as the drift-hash sidecar, fed from the `file` field. The entity graph is rebuilt per query and never persisted — no stored format migrates.

**Tech Stack:** .NET 10 / C#, xUnit + FluentAssertions, `GeneratedRegex`, source-generated JSON contexts.

**Spec:** `docs/superpowers/specs/2026-07-01-field-extraction-design.md` (v2 unified)

## Global Constraints

- `Fields` type is `Dictionary<string, string[]>?` everywhere. Keys lowercased + trimmed; values verbatim (trimmed, never tokenized). Caps: **32** keys/entry, values ≤ **120** chars, key regex `[A-Za-z][A-Za-z0-9_-]{0,29}`.
- Merge semantics come ONLY from `KnownFields`: entity keys (`person,place,org,thing,topic`) and reference keys (`file,ref`) union-dedupe; all other keys are scalar newer-wins (replace whole value array). Within one extraction pass: multi-valued keys accumulate, scalar keys first-wins.
- Keyword routing rule (verbatim from spec): part before first `:` matches `[A-Za-z][A-Za-z0-9_-]{0,29}` AND remainder is non-empty AND doesn't start with `//` → routes to Fields; otherwise stays a keyword.
- TF seeding: +3 per token from field keys and values (between auto-keyword +2 and agent-keyword +5).
- Scorer: compound `key:value` pair exact = 50, value exact = 40, value contains = 12; the slots ALSO parse `key:value`-shaped legacy keywords (dual-read). All string comparisons explicit `StringComparison.OrdinalIgnoreCase` — dictionary comparers do NOT survive JSON round-trips.
- New writes stop emitting `person:`/`file:`/`ref:` prefixed keywords entirely (dual-read only, no dual-write). Old entries migrate organically on next store/append.
- Old index entries (null `Fields`) must load and behave exactly as today — no NREs, no score changes for them beyond what dual-read intentionally adds.
- Commit trailer for every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- Branch setup (before Task 1): `git checkout main && git checkout -b feature/unified-fields`, then cherry-pick the doc commits from `experiment/kompress-locomo`: `git cherry-pick fe23fff aeddee8 8595ce1 <this-plan's-commit>`. Do not carry over any other kompress-branch changes.
- Tests: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "<filter>" --nologo -v q` (server tests: `tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`). Pre-existing CA1707 warnings are noise.
- Verified harness surfaces: `TestHelpers.StoreScope` exposes `WorkspaceDir` (`tests/Scrinia.Tests/TestHelpers.cs:86-92`); `FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions = null)` (`src/Scrinia.Core/FileMemoryStore.cs:180`); `IMemoryStore.SearchAll(string query, string? scopes = null, int limit = 20)` (`src/Scrinia.Core/IMemoryStore.cs:27`); `EntryResult(ScopedArtifact Item, double Score)` in `Scrinia.Core.Search` (`src/Scrinia.Core/Search/SearchResults.cs:9`); MCP tool tests use `TestHelpers.StoreScope` + `Tools()` + `ScriniaArtifactStore.LoadIndex()` (see `Store_WithAnchors_PersistsAnchors_SeedsTf_AndSurfacesViaSearch`, `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs:2025`).

---

### Task 1: `KnownFields` — the single schema point

**Files:**
- Create: `src/Scrinia.Core/Search/KnownFields.cs`
- Test: `tests/Scrinia.Tests/KnownFieldsTests.cs`

**Interfaces:**
- Produces (all later tasks consume):
  - `public static readonly IReadOnlySet<string> EntityKeys` — `person, place, org, thing, topic` (OrdinalIgnoreCase).
  - `public static readonly IReadOnlySet<string> ReferenceKeys` — `file, ref` (OrdinalIgnoreCase).
  - `public static bool IsMultiValued(string key)` — true for entity ∪ reference keys.
  - `public static Dictionary<string, string[]>? Merge(Dictionary<string, string[]>? existing, Dictionary<string, string[]>? incoming)` — per-key: multi-valued → union-dedupe (OrdinalIgnoreCase, existing order first); scalar → incoming replaces. 32-key cap (existing keys first). Null when both empty.
  - `public const int MaxFields = 32;`

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="KnownFields"/> — the single schema point for unified fields:
/// which keys are multi-valued (entity + reference) and how merges behave per class.
/// </summary>
public sealed class KnownFieldsTests
{
    [Theory]
    [InlineData("person"), InlineData("place"), InlineData("org"), InlineData("thing"), InlineData("topic")]
    [InlineData("file"), InlineData("ref")]
    [InlineData("PERSON")] // case-insensitive
    public void IsMultiValued_TrueForEntityAndReferenceKeys(string key) =>
        KnownFields.IsMultiValued(key).Should().BeTrue();

    [Theory]
    [InlineData("status"), InlineData("verdict"), InlineData("version"), InlineData("why")]
    public void IsMultiValued_FalseForScalarKeys(string key) =>
        KnownFields.IsMultiValued(key).Should().BeFalse();

    [Fact]
    public void Merge_MultiValued_UnionsAndDedupes()
    {
        var existing = new Dictionary<string, string[]> { ["person"] = ["alice"] };
        var incoming = new Dictionary<string, string[]> { ["person"] = ["Bob", "ALICE"] };

        var merged = KnownFields.Merge(existing, incoming);

        merged!["person"].Should().BeEquivalentTo(["alice", "Bob"],
            because: "entity fields accumulate across appends, deduped case-insensitively");
    }

    [Fact]
    public void Merge_Scalar_NewerWinsReplace()
    {
        var existing = new Dictionary<string, string[]> { ["status"] = ["active"], ["type"] = ["finding"] };
        var incoming = new Dictionary<string, string[]> { ["status"] = ["done"] };

        var merged = KnownFields.Merge(existing, incoming);

        merged!["status"].Should().BeEquivalentTo(["done"],
            because: "scalar fields replace — a changed status must not accumulate stale values");
        merged["type"].Should().BeEquivalentTo(["finding"], because: "untouched keys survive");
    }

    [Fact]
    public void Merge_NullHandling_AndCap()
    {
        KnownFields.Merge(null, null).Should().BeNull();
        KnownFields.Merge(new() { ["a"] = ["1"] }, null).Should().ContainKey("a");
        KnownFields.Merge(null, new() { ["b"] = ["2"] }).Should().ContainKey("b");

        var big = Enumerable.Range(1, 40).ToDictionary(i => $"key{i}", i => new[] { $"v{i}" });
        KnownFields.Merge(null, big)!.Should().HaveCount(KnownFields.MaxFields);
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~KnownFieldsTests" --nologo -v q`
Expected: compile FAILURE — `KnownFields` does not exist.

- [ ] **Step 3: Implement**

```csharp
namespace Scrinia.Core.Search;

/// <summary>
/// The single schema point for unified fields — the ONLY place that knows which field keys
/// are multi-valued and what merge semantics apply. Replaces the three divergent typed-prefix
/// lists (EntityGraph.RealTypes stays as an alias view for graph node identity; the benchmark
/// and guide copies are retired). Entity keys feed the entity graph; reference keys feed
/// References/Link/orphan tracking and CodeRefs hashing; everything else is scalar state.
/// </summary>
public static class KnownFields
{
    public const int MaxFields = 32;

    /// <summary>Entity field keys — union-merge, graph-building. Same set as EntityGraph.RealTypes.</summary>
    public static readonly IReadOnlySet<string> EntityKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "person", "place", "org", "thing", "topic" };

    /// <summary>Reference field keys — union-merge, feed References/Link/orphan + CodeRefs.</summary>
    public static readonly IReadOnlySet<string> ReferenceKeys =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "file", "ref" };

    /// <summary>Multi-valued keys accumulate across appends; everything else is scalar newer-wins.</summary>
    public static bool IsMultiValued(string key) =>
        EntityKeys.Contains(key) || ReferenceKeys.Contains(key);

    /// <summary>
    /// Merges incoming fields over existing per the key-class semantics: multi-valued keys
    /// union-dedupe (existing values keep their order); scalar keys are replaced by the
    /// incoming value array (newer-wins — first-wins would freeze stale statuses).
    /// Existing keys count first against the <see cref="MaxFields"/> cap. Null when both empty.
    /// </summary>
    public static Dictionary<string, string[]>? Merge(
        Dictionary<string, string[]>? existing,
        Dictionary<string, string[]>? incoming)
    {
        if (existing is not { Count: > 0 } && incoming is not { Count: > 0 })
            return null;

        var merged = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            foreach (var kv in existing)
            {
                if (merged.Count >= MaxFields) break;
                merged[kv.Key] = kv.Value;
            }
        }
        if (incoming is not null)
        {
            foreach (var kv in incoming)
            {
                if (merged.TryGetValue(kv.Key, out var current))
                {
                    merged[kv.Key] = IsMultiValued(kv.Key)
                        ? current.Union(kv.Value, StringComparer.OrdinalIgnoreCase).ToArray()
                        : kv.Value;
                }
                else if (merged.Count < MaxFields)
                {
                    merged[kv.Key] = kv.Value;
                }
            }
        }
        return merged;
    }
}
```

- [ ] **Step 4: Run to verify pass**

Same filter as Step 2. Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/KnownFields.cs tests/Scrinia.Tests/KnownFieldsTests.cs
git commit -m "feat(core): KnownFields — single schema point for unified fields"
```

---

### Task 2: `FieldExtractor` — extraction, keyword routing, TF seeding

**Files:**
- Create: `src/Scrinia.Core/Search/FieldExtractor.cs`
- Test: `tests/Scrinia.Tests/FieldExtractorTests.cs`

**Interfaces:**
- Consumes: `KnownFields.IsMultiValued/MaxFields` (Task 1); `TextAnalysis.Tokenize` (existing, `src/Scrinia.Core/Search/TextAnalysis.cs`).
- Produces:
  - `public static Dictionary<string, string[]>? Extract(string? content)` — frontmatter + line-start + bold-label patterns; null when none.
  - `public static (Dictionary<string, string[]>? Fields, string[]? Keywords) RouteKeywords(string[]? keywords)` — splits agent keywords into field-shaped vs plain; either side null when empty.
  - `public static void SeedTermFrequencies(Dictionary<string, string[]>? fields, Dictionary<string, int> tf)` — +3 per token from keys and all values.
  - `public const int MaxValueLength = 120;`

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="FieldExtractor"/> — mechanical key:value extraction (frontmatter,
/// line-start pairs, bold labels) and agent-keyword routing into the unified Fields store.
/// </summary>
public sealed class FieldExtractorTests
{
    [Fact]
    public void Extract_FrontmatterAndLineStartAndBoldLabel()
    {
        string content = "---\nStatus: Active\n---\nverdict: no-go\n**Why:** taxes retrieval\nProse: is skipped when value is prose? No — this extracts too.";
        var fields = FieldExtractor.Extract(content);

        fields.Should().NotBeNull();
        fields!["status"].Should().BeEquivalentTo(["Active"], because: "keys lowercase, values verbatim");
        fields["verdict"].Should().BeEquivalentTo(["no-go"]);
        fields["why"].Should().BeEquivalentTo(["taxes retrieval"]);
    }

    [Fact]
    public void Extract_BoldLabel_ColonOutsideVariant()
    {
        FieldExtractor.Extract("**Status**: active")!["status"].Should().BeEquivalentTo(["active"]);
    }

    [Fact]
    public void Extract_MultiValuedKey_AccumulatesWithinText()
    {
        var fields = FieldExtractor.Extract("person: Alice\nperson: Bob");
        fields!["person"].Should().BeEquivalentTo(["Alice", "Bob"],
            because: "entity keys accumulate within one text");
    }

    [Fact]
    public void Extract_ScalarKey_FirstWinsWithinText()
    {
        var fields = FieldExtractor.Extract("status: first\nstatus: second");
        fields!["status"].Should().BeEquivalentTo(["first"],
            because: "scalar keys are first-wins within one extraction pass");
    }

    [Fact]
    public void Extract_Guards_FenceUrlCharsetLengthEmpty()
    {
        string longVal = new string('x', 121);
        var fields = FieldExtractor.Extract(
            "```\nfenced: skipped\n```\n" +
            "http://example.com\n" +          // key "http", value "//..." → URL guard
            "1key: starts with digit\n" +
            "has.dot: bad charset\n" +
            "toolongkeytoolongkeytoolongkeyxx: over 30\n" +
            $"longval: {longVal}\n" +
            "emptyval:\n" +
            "ok: fine");
        fields.Should().NotBeNull();
        fields.Should().HaveCount(1);
        fields!["ok"].Should().BeEquivalentTo(["fine"]);
    }

    [Fact]
    public void Extract_CapsAtMaxFields_AndNullWhenNone()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"key{i}: value{i}");
        FieldExtractor.Extract(string.Join("\n", lines))!.Should().HaveCount(KnownFields.MaxFields);
        FieldExtractor.Extract("Just prose.").Should().BeNull();
        FieldExtractor.Extract(null).Should().BeNull();
    }

    [Fact]
    public void RouteKeywords_SplitsFieldShapedFromPlain()
    {
        var (fields, keywords) = FieldExtractor.RouteKeywords(
            ["person:Nick", "status:active", "plainword", "http://x.com", "person:Maria"]);

        fields.Should().NotBeNull();
        fields!["person"].Should().BeEquivalentTo(["Nick", "Maria"],
            because: "multi-valued keys accumulate; values verbatim");
        fields["status"].Should().BeEquivalentTo(["active"]);
        keywords.Should().BeEquivalentTo(["plainword", "http://x.com"],
            because: "plain and URL-guarded keywords stay keywords");
    }

    [Fact]
    public void RouteKeywords_NullAndAllPlain()
    {
        FieldExtractor.RouteKeywords(null).Should().Be((null, null));
        var (f, k) = FieldExtractor.RouteKeywords(["alpha", "beta"]);
        f.Should().BeNull();
        k.Should().BeEquivalentTo(["alpha", "beta"]);
    }

    [Fact]
    public void SeedTermFrequencies_Plus3PerTokenFromKeysAndAllValues()
    {
        var fields = new Dictionary<string, string[]> { ["verdict"] = ["rejected", "quiescent decision"] };
        var tf = new Dictionary<string, int> { ["decision"] = 1 };

        FieldExtractor.SeedTermFrequencies(fields, tf);

        tf["verdict"].Should().Be(3);
        tf["rejected"].Should().Be(3);
        tf["quiescent"].Should().Be(3);
        tf["decision"].Should().Be(4, because: "+3 stacks on the existing count");
    }

    [Fact]
    public void SeedTermFrequencies_NullOrEmpty_NoOp()
    {
        var tf = new Dictionary<string, int>();
        FieldExtractor.SeedTermFrequencies(null, tf);
        FieldExtractor.SeedTermFrequencies(new(), tf);
        tf.Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FieldExtractorTests" --nologo -v q`
Expected: compile FAILURE.

- [ ] **Step 3: Implement**

```csharp
using System.Text.RegularExpressions;

namespace Scrinia.Core.Search;

/// <summary>
/// Populates the unified Fields store from its two mechanical sources: content extraction
/// (frontmatter, line-start <c>key: value</c>, <c>**Bold:** label</c> lines) and agent-keyword
/// routing (<c>key:value</c>-shaped entries in the keywords param). LLM-free, regex-only,
/// run at write time. Values verbatim — exact-match signal for what BM25 tokenization shreds
/// (versions, dates, hyphenated identifiers, statuses). Conservative: nothing inside code
/// fences, URL-remainder values skipped, tight key charset, value + field caps.
/// Merge semantics live in <see cref="KnownFields"/>, not here.
/// </summary>
public static partial class FieldExtractor
{
    public const int MaxValueLength = 120;

    // Line-start key: value. Tight key charset so prose colons don't match.
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_-]{0,29}):[ \t]+(\S.*)$")]
    private static partial Regex LineStartPairPattern();

    // Bold labels: **Why:** value  or  **Why**: value (keys may contain internal spaces).
    [GeneratedRegex(@"^\*\*([A-Za-z][A-Za-z0-9 _-]{0,29})(?::\*\*|\*\*:)[ \t]*(\S.*)$")]
    private static partial Regex BoldLabelPattern();

    // Keyword routing: key charset before first colon, non-empty remainder.
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_-]{0,29}):(.+)$")]
    private static partial Regex KeywordFieldPattern();

    /// <summary>
    /// Extracts fields from content. Multi-valued keys (per <see cref="KnownFields"/>)
    /// accumulate; scalar keys are first-wins within one text. Null when none found.
    /// </summary>
    public static Dictionary<string, string[]>? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        Dictionary<string, List<string>>? found = null;
        bool inFence = false, firstLine = true, inFrontmatter = false;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                firstLine = false;
                continue;
            }
            if (inFence) { firstLine = false; continue; }

            if (line.Trim() == "---")
            {
                if (firstLine) inFrontmatter = true;
                else if (inFrontmatter) inFrontmatter = false;
                firstLine = false;
                continue;
            }
            firstLine = false;

            var (key, value) = MatchFieldLine(line);
            if (key is null) continue;

            found ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
            AddField(found, key, value!);
        }

        return ToFieldDict(found);
    }

    /// <summary>
    /// Splits agent-supplied keywords into field-shaped entries (routed to Fields) and plain
    /// keywords. Routing rule: key charset before the first colon, non-empty remainder that
    /// doesn't start with <c>//</c> (URL guard) and fits the value cap. Either side null when empty.
    /// </summary>
    public static (Dictionary<string, string[]>? Fields, string[]? Keywords) RouteKeywords(string[]? keywords)
    {
        if (keywords is not { Length: > 0 })
            return (null, null);

        Dictionary<string, List<string>>? found = null;
        List<string>? plain = null;

        foreach (string kw in keywords)
        {
            if (string.IsNullOrWhiteSpace(kw)) continue;
            Match m = KeywordFieldPattern().Match(kw.Trim());
            string? value = m.Success ? m.Groups[2].Value.Trim() : null;
            if (value is { Length: > 0 and <= MaxValueLength }
                && !value.StartsWith("//", StringComparison.Ordinal))
            {
                found ??= new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
                AddField(found, m.Groups[1].Value.ToLowerInvariant(), value);
            }
            else
            {
                (plain ??= []).Add(kw.Trim());
            }
        }

        return (ToFieldDict(found), plain is { Count: > 0 } ? [.. plain] : null);
    }

    /// <summary>
    /// Seeds field keys and value tokens into a term-frequency dictionary at +3 per token —
    /// between the auto-keyword (+2) and agent-keyword (+5) boosts — so BM25 picks up field
    /// vocabulary transparently.
    /// </summary>
    public static void SeedTermFrequencies(
        Dictionary<string, string[]>? fields, Dictionary<string, int> tf)
    {
        if (fields is not { Count: > 0 })
            return;

        foreach (var kv in fields)
        {
            Seed(kv.Key, tf);
            foreach (string value in kv.Value)
                Seed(value, tf);
        }

        static void Seed(string text, Dictionary<string, int> tf)
        {
            foreach (string token in TextAnalysis.Tokenize(text))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + 3;
            }
        }
    }

    /// <summary>Multi-valued keys accumulate (deduped); scalar keys are first-wins.</summary>
    private static void AddField(Dictionary<string, List<string>> found, string key, string value)
    {
        if (found.TryGetValue(key, out var values))
        {
            if (KnownFields.IsMultiValued(key)
                && !values.Contains(value, StringComparer.OrdinalIgnoreCase))
                values.Add(value);
            // scalar + existing → first-wins: ignore
        }
        else if (found.Count < KnownFields.MaxFields)
        {
            found[key] = [value];
        }
    }

    private static Dictionary<string, string[]>? ToFieldDict(Dictionary<string, List<string>>? found) =>
        found is { Count: > 0 }
            ? found.ToDictionary(kv => kv.Key, kv => kv.Value.ToArray(), StringComparer.OrdinalIgnoreCase)
            : null;

    private static (string? Key, string? Value) MatchFieldLine(string line)
    {
        Match m = BoldLabelPattern().Match(line);
        if (!m.Success)
            m = LineStartPairPattern().Match(line);
        if (!m.Success)
            return (null, null);

        string value = m.Groups[2].Value.Trim();
        if (value.Length == 0 || value.Length > MaxValueLength)
            return (null, null);
        if (value.StartsWith("//", StringComparison.Ordinal)) // URL remainder guard
            return (null, null);

        return (m.Groups[1].Value.Trim().ToLowerInvariant(), value);
    }
}
```

- [ ] **Step 4: Run to verify pass** — same filter; expected PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/FieldExtractor.cs tests/Scrinia.Tests/FieldExtractorTests.cs
git commit -m "feat(core): FieldExtractor — content extraction, keyword routing, TF seeding"
```

---

### Task 3: `Fields` on the index models + round-trip

**Files:**
- Modify: `src/Scrinia.Core/Models/ArtifactEntry.cs` and `src/Scrinia.Core/Models/EphemeralEntry.cs` (add after `RetrievalAnchors`)
- Test: `tests/Scrinia.Tests/FieldExtractorTests.cs` (co-locate)

**Interfaces:**
- Produces: `Dictionary<string, string[]>? Fields = null` as the LAST positional parameter on both records. `FileStoreJsonContext` is source-generated from the type shape — no context edits needed. Record `with`-copies carry Fields automatically; the already-lossy reconstruction branches (e.g. `FileMemoryStore.CopyMemory` fallbacks that drop Keywords/TF) stay lossy — NOT in scope.

- [ ] **Step 1: Failing test** (add to `FieldExtractorTests.cs`):

```csharp
    [Fact]
    public void ArtifactEntry_Fields_RoundTripsThroughIndexSaveLoad()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);

        var entry = new Scrinia.Core.Models.ArtifactEntry(
            Name: "fields-roundtrip", Uri: "file:///dev/null", OriginalBytes: 10, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow, Description: "test",
            Fields: new Dictionary<string, string[]> { ["person"] = ["Alice", "Bob"], ["status"] = ["no-go"] });

        store.Upsert(entry, "local");
        var reloaded = store.LoadIndex("local").Single(e => e.Name == "fields-roundtrip");

        reloaded.Fields!["person"].Should().BeEquivalentTo(["Alice", "Bob"]);
        reloaded.Fields["status"].Should().BeEquivalentTo(["no-go"], because: "values verbatim through JSON");
    }
```

- [ ] **Step 2: Verify failure** — filter `FullyQualifiedName~ArtifactEntry_Fields_RoundTrips`; expected compile FAILURE (no `Fields` param).

- [ ] **Step 3: Add to both records** — in `ArtifactEntry.cs` after `string[]? RetrievalAnchors = null` (change its trailing `);` to `,`):

```csharp
    /// <summary>
    /// Unified multi-valued fields — key:value facts from agent-supplied keywords
    /// (person:x routed at write time), mechanical content extraction, and reference
    /// extraction (file/ref). Keys lowercase, values verbatim. See Search.FieldExtractor
    /// and Search.KnownFields. Null for pre-fields entries (graceful degradation).
    /// </summary>
    Dictionary<string, string[]>? Fields = null);
```

Same addition (same doc comment) at the end of `EphemeralEntry.cs`.

- [ ] **Step 4: Build + verify pass** — `dotnet build --nologo -v q` clean, then Step 2 filter PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Models/ArtifactEntry.cs src/Scrinia.Core/Models/EphemeralEntry.cs tests/Scrinia.Tests/FieldExtractorTests.cs
git commit -m "feat(core): multi-valued Fields on ArtifactEntry/EphemeralEntry"
```

---

### Task 4: Scorer slots with legacy dual-read

**Files:**
- Modify: `src/Scrinia.Core/Search/WeightedFieldScorer.cs` — `ScoreEntryTerm` (line ~322, after the anchor block ending ~line 380) + the doc-comment score table (~line 14)
- Test: `tests/Scrinia.Tests/FieldExtractorTests.cs`

**Interfaces:**
- Consumes: `ArtifactEntry.Fields` (Task 3).
- Produces: pair-exact 50 / value-exact 40 / value-contains 12, over `Fields` AND over `key:value`-shaped legacy keywords (dual-read — pre-migration entries rank identically). Query terms reach `ScoreEntryTerm` whole (split on whitespace only, `WeightedFieldScorer.cs:303`), so `verdict:quiescent` arrives as one term.

- [ ] **Step 1: Failing tests** (add to `FieldExtractorTests.cs`):

```csharp
    private static Scrinia.Core.Models.ArtifactEntry MakeEntry(
        string name, Dictionary<string, string[]>? fields = null, string[]? keywords = null) =>
        new(
            Name: name, Uri: "file:///dev/null", OriginalBytes: 10, ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow, Description: "unrelated description",
            Keywords: keywords, Fields: fields);

    [Fact]
    public void SearchAll_FieldValueExact_OutranksFieldlessEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        store.Upsert(MakeEntry("with-field",
            new Dictionary<string, string[]> { ["verdict"] = ["quiescent"] }), "local");
        store.Upsert(MakeEntry("without-field"), "local");

        var results = store.SearchAll("quiescent", scopes: null, limit: 10);
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("with-field");
    }

    [Fact]
    public void SearchAll_CompoundKeyValueTerm_OutranksValueOnlyMatch()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        store.Upsert(MakeEntry("right-key",
            new Dictionary<string, string[]> { ["verdict"] = ["quiescent"] }), "local");
        store.Upsert(MakeEntry("wrong-key",
            new Dictionary<string, string[]> { ["mood"] = ["quiescent"] }), "local");

        var results = store.SearchAll("verdict:quiescent", scopes: null, limit: 10);
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("right-key", because: "pair exact (50) outranks value-only exact (40)");
    }

    [Fact]
    public void SearchAll_LegacyTypedKeyword_ScoresLikeAField()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        // Pre-migration entry: person:maria lives in Keywords, Fields is null.
        store.Upsert(MakeEntry("legacy", keywords: ["person:maria"]), "local");
        store.Upsert(MakeEntry("bystander"), "local");

        var results = store.SearchAll("person:maria", scopes: null, limit: 10);
        results.OfType<Scrinia.Core.Search.EntryResult>().First().Item.Entry.Name
            .Should().Be("legacy",
            because: "dual-read parses key:value legacy keywords into the field slots");
    }
```

- [ ] **Step 2: Verify failure** — filter `FullyQualifiedName~SearchAll_`; first two FAIL (no field scoring). Note: the legacy test may pass weakly via the existing keyword-exact(40) path — that's fine; the assertion holds before and after, it guards regression.

- [ ] **Step 3: Implement** — in `ScoreEntryTerm`, after the anchor block, before the description check:

```csharp
        // Unified-field matching. Compound "key:value" query terms hitting a (key, value)
        // pair score 50 (tag-exact parity); a term equal to a value scores 40 (keyword-exact
        // parity); contained in a value, 12. Dual-read: key:value-shaped LEGACY keywords
        // (person:maria written before unification) are parsed on the fly so pre-migration
        // entries rank identically — the cutover needs no re-index. Explicit OrdinalIgnoreCase
        // everywhere: dictionary comparers don't survive JSON round-trips.
        int sep = term.IndexOf(':');
        string? termKey = sep > 0 && sep < term.Length - 1 ? term[..sep] : null;
        string? termValue = termKey is not null ? term[(sep + 1)..] : null;

        if (entry.Fields is { Count: > 0 })
        {
            foreach (var field in entry.Fields)
            {
                foreach (string value in field.Value)
                {
                    if (termKey is not null
                        && field.Key.Equals(termKey, StringComparison.OrdinalIgnoreCase)
                        && value.Equals(termValue, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 50);
                    else if (value.Equals(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 40);
                    else if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                        score = Math.Max(score, 12);
                }
            }
        }

        if (entry.Keywords is { Length: > 0 })
        {
            foreach (string kw in entry.Keywords)
            {
                int kwSep = kw.IndexOf(':');
                if (kwSep <= 0 || kwSep >= kw.Length - 1) continue;
                string kwKey = kw[..kwSep];
                string kwValue = kw[(kwSep + 1)..];

                if (termKey is not null
                    && kwKey.Equals(termKey, StringComparison.OrdinalIgnoreCase)
                    && kwValue.Equals(termValue, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 50);
                else if (kwValue.Equals(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 40);
                else if (kwValue.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 12);
            }
        }
```

Update the doc-comment entry-score table (~line 14): add `/// Field key:value pair exact    50`, `/// Field value exact match       40`, `/// Field value contains          12` rows (note "fields ∪ legacy key:value keywords").

- [ ] **Step 4: Verify pass** — Step 2 filter PASS, then FULL suite `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --nologo -v q`. If an existing ranking test shifts, check whether its entries carry `key:value`-shaped keywords now scoring via dual-read (e.g. `file:x` refs matching a query term at 40 instead of contains-12); adjust the test's expectation ONLY if the new ranking is the intended semantics, otherwise fix the slot code.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/WeightedFieldScorer.cs tests/Scrinia.Tests/FieldExtractorTests.cs
git commit -m "feat(core): field-match scorer slots with legacy keyword dual-read"
```

---

### Task 5: Entity graph + References tool dual-read

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityGraph.cs` — add `ParseEntityFields` + a `Build` overload taking Fields
- Modify: `src/Scrinia.Core/Search/EntityGraphScoreContributor.cs:59-62` — pass Fields
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs:1263-1289` — `References` matches fields ∪ keywords
- Test: `tests/Scrinia.Tests/KnownFieldsTests.cs` (graph), `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (References)

**Interfaces:**
- Consumes: `KnownFields.EntityKeys`, `ArtifactEntry.Fields`.
- Produces:
  - `public static IEnumerable<(string Type, string Value)> ParseEntityFields(IReadOnlyDictionary<string, string[]>? fields)` on `EntityGraph` — yields (key, value) for keys in `EntityKeys`, lowercased, mirroring `ParseTypedEntities` output shape.
  - `public static EntityGraph Build(IEnumerable<(string DocKey, IReadOnlyList<string>? Keywords, IReadOnlyDictionary<string, string[]>? Fields)> docs)` — existing 2-tuple `Build` (`EntityGraph.cs:115`) delegates to it with null Fields. Per-doc tags = ParseTypedEntities(keywords) ∪ ParseEntityFields(fields).

- [ ] **Step 1: Failing tests** — graph parity in `KnownFieldsTests.cs`:

```csharp
    [Fact]
    public void EntityGraph_FieldsAndLegacyKeywords_ProduceIdenticalGraphs()
    {
        var fromKeywords = EntityGraph.Build(
            [("local|a", (IReadOnlyList<string>?)["person:maria", "org:acme"], null),
             ("local|b", ["person:maria"], null)]);
        var fromFields = EntityGraph.Build(
            [("local|a", null, (IReadOnlyDictionary<string, string[]>?)new Dictionary<string, string[]>
                 { ["person"] = ["Maria"], ["org"] = ["Acme"] }),
             ("local|b", null, new Dictionary<string, string[]> { ["person"] = ["maria"] })]);

        fromFields.EntityCount.Should().Be(fromKeywords.EntityCount);
        fromFields.DocCount.Should().Be(fromKeywords.DocCount);

        var scoresKw = fromKeywords.Score("tell me about maria", 0.15, 20);
        var scoresF = fromFields.Score("tell me about maria", 0.15, 20);
        scoresF.Should().BeEquivalentTo(scoresKw,
            because: "a fields entry and a legacy typed-keyword entry must graph identically");
    }

    [Fact]
    public void EntityGraph_MixedDoc_UnionsBothSources()
    {
        var graph = EntityGraph.Build(
            [("local|a", (IReadOnlyList<string>?)["person:maria"],
              (IReadOnlyDictionary<string, string[]>?)new Dictionary<string, string[]> { ["org"] = ["acme"] })]);
        graph.EntityCount.Should().Be(2, because: "keywords ∪ fields on the same doc");
    }
```

References dual-read in `ScriniaMcpToolsTests.cs` — see Step 3 for what makes it pass; store one legacy-style entry by direct `ScriniaArtifactStore`/`store.Upsert` with `Keywords: ["file:src/Foo.cs"]` and one with `Fields: new() { ["file"] = ["src/Foo.cs"] }`, then:

```csharp
    [Fact]
    public async Task References_FindsBothLegacyKeywordAndFieldEntries()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["legacy body"], "legacy-ref", keywords: ["file:src/Foo.cs"]);
        // After Task 7 the line above routes to Fields; to pin LEGACY shape, upsert directly:
        var store = ScriniaArtifactStore.Store; // or however tests reach the store — mirror the file's pattern
        var legacy = store.LoadIndex("local").Single(e => e.Name == "legacy-ref");
        store.Upsert(legacy with { Fields = null, Keywords = ["file:src/Foo.cs"] }, "local");

        store.Upsert(legacy with { Name = "fields-ref", Keywords = null,
            Fields = new Dictionary<string, string[]> { ["file"] = ["src/Foo.cs"] } }, "local");

        string result = await Tools().References("src/Foo.cs");
        result.Should().Contain("legacy-ref").And.Contain("fields-ref");
    }
```

(Adapt the store-access line to the test file's existing pattern for reaching the active store — several tests in the file manipulate entries directly; mirror them.)

- [ ] **Step 2: Verify failure** — graph tests: compile FAILURE (no 3-tuple Build). References test: FAIL (fields entry not found).

- [ ] **Step 3: Implement**

`EntityGraph.cs` — add below `ParseTypedEntities`:

```csharp
    /// <summary>
    /// Fields-side sibling of <see cref="ParseTypedEntities"/>: yields (key, value) pairs for
    /// entity-class fields (KnownFields.EntityKeys), lowercased, values kept whole. Together
    /// they give Build its dual-read: legacy typed keywords ∪ unified fields.
    /// </summary>
    public static IEnumerable<(string Type, string Value)> ParseEntityFields(
        IReadOnlyDictionary<string, string[]>? fields)
    {
        if (fields is null) yield break;
        foreach (var kv in fields)
        {
            if (!KnownFields.EntityKeys.Contains(kv.Key)) continue;
            string type = kv.Key.ToLowerInvariant();
            foreach (string raw in kv.Value)
            {
                string value = raw.Trim().ToLowerInvariant();
                if (value.Length > 0) yield return (type, value);
            }
        }
    }
```

Change the existing `Build` (2-tuple, `:115`) to delegate, and add the 3-tuple overload — inside it, replace the per-doc parse line (`foreach (var pair in ParseTypedEntities(keywords)) tags.Add(pair);`) with:

```csharp
    public static EntityGraph Build(IEnumerable<(string DocKey, IReadOnlyList<string>? Keywords)> docs)
        => Build(docs?.Select(d => (d.DocKey, d.Keywords, (IReadOnlyDictionary<string, string[]>?)null)) ?? []);

    public static EntityGraph Build(
        IEnumerable<(string DocKey, IReadOnlyList<string>? Keywords, IReadOnlyDictionary<string, string[]>? Fields)> docs)
    {
        // ... same two-pass body as today, but per doc:
        var tags = new List<(string Type, string Value)>();
        foreach (var pair in ParseTypedEntities(keywords)) tags.Add(pair);
        foreach (var pair in ParseEntityFields(fields)) tags.Add(pair);
        // guard changes from `keywords is not { Count: > 0 }` to `tags.Count == 0` after parsing both
    }
```

(Move the whole existing body into the 3-tuple overload; the skip-guard `if (string.IsNullOrWhiteSpace(docKey) || keywords is not { Count: > 0 }) continue;` becomes `if (string.IsNullOrWhiteSpace(docKey)) continue;` followed by parsing both sources and `if (tags.Count == 0) continue;`.)

`EntityGraphScoreContributor.cs:59-61`:

```csharp
        var docs = new List<(string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)>(candidates.Count);
        foreach (var c in candidates)
            docs.Add(($"{c.Scope}|{c.Entry.Name}", c.Entry.Keywords, c.Entry.Fields));
        var graph = EntityGraph.Build(docs);
```

`References` (`MemoryTools.Core.cs:1273-1278`) — extend the match predicate:

```csharp
        var matches = allEntries
            .Where(sa =>
                (sa.Entry.Keywords is not null &&
                 sa.Entry.Keywords.Any(k =>
                     k.Equals(fileKey, StringComparison.OrdinalIgnoreCase) ||
                     k.Equals(refKey, StringComparison.OrdinalIgnoreCase) ||
                     k.EndsWith($"/{target}", StringComparison.OrdinalIgnoreCase)))
                ||
                (sa.Entry.Fields is not null &&
                 (MatchesFieldRef(sa.Entry.Fields, "file", target) ||
                  MatchesFieldRef(sa.Entry.Fields, "ref", target))))
```

with a private helper in the same file:

```csharp
    /// <summary>Reference-field match: exact value or path-suffix ("/{target}") — the fields-side
    /// twin of the legacy file:/ref: keyword predicate in References.</summary>
    private static bool MatchesFieldRef(Dictionary<string, string[]> fields, string key, string target)
    {
        foreach (var kv in fields)
        {
            if (!kv.Key.Equals(key, StringComparison.OrdinalIgnoreCase)) continue;
            foreach (string v in kv.Value)
            {
                if (v.Equals(target, StringComparison.OrdinalIgnoreCase)
                    || v.EndsWith($"/{target}", StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        return false;
    }
```

- [ ] **Step 4: Verify pass** — graph + References filters PASS; full Scrinia.Tests suite PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityGraph.cs src/Scrinia.Core/Search/EntityGraphScoreContributor.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/
git commit -m "feat(core,mcp): entity graph + References dual-read fields ∪ legacy keywords"
```

---

### Task 6: MaintenanceEventSink — ref field read/write

**Files:**
- Modify: `src/Scrinia.Core/MaintenanceEventSink.cs` (whole file is 117 lines — auto-link writes `ref:{source}` keywords at `:61-69`; orphan detection reads inbound `ref:` keywords at `:88-92`)
- Test: `tests/Scrinia.Tests/` — locate the existing MaintenanceEventSink tests with `Grep "MaintenanceEventSink" tests/`; add there (create `tests/Scrinia.Tests/MaintenanceEventSinkFieldsTests.cs` if none exist).

**Interfaces:**
- Consumes: `KnownFields.Merge`, `ArtifactEntry.Fields`.
- Produces: auto-link writes `Fields["ref"] += sourceName` on the target (no more `ref:` keyword writes); orphan detection counts inbound refs from `Fields["ref"]` ∪ legacy `ref:` keywords.

- [ ] **Step 1: Failing test**

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;

namespace Scrinia.Tests;

/// <summary>MaintenanceEventSink under unified fields: auto-links land in Fields["ref"],
/// orphan detection sees inbound refs in fields OR legacy ref: keywords.</summary>
public sealed class MaintenanceEventSinkFieldsTests
{
    [Fact]
    public async Task AutoLink_WritesRefField_NotKeyword_AndOrphanSeesBothShapes()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        var sink = new MaintenanceEventSink();

        // Target that will be referenced.
        store.Upsert(new ArtifactEntry("api:auth-flow".Replace(":", "_"), "file:///dev/null", 1, 1,
            DateTimeOffset.UtcNow, "target"), "local");
        // NOTE: adapt the target name/scope to how ParseQualifiedName resolves "api:auth-flow"
        // in this store — mirror existing sink tests if present; the assertion below is the contract.

        await sink.OnStoredAsync("local/source-mem", ["see api:auth-flow for details"], store, default);

        var target = store.LoadIndex(/* target scope per naming */ "local")
            .Single(e => e.Name.Contains("auth-flow"));
        target.Fields.Should().NotBeNull();
        target.Fields!["ref"].Should().Contain(v => v.Contains("source-mem"),
            because: "auto-link writes the ref FIELD now");
        (target.Keywords ?? []).Should().NotContain(k => k.StartsWith("ref:"),
            because: "prefixed ref: keywords are no longer written");
    }
}
```

(First run `Grep "MaintenanceEventSink" tests/Scrinia.Tests/` — if existing tests cover auto-link/orphan, model the arrange section on them exactly; the name-resolution comment above disappears once the real pattern is copied. The two assertions are the contract.)

- [ ] **Step 2: Verify failure** — ref lands in Keywords, not Fields.

- [ ] **Step 3: Implement** — in `AutoLinkAndDetectOrphans`:

Replace the link-write block (`:61-69`):

```csharp
                // Write the ref FIELD (unified fields) — no more ref: keyword emission.
                string[] existingRefs = targetEntry.Fields is not null
                    && targetEntry.Fields.TryGetValue("ref", out var r) ? r : [];
                if (existingRefs.Contains(qualifiedName, StringComparer.OrdinalIgnoreCase)
                    || targetEntry.Keywords?.Contains($"ref:{qualifiedName}", StringComparer.OrdinalIgnoreCase) == true)
                    continue; // already linked (either shape)

                var updatedFields = KnownFields.Merge(
                    targetEntry.Fields, new() { ["ref"] = [qualifiedName] });
                store.Upsert(targetEntry with { Fields = updatedFields }, targetScope);
```

Replace the inbound-ref check (`:91-92`):

```csharp
            bool hasInboundRefs = allEntries.Any(sa =>
                (sa.Entry.Keywords?.Any(k => k.Equals(inboundRefKey, StringComparison.OrdinalIgnoreCase)) == true)
                || (sa.Entry.Fields is not null
                    && sa.Entry.Fields.TryGetValue("ref", out var refs)
                    && refs.Any(v => v.Equals(sourceQualified, StringComparison.OrdinalIgnoreCase))));
```

(Note: `Fields.TryGetValue("ref", ...)` is safe here even though JSON-deserialized dictionaries lose the comparer — the write side always stores the key as lowercase `"ref"`, and this sink is the only writer. Keep the literal lowercase.)

- [ ] **Step 4: Verify pass** — new test + full suite.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/MaintenanceEventSink.cs tests/Scrinia.Tests/
git commit -m "feat(core): MaintenanceEventSink links via ref field, orphan check dual-reads"
```

---

### Task 7: MCP Store path — routing, extraction, refs-as-fields, CodeRefs

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `Store` (~lines 128-356), `ExtractRefKeywords` (~line 1386)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes: `FieldExtractor.{Extract,RouteKeywords,SeedTermFrequencies}`, `KnownFields.Merge`, `Fields` params.
- Produces: `Store` persists `Fields` = merge of (routed keywords ∪ content extraction ∪ file/ref reference fields); `Keywords` holds ONLY plain (non-`key:value`) keywords + auto-extracted terms; CodeRefs built from `Fields["file"]` ∪ explicit `codeRefs` param. New helper replaces `ExtractRefKeywords`:
  `private static Dictionary<string, string[]>? ExtractRefFields(string text)` — `{ ["file"] = fileRefs, ["ref"] = memoryRefs }` (keys omitted when empty; null when both empty).

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public async Task Store_UnifiedFields_RoutesKeywordsExtractsContentAndRefs()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(
            ["---\nstatus: active\n---\nSee notes in src/Widget.cs today.\nperson mentioned inline: nobody"],
            "unified-store-test",
            keywords: ["person:Nick", "plainword"]);

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "unified-store-test");

        // Routed keyword → field; plain keyword stays.
        entry.Fields!["person"].Should().BeEquivalentTo(["Nick"]);
        entry.Keywords.Should().Contain("plainword");
        entry.Keywords.Should().NotContain(k => k.StartsWith("person:"),
            because: "prefixed keywords are no longer written");

        // Content extraction.
        entry.Fields["status"].Should().BeEquivalentTo(["active"]);

        // Reference extraction → file field (not file: keyword) → CodeRefs unaffected shape.
        entry.Fields["file"].Should().Contain("src/Widget.cs");
        entry.Keywords.Should().NotContain(k => k.StartsWith("file:"));

        // TF seeding at +3 for routed field value.
        entry.TermFrequencies!["nick"].Should().BeGreaterThanOrEqualTo(3);
    }
```

- [ ] **Step 2: Verify failure** — `Fields` null / prefixed keywords present.

- [ ] **Step 3: Implement** — in `Store`:

(a) Route BEFORE analysis merge. Replace `var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(keywords, autoKeywords);` (~line 134):

```csharp
        // Unified fields: key:value-shaped agent keywords route to Fields; only plain
        // keywords participate in the keyword merge. Prefixed-keyword writes are retired.
        var (routedFields, plainKeywords) = FieldExtractor.RouteKeywords(keywords);
        var (mergedKeywords, agentKeywordSet) = TextAnalysis.MergeKeywordsWithSource(plainKeywords, autoKeywords);
```

(b) Replace the ref-keyword concat (~line 138) `mergedKeywords = mergedKeywords.Concat(ExtractRefKeywords(rawContent))...`:

```csharp
        // References land in Fields (file/ref), not as prefixed keywords.
        var refFields = ExtractRefFields(rawContent);
```

(c) After the anchor-seeding block (~line 177):

```csharp
        // Assemble unified fields: routed keywords ∪ content extraction ∪ references,
        // then seed key/value tokens into TF at +3 (between auto +2 and agent +5).
        var fields = KnownFields.Merge(KnownFields.Merge(routedFields, FieldExtractor.Extract(joined)), refFields);
        FieldExtractor.SeedTermFrequencies(fields, tf);
```

(d) CodeRefs source (~lines 310-314) — replace the `file:` keyword scan:

```csharp
        var allRefPaths = (codeRefs ?? [])
            .Concat(fields is not null && fields.TryGetValue("file", out var fileRefs)
                ? fileRefs : [])
            .Distinct(StringComparer.OrdinalIgnoreCase);
```

(e) Pass `Fields: fields` on both the `EphemeralEntry` (~line 204) and `ArtifactEntry` (~line 329) constructions.

(f) Replace `ExtractRefKeywords` (~line 1386) with:

```csharp
    /// <summary>File and memory references as unified fields — {file: [...], ref: [...]}.
    /// Replaces the retired file:/ref: prefixed-keyword emission.</summary>
    private static Dictionary<string, string[]>? ExtractRefFields(string text)
    {
        var fileRefs = ReferenceExtractor.ExtractFileRefs(text);
        var memoryRefs = ReferenceExtractor.ExtractMemoryRefs(text);
        if (fileRefs.Length == 0 && memoryRefs.Length == 0) return null;
        var fields = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        if (fileRefs.Length > 0) fields["file"] = fileRefs;
        if (memoryRefs.Length > 0) fields["ref"] = memoryRefs;
        return fields;
    }
```

Keep `ExtractRefKeywords` deleted only if `Append`'s chunk-keyword use (`:1076`) and `BuildAppendMetadata` (`:1199`) are updated in the same commit — they are Task 8's scope, so for THIS task change them minimally to call `ExtractRefFields` and drop the concat (chunk keywords lose ref entries; chunk-level ref keywords were only feeding the same retired convention). If existing tests assert `file:` keywords on chunks, update them to the new contract.

- [ ] **Step 4: Verify pass** — new test + FULL suite. Existing tests asserting `file:`/`ref:` keywords (e.g. around `References`, `ExtractRefKeywords`, keyword listings) must be updated to expect fields — each such edit is the new intended contract, not a regression.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "feat(mcp): Store writes unified fields — keyword routing, extraction, refs, CodeRefs"
```

---

### Task 8: MCP Append path — KnownFields merge

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `Append` (~1027-1173) + `BuildAppendMetadata` (~1182-1242)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes: everything from Tasks 1-2 + 7 (`ExtractRefFields` helper).
- Produces: `BuildAppendMetadata` signature gains `string appendedContent`, `string[]? routedPlainKeywords` handling and `Dictionary<string, string[]>? existingFields`; returns 6-tuple ending `Dictionary<string, string[]>? Fields`. Fields = `KnownFields.Merge(existingFields, incoming)` where incoming = routed-new-keywords ∪ extract(appendedContent) ∪ refFields(appendedContent). Entity/ref accumulate, scalars replace.

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public async Task Append_FieldMerge_ScalarReplacesEntityAccumulates()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["status: active\nInitial."], "unified-append-test",
            keywords: ["person:Alice"]);
        await Tools().Append("status: done\nFollow-up.", "unified-append-test",
            keywords: ["person:Bob"]);

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "unified-append-test");
        entry.Fields!["status"].Should().BeEquivalentTo(["done"], because: "scalar newer-wins");
        entry.Fields["person"].Should().BeEquivalentTo(["Alice", "Bob"], because: "entities accumulate");
    }
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement**

In `Append`: route the caller's `keywords` once at the top of the metadata phase — `var (routedNewFields, plainNewKeywords) = FieldExtractor.RouteKeywords(keywords);` — and pass `plainNewKeywords` where `keywords` currently flows into `BuildAppendMetadata`, plus new args `content` and `existingEntry?.Fields` / `existingEph?.Fields` and `routedNewFields`. In `BuildAppendMetadata`, after the anchor merge:

```csharp
        // Unified fields: incoming = routed new keywords ∪ content extraction ∪ references
        // from the appended chunk; merged per KnownFields (entity/ref union, scalar
        // newer-wins). Seed the merged set at +3 — TF is rebuilt from scratch per append,
        // so carried-forward fields must re-seed (same contract as merged anchors).
        var incoming = KnownFields.Merge(
            KnownFields.Merge(routedNewFields, FieldExtractor.Extract(appendedContent)),
            ExtractRefFields(appendedContent));
        var mergedFields = KnownFields.Merge(existingFields, incoming);
        FieldExtractor.SeedTermFrequencies(mergedFields, tf);
```

Return `mergedFields` in the tuple; set `Fields: meta.Fields` on both the `EphemeralEntry` (~1100) and `ArtifactEntry` (~1145) constructions. Also update the create-via-append fallback (`:1052-1057`) — it already forwards `keywords` to `Store`, which now routes; no change needed there beyond confirming the test covers it implicitly.

- [ ] **Step 4: Verify pass** — new test + full suite.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "feat(mcp): Append merges unified fields per KnownFields semantics"
```

---

### Task 9: UpdateMeta + Link — fields-aware metadata updates

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` — `UpdateMeta` (~358-473), `Link` (~1292-1313)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes: `FieldExtractor.RouteKeywords`, `KnownFields.Merge`.
- Produces: `UpdateMeta`'s `keywords` param routes `key:value` entries into a `KnownFields.Merge` on the entry's Fields (plain ones merge into Keywords as today; routed values TF-seed at +5 — they are agent-curated). `Link` becomes two field merges: `ref → [to]` on `from`, `ref → [from]` on `to` (via `UpdateMeta(name, keywords: ["ref:..."])`, which now routes — Link's code is UNCHANGED except its doc comment; the routing does the work).

- [ ] **Step 1: Failing test**

```csharp
    [Fact]
    public async Task UpdateMeta_RoutesFieldShapedKeywords_AndLinkWritesRefFields()
    {
        using var scope = new TestHelpers.StoreScope();
        await Tools().Store(["alpha body"], "um-alpha");
        await Tools().Store(["beta body"], "um-beta");

        await Tools().UpdateMeta("um-alpha", keywords: ["person:Carol", "plain-extra"]);
        var alpha = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "um-alpha");
        alpha.Fields!["person"].Should().BeEquivalentTo(["Carol"]);
        alpha.Keywords.Should().Contain("plain-extra");
        alpha.Keywords.Should().NotContain(k => k.StartsWith("person:"));

        await Tools().Link("um-alpha", "um-beta");
        var entries = ScriniaArtifactStore.LoadIndex();
        entries.Single(e => e.Name == "um-alpha").Fields!["ref"].Should().Contain(v => v.Contains("um-beta"));
        entries.Single(e => e.Name == "um-beta").Fields!["ref"].Should().Contain(v => v.Contains("um-alpha"));
    }
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement** — in `UpdateMeta`, replace the keyword-merge block (~392-402):

```csharp
        // Route key:value-shaped additions into Fields; only plain ones merge into Keywords.
        var (routedFields, plainAdds) = FieldExtractor.RouteKeywords(keywords);

        string[]? mergedKeywords = entry.Keywords;
        int addedCount = 0;
        if (plainAdds is { Length: > 0 })
        {
            var existing = entry.Keywords ?? [];
            mergedKeywords = existing.Union(plainAdds, StringComparer.OrdinalIgnoreCase).ToArray();
            addedCount = mergedKeywords.Length - existing.Length;
            if (addedCount > 0) changes.Add($"{addedCount} keyword(s) added");
        }

        Dictionary<string, string[]>? mergedFields = entry.Fields;
        if (routedFields is not null)
        {
            mergedFields = KnownFields.Merge(entry.Fields, routedFields);
            changes.Add($"{routedFields.Count} field(s) merged");
        }
```

TF update block (~438-455): keep the +5 seeding for genuinely-new plain keywords (swap `keywords` → `plainAdds`); add after it:

```csharp
        if (routedFields is not null)
        {
            var tfForFields = updatedTf is not null
                ? new Dictionary<string, int>(updatedTf) : new Dictionary<string, int>();
            // Agent-curated fields seed at +5, matching agent keywords (not the +3 auto rate).
            foreach (var kv in routedFields)
                foreach (string v in kv.Value)
                    foreach (string token in TextAnalysis.Tokenize(v))
                    {
                        tfForFields.TryGetValue(token, out int c);
                        tfForFields[token] = c + 5;
                    }
            updatedTf = tfForFields;
        }
```

`with`-expression (~457): add `Fields = mergedFields,`. Guard (~435): the no-changes check must consider `routedFields is not null` as a change. Update `Link`'s doc comment to say it writes `ref` fields (its `UpdateMeta(..., keywords: ["ref:{to}"])` calls now route automatically).

- [ ] **Step 4: Verify pass** — new test + full suite.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "feat(mcp): UpdateMeta routes fields; Link writes ref fields via routing"
```

---

### Task 10: Server orchestrator parity

**Files:**
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs` — `StoreAsync` (~15-137), `AppendAsync` (~139-259)
- Test: `tests/Scrinia.Server.Tests/` — locate with `Grep -l "MemoryOrchestrator" tests/Scrinia.Server.Tests/`; follow its arrange pattern (tests construct a `FileMemoryStore` over a temp dir; check `src/Scrinia.Server/Models/` for `StoreRequest`'s exact constructor).

**Interfaces:**
- Consumes: `FieldExtractor`, `KnownFields`, `Fields` params. Server already imports `Scrinia.Core.Search`.
- Produces: REST-stored/appended memories carry Fields identically to MCP ones.

- [ ] **Step 1: Failing test** (adapt arrange to the file's pattern):

```csharp
    [Fact]
    public async Task StoreAsync_And_AppendAsync_CarryUnifiedFields()
    {
        // arrange store per existing pattern in this file
        await MemoryOrchestrator.StoreAsync(store,
            new StoreRequest(["status: active\nBody."], "server-fields", Keywords: ["person:Nick"]));

        var entry = store.LoadIndex("local").Single(e => e.Name == "server-fields");
        entry.Fields!["status"].Should().BeEquivalentTo(["active"]);
        entry.Fields["person"].Should().BeEquivalentTo(["Nick"]);
        entry.Keywords.Should().NotContain(k => k.StartsWith("person:"));

        await MemoryOrchestrator.AppendAsync(store, "server-fields", "status: done\nperson: Maria\nMore.");
        entry = store.LoadIndex("local").Single(e => e.Name == "server-fields");
        entry.Fields!["status"].Should().BeEquivalentTo(["done"]);
        entry.Fields["person"].Should().BeEquivalentTo(["Nick", "Maria"]);
    }
```

- [ ] **Step 2: Verify failure.**

- [ ] **Step 3: Implement** — mirror the MCP contract exactly:

`StoreAsync`: after `AnalyzeText` (~21), route — `var (routedFields, plainKeywords) = FieldExtractor.RouteKeywords(req.Keywords);`, use `plainKeywords` in `MergeKeywordsWithSource`; after the keyword-boost loop (~33):

```csharp
        var refFields = ExtractRefFieldsForServer(joined);
        var fields = KnownFields.Merge(KnownFields.Merge(routedFields, FieldExtractor.Extract(joined)), refFields);
        FieldExtractor.SeedTermFrequencies(fields, tf);
```

Add a private static twin of `ExtractRefFields` (same body as Task 7's — name it `ExtractRefFieldsForServer` locally OR promote the Task 7 helper: move `ExtractRefFields` to `FieldExtractor` as `public static Dictionary<string, string[]>? ExtractRefFields(string text)` and have both MCP and server call it — PREFERRED, do that: add it to `FieldExtractor` in this task, delete the MCP-local copy, update the MCP call sites). Pass `Fields: fields` on both entry constructions (~57, ~108).

`AppendAsync`: inside each branch (after `existingEph` ~188 / `existingEntry` ~215):

```csharp
            var incoming = KnownFields.Merge(
                FieldExtractor.Extract(content), FieldExtractor.ExtractRefFields(content));
            var mergedFields = KnownFields.Merge(existingEph?.Fields /* or existingEntry?.Fields */, incoming);
            FieldExtractor.SeedTermFrequencies(mergedFields, tf);
```

with `Fields: mergedFields` on the entry constructions (~194, ~229). (Server `AppendAsync` takes no keywords param — nothing to route.)

- [ ] **Step 4: Verify pass** — `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --nologo -v q` all green, plus full Scrinia.Tests (the promoted `ExtractRefFields` touched MCP call sites).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Services/MemoryOrchestrator.cs src/Scrinia.Core/Search/FieldExtractor.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/
git commit -m "feat(server): unified-fields parity in MemoryOrchestrator; promote ExtractRefFields to core"
```

---

### Task 11: Peripherals, docs, bench control

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgentPrompts.cs:169-233` — delete `TypedKeywordPrefixes` (`:176`), use `KnownFields.EntityKeys` in `ParseTypedKeywords` (`:200-233`)
- Modify: `src/Scrinia/Commands/ScriniaCommands.Memory.cs:156-195` — `list --summary` top-keywords aggregation: exclude `key:value`-shaped keywords from the raw display and add a fields-aware line (top field keys with counts)
- Modify: `src/Scrinia.Mcp/prompts/guide.md:52` (typed-keywords guidance) + the `keywords` `[Description]` on `Store` (`MemoryTools.Core.cs:115-120`) — describe fields as first-class, still expressed via `keywords`
- Modify: `docs/architecture/core.md` — `ArtifactEntry` listing (~97), scorer table (~190), replace/extend the ReferenceExtractor section (~247) with a Unified Fields section covering `KnownFields`/`FieldExtractor`/dual-read
- Test: existing suites + bench control run

- [ ] **Step 1: Make the edits.** Read each target region first; the contracts:
  - `AgentPrompts.ParseTypedKeywords` prefix test uses `KnownFields.EntityKeys.Contains(prefix)` instead of its own list (add project reference/using if the benchmarks project lacks `Scrinia.Core` — check `benchmarks/Scrinia.Retrieval.Benchmarks/*.csproj`; it references Core already for `TextAnalysis`).
  - CLI summary: skip keywords matching `^[A-Za-z][A-Za-z0-9_-]{0,29}:.+` in the top-keywords tally (legacy entries only now); append a `Top fields:` line aggregating `entry.Fields` keys by frequency when any entry carries fields.
  - `guide.md` + `Store` keyword description: "Typed entries (person:/place:/org:/thing:/topic:) and any other key:value keyword become structured fields — exact-match retrieval signal and entity-graph input. Plain keywords remain search keywords."
  - `docs/architecture/core.md`: document `Fields` on the models, the three scorer rows (50/40/12 + dual-read note), `KnownFields` table semantics, `FieldExtractor` sources (routing/extraction/refs), CodeRefs fed from the `file` field, forward-only migration.

- [ ] **Step 2: Full verification.**

Run: `dotnet build --nologo -v q` (whole solution incl. benchmarks) — clean.
Run both test suites — all green.

- [ ] **Step 3: Bench control run.** LoCoMo in the default configuration (invocation + pre-change baseline numbers are recorded in `docs/reviews/kompress-locomo/`; benchmark project `benchmarks/Scrinia.Retrieval.Benchmarks`). Expect R@5/R@10/NDCG within noise of baseline: bench ingest supplies typed keywords that now route to Fields, and the graph/scorer dual-read must keep scores equivalent. A regression means dual-read parity is broken — STOP and fix before commit (check `ParseEntityFields` vs `ParseTypedEntities` and the scorer slots first).

- [ ] **Step 4: Commit**

```bash
git add benchmarks/ src/Scrinia/Commands/ScriniaCommands.Memory.cs src/Scrinia.Mcp/prompts/guide.md src/Scrinia.Mcp/MemoryTools.Core.cs docs/architecture/core.md
git commit -m "feat: unified-fields peripherals — bench shared table, CLI summary, guide, architecture docs"
```

---

## Out of scope (explicitly)

- Per-chunk fields; a `fields` filter on `search`/`list` (storage supports it; v2 follow-on); backfill of old entries (forward-only; dual-read covers reads); the already-lossy copy/move fallback branches; forget-time cleanup of stale inbound refs (pre-existing gap, spec-noted); web UI (reads no keywords today).
