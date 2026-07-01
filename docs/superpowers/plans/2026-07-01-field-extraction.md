# Mechanical Field Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Mechanically extract `key: value` fields from the YAML-ish markdown agents write into memories, persist them on the index entry, and use them as transparent ranking signal (TF seeding + weighted-field-score slots).

**Architecture:** A new `FieldExtractor` (sibling of `ReferenceExtractor` in `Scrinia.Core.Search`) extracts fields at write time from frontmatter, line-start `key: value` pairs, and `**Bold:** label` lines. A `Fields` dictionary is added to `ArtifactEntry`/`EphemeralEntry` (same shape as `CodeRefs`). Both write paths (MCP `MemoryTools` and server `MemoryOrchestrator`) extract + seed; `WeightedFieldScorer.ScoreEntryTerm` gains three match slots. nmp/2 encoding is untouched.

**Tech Stack:** .NET 10 / C#, xUnit + FluentAssertions, source-generated regex (`GeneratedRegex`), source-generated JSON contexts.

**Spec:** `docs/superpowers/specs/2026-07-01-field-extraction-design.md`

## Global Constraints

- Values stored **verbatim** (trimmed, never tokenized); keys lowercased + trimmed.
- Caps: max **32** fields per entry, key regex `[A-Za-z][A-Za-z0-9_-]{0,29}` (bold-label keys additionally allow internal spaces), value ≤ **120** chars after trim.
- First occurrence wins within one text; **newer-wins** merge across appends.
- Nothing inside ``` code fences is extracted; values starting with `//` (URL remainders) are skipped.
- TF seeding boost is **+3** per token (between auto-keyword +2 and agent-keyword +5).
- Scorer slots: compound `key:value` exact = 50, value exact = 40, value contains = 12.
- Old index entries load with null `Fields` and must degrade gracefully (no NREs anywhere).
- Do NOT rely on a `Dictionary` string comparer surviving JSON round-trips — deserialized dictionaries have the default comparer. All field matching iterates pairs with explicit `StringComparison.OrdinalIgnoreCase`.
- Commit trailer for every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
- Branch setup (before Task 1): `git checkout main && git checkout -b feature/field-extraction`, then cherry-pick the spec/plan commits from `experiment/kompress-locomo` (they were authored there): `git cherry-pick fe23fff <plan-commit-sha>`. Do not carry over any other kompress-branch changes.
- Run tests with: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "<filter>" --nologo` (append `-v q` for terse output). Pre-existing CA1707 warnings in the test project are noise — ignore them.

---

### Task 1: `FieldExtractor` in `Scrinia.Core.Search`

**Files:**
- Create: `src/Scrinia.Core/Search/FieldExtractor.cs`
- Test: `tests/Scrinia.Tests/FieldExtractorTests.cs`

**Interfaces:**
- Consumes: `Scrinia.Core.Search.TextAnalysis.Tokenize(string) → IReadOnlyList<string>` (existing).
- Produces (later tasks call all three):
  - `public static Dictionary<string, string>? Extract(string? content)` — null when no fields found.
  - `public static Dictionary<string, string>? MergeNewerWins(Dictionary<string, string>? existing, Dictionary<string, string>? extracted)` — extracted values overwrite existing keys (case-insensitive); null when both empty. Result capped at 32 (existing-first ordering; new keys admitted until cap).
  - `public static void SeedTermFrequencies(Dictionary<string, string>? fields, Dictionary<string, int> tf)` — tokenizes each key and value via `TextAnalysis.Tokenize`, adds +3 per token occurrence into `tf`. No-op on null/empty fields.
  - `public const int MaxFields = 32;` and `public const int MaxValueLength = 120;`

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Tests/FieldExtractorTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Core.Search;

namespace Scrinia.Tests;

/// <summary>
/// Tests for <see cref="FieldExtractor"/> — mechanical key:value extraction from the
/// YAML-ish markdown agents write (frontmatter, line-start pairs, bold labels).
/// Values are verbatim; keys lowercased; code fences and URL remainders are skipped.
/// </summary>
public sealed class FieldExtractorTests
{
    [Fact]
    public void Extract_Frontmatter_YieldsLowercasedKeysAndVerbatimValues()
    {
        string content = "---\nStatus: Active\ntype: finding\n---\nBody prose here.";
        var fields = FieldExtractor.Extract(content);

        fields.Should().NotBeNull();
        fields.Should().Contain(new KeyValuePair<string, string>("status", "Active"),
            because: "keys lowercase, values verbatim");
        fields.Should().Contain(new KeyValuePair<string, string>("type", "finding"));
    }

    [Fact]
    public void Extract_LineStartPair_Extracted()
    {
        var fields = FieldExtractor.Extract("Some intro.\nverdict: no-go\nMore prose.");
        fields.Should().NotBeNull();
        fields!["verdict"].Should().Be("no-go", because: "hyphenated values survive verbatim");
    }

    [Fact]
    public void Extract_BoldLabel_Extracted()
    {
        var fields = FieldExtractor.Extract("**Why:** taxes retrieval at ingest");
        fields.Should().NotBeNull();
        fields!["why"].Should().Be("taxes retrieval at ingest");
    }

    [Fact]
    public void Extract_BoldLabel_ColonOutsideVariant_Extracted()
    {
        var fields = FieldExtractor.Extract("**Status**: active");
        fields.Should().NotBeNull();
        fields!["status"].Should().Be("active");
    }

    [Fact]
    public void Extract_InsideCodeFence_Skipped()
    {
        string content = "```\nstatus: fenced\n```\nverdict: real";
        var fields = FieldExtractor.Extract(content);
        fields.Should().NotBeNull();
        fields.Should().NotContainKey("status", because: "code-fence content is never extracted");
        fields!["verdict"].Should().Be("real");
    }

    [Fact]
    public void Extract_UrlLine_Skipped()
    {
        // "http://example.com" would parse as key "http", value "//example.com" — guarded.
        var fields = FieldExtractor.Extract("http://example.com\nhttps://example.org");
        fields.Should().BeNull();
    }

    [Fact]
    public void Extract_InvalidKeys_Skipped()
    {
        // Key must start with a letter and stay within charset/length; value must be non-empty.
        var fields = FieldExtractor.Extract(
            "1key: starts with digit\n" +
            "toolongkeytoolongkeytoolongkeyxx: over 30 chars\n" +
            "emptyval:\n" +
            "has.dot: dots not in charset\n" +
            "ok: fine");
        fields.Should().NotBeNull();
        fields.Should().HaveCount(1);
        fields!["ok"].Should().Be("fine");
    }

    [Fact]
    public void Extract_LongValue_Skipped()
    {
        string longVal = new string('x', 121);
        var fields = FieldExtractor.Extract($"key: {longVal}\nshort: ok");
        fields.Should().NotBeNull();
        fields.Should().NotContainKey("key", because: "values over 120 chars are noise, not fields");
        fields!["short"].Should().Be("ok");
    }

    [Fact]
    public void Extract_FirstOccurrenceWins_WithinOneText()
    {
        var fields = FieldExtractor.Extract("status: first\nstatus: second");
        fields!["status"].Should().Be("first");
    }

    [Fact]
    public void Extract_CapsAtMaxFields()
    {
        var lines = Enumerable.Range(1, 40).Select(i => $"key{i}: value{i}");
        var fields = FieldExtractor.Extract(string.Join("\n", lines));
        fields.Should().HaveCount(FieldExtractor.MaxFields);
    }

    [Fact]
    public void Extract_NoFields_ReturnsNull()
    {
        FieldExtractor.Extract("Just prose without any structure.").Should().BeNull();
        FieldExtractor.Extract("").Should().BeNull();
        FieldExtractor.Extract(null).Should().BeNull();
    }

    [Fact]
    public void MergeNewerWins_ExtractedOverwritesExisting_CaseInsensitive()
    {
        var existing = new Dictionary<string, string> { ["status"] = "active", ["type"] = "finding" };
        var extracted = new Dictionary<string, string> { ["STATUS"] = "done" };

        var merged = FieldExtractor.MergeNewerWins(existing, extracted);

        merged.Should().NotBeNull();
        merged!.Should().HaveCount(2);
        merged.Single(kv => kv.Key.Equals("status", StringComparison.OrdinalIgnoreCase))
            .Value.Should().Be("done", because: "append merge is newer-wins");
        merged["type"].Should().Be("finding", because: "untouched keys survive");
    }

    [Fact]
    public void MergeNewerWins_NullInputs()
    {
        FieldExtractor.MergeNewerWins(null, null).Should().BeNull();
        FieldExtractor.MergeNewerWins(new Dictionary<string, string> { ["a"] = "1" }, null)
            .Should().ContainKey("a");
        FieldExtractor.MergeNewerWins(null, new Dictionary<string, string> { ["b"] = "2" })
            .Should().ContainKey("b");
    }

    [Fact]
    public void SeedTermFrequencies_AddsPlus3PerTokenFromKeysAndValues()
    {
        var fields = new Dictionary<string, string> { ["verdict"] = "no-go decision" };
        var tf = new Dictionary<string, int> { ["decision"] = 1 };

        FieldExtractor.SeedTermFrequencies(fields, tf);

        tf["verdict"].Should().Be(3, because: "keys seed at +3");
        tf["decision"].Should().Be(4, because: "+3 stacks on the existing count");
        // "no-go" tokenizes to "go" ("no" is a stopword, "go" is 2 chars — both survive
        // only if TextAnalysis keeps them; assert on a token we know survives instead).
        tf.Should().ContainKey("verdict");
    }

    [Fact]
    public void SeedTermFrequencies_NullOrEmpty_NoOp()
    {
        var tf = new Dictionary<string, int>();
        FieldExtractor.SeedTermFrequencies(null, tf);
        FieldExtractor.SeedTermFrequencies(new Dictionary<string, string>(), tf);
        tf.Should().BeEmpty();
    }
}
```

Note on `SeedTermFrequencies_AddsPlus3PerTokenFromKeysAndValues`: before finalizing the assertion for `no-go`, check `TextAnalysis.Tokenize("no-go decision")` behavior in `src/Scrinia.Core/Search/TextAnalysis.cs` (stopword list membership for "no"/"go"). Assert only on tokens that actually survive tokenization; the shown assertions on `verdict`/`decision` are safe.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FieldExtractorTests" --nologo -v q`
Expected: compile FAILURE — `FieldExtractor` does not exist yet.

- [ ] **Step 3: Implement `FieldExtractor`**

Create `src/Scrinia.Core/Search/FieldExtractor.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Scrinia.Core.Search;

/// <summary>
/// Mechanically extracts <c>key: value</c> fields from the YAML-ish markdown agents write —
/// frontmatter blocks, line-start pairs, and <c>**Bold:** label</c> lines. LLM-free, regex-only,
/// run at write time (store/append); results live on <see cref="Models.ArtifactEntry.Fields"/>.
///
/// <para>Values are kept verbatim (trimmed, never tokenized) so exact-match signal survives for
/// content BM25 tokenization shreds: versions, dates, hyphenated identifiers, statuses. Keys are
/// lowercased. Conservative by design: nothing inside code fences, values that look like URL
/// remainders are skipped, tight key charset, value length cap, and a hard field cap.</para>
/// </summary>
public static partial class FieldExtractor
{
    public const int MaxFields = 32;
    public const int MaxValueLength = 120;

    // Line-start key: value. Key charset intentionally tight (letters first, then
    // letters/digits/underscore/hyphen, ≤30 chars) so prose colons don't match.
    [GeneratedRegex(@"^([A-Za-z][A-Za-z0-9_-]{0,29}):[ \t]+(\S.*)$")]
    private static partial Regex LineStartPairPattern();

    // Bold labels: **Why:** value  or  **Why**: value. Keys may contain internal spaces
    // ("How to apply") — normalized below.
    [GeneratedRegex(@"^\*\*([A-Za-z][A-Za-z0-9 _-]{0,29})(?::\*\*|\*\*:)[ \t]*(\S.*)$")]
    private static partial Regex BoldLabelPattern();

    /// <summary>
    /// Extracts fields from content. Returns null when none found (matching the nullable
    /// metadata convention on index entries). First occurrence of a key wins; capped at
    /// <see cref="MaxFields"/>.
    /// </summary>
    public static Dictionary<string, string>? Extract(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        Dictionary<string, string>? fields = null;
        bool inFence = false;
        bool inFrontmatter = false;
        bool firstLine = true;

        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');

            // Code-fence tracking — nothing inside fences is a field.
            if (line.StartsWith("```", StringComparison.Ordinal))
            {
                inFence = !inFence;
                firstLine = false;
                continue;
            }
            if (inFence) { firstLine = false; continue; }

            // Frontmatter tracking: a leading --- opens it; the next --- closes it.
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

            fields ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (fields.Count >= MaxFields) break;
            fields.TryAdd(key, value!); // first occurrence wins
        }

        return fields is { Count: > 0 } ? fields : null;
    }

    /// <summary>
    /// Merges append-extracted fields over existing ones — newer values win (a changed
    /// <c>status:</c> must update; first-wins would freeze stale values). Existing keys keep
    /// their position; the union is capped at <see cref="MaxFields"/>. Null when both empty.
    /// </summary>
    public static Dictionary<string, string>? MergeNewerWins(
        Dictionary<string, string>? existing,
        Dictionary<string, string>? extracted)
    {
        if (existing is not { Count: > 0 } && extracted is not { Count: > 0 })
            return null;

        var merged = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (existing is not null)
        {
            foreach (var kv in existing)
                merged[kv.Key] = kv.Value;
        }
        if (extracted is not null)
        {
            foreach (var kv in extracted)
            {
                if (merged.ContainsKey(kv.Key))
                    merged[kv.Key] = kv.Value;    // newer wins
                else if (merged.Count < MaxFields)
                    merged[kv.Key] = kv.Value;    // new key, room left
            }
        }
        return merged;
    }

    /// <summary>
    /// Seeds field keys and value tokens into a term-frequency dictionary at +3 per token —
    /// between the auto-keyword (+2) and agent-keyword (+5) boosts — so BM25 picks up field
    /// vocabulary transparently.
    /// </summary>
    public static void SeedTermFrequencies(
        Dictionary<string, string>? fields,
        Dictionary<string, int> tf)
    {
        if (fields is not { Count: > 0 })
            return;

        foreach (var kv in fields)
        {
            foreach (string token in TextAnalysis.Tokenize(kv.Key))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + 3;
            }
            foreach (string token in TextAnalysis.Tokenize(kv.Value))
            {
                tf.TryGetValue(token, out int count);
                tf[token] = count + 3;
            }
        }
    }

    /// <summary>Matches one line against the field patterns; (null, null) when no field.</summary>
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
        // URL remainder guard: "http://x" parses as key "http", value "//x" — not a field.
        if (value.StartsWith("//", StringComparison.Ordinal))
            return (null, null);

        string key = m.Groups[1].Value.Trim().ToLowerInvariant();
        return (key, value);
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~FieldExtractorTests" --nologo -v q`
Expected: PASS (all tests). If the `SeedTermFrequencies` test fails on a tokenization assumption, fix the test assertion to match actual `TextAnalysis.Tokenize` output (the implementation contract is "+3 per surviving token", not a specific token list).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/FieldExtractor.cs tests/Scrinia.Tests/FieldExtractorTests.cs
git commit -m "feat(core): FieldExtractor — mechanical key:value extraction from memory content

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 2: `Fields` on the index models + round-trip

**Files:**
- Modify: `src/Scrinia.Core/Models/ArtifactEntry.cs`
- Modify: `src/Scrinia.Core/Models/EphemeralEntry.cs`
- Test: `tests/Scrinia.Tests/FieldExtractorTests.cs` (add one round-trip test — small enough to co-locate)

**Interfaces:**
- Produces: `ArtifactEntry.Fields` / `EphemeralEntry.Fields` — `Dictionary<string, string>?`, last positional parameter, default null. Later tasks pass `Fields:` by name.
- Note: `FileStoreJsonContext` is source-generated from the type shape — the new property serializes automatically, no context change needed. Record `with`-copies (copy/move paths) carry `Fields` automatically. Explicit reconstruction sites that already drop `Keywords`/`TermFrequencies` (e.g. the lossy fallback branches in `FileMemoryStore.CopyMemory`) may also drop `Fields` — that is consistent with existing behavior and NOT in scope to change.

- [ ] **Step 1: Write the failing round-trip test**

Add to `tests/Scrinia.Tests/FieldExtractorTests.cs`:

```csharp
    [Fact]
    public void ArtifactEntry_Fields_RoundTripsThroughIndexSaveLoad()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);

        var entry = new Scrinia.Core.Models.ArtifactEntry(
            Name: "fields-roundtrip",
            Uri: "file:///dev/null",
            OriginalBytes: 10,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "test",
            Fields: new Dictionary<string, string> { ["status"] = "Active", ["verdict"] = "no-go" });

        store.Upsert(entry, "local");
        var reloaded = store.LoadIndex("local").Single(e => e.Name == "fields-roundtrip");

        reloaded.Fields.Should().NotBeNull();
        reloaded.Fields!["status"].Should().Be("Active", because: "values are verbatim through JSON");
        reloaded.Fields["verdict"].Should().Be("no-go");
    }
```

Verified surfaces: `TestHelpers.StoreScope` exposes `WorkspaceDir` (`tests/Scrinia.Tests/TestHelpers.cs:86-92`); `FileMemoryStore(string workspaceRoot, RankerOptions? rankerOptions = null)` (`src/Scrinia.Core/FileMemoryStore.cs:180`).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ArtifactEntry_Fields_RoundTrips" --nologo -v q`
Expected: compile FAILURE — no `Fields` parameter.

- [ ] **Step 3: Add the parameter to both records**

In `src/Scrinia.Core/Models/ArtifactEntry.cs`, after `RetrievalAnchors`:

```csharp
    string[]? RetrievalAnchors = null,
    /// <summary>
    /// Mechanically extracted key:value fields from the content (frontmatter, line-start
    /// pairs, bold labels — see Search.FieldExtractor). Values verbatim, keys lowercase.
    /// Ranking signal today; queryable surface later. Null for pre-fields entries.
    /// </summary>
    Dictionary<string, string>? Fields = null);
```

Same addition (same doc comment) at the end of `src/Scrinia.Core/Models/EphemeralEntry.cs` after its `RetrievalAnchors`.

- [ ] **Step 4: Build the full solution and run the test**

Run: `dotnet build --nologo -v q` then the Step 2 test command.
Expected: build clean (positional-construction sites all use optional params — verify no CS errors), test PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Models/ArtifactEntry.cs src/Scrinia.Core/Models/EphemeralEntry.cs tests/Scrinia.Tests/FieldExtractorTests.cs
git commit -m "feat(core): Fields dictionary on ArtifactEntry/EphemeralEntry

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 3: MCP Store path — extract, seed, persist

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (the `Store` method, ~lines 128–356)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes: `FieldExtractor.Extract`, `FieldExtractor.SeedTermFrequencies` (Task 1); `ArtifactEntry.Fields` / `EphemeralEntry.Fields` (Task 2).
- Produces: every memory stored via the MCP `Store` path carries extracted `Fields` and field-seeded TF. Task 4 relies on `entry.Fields` being populated by Store.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`, next to `Store_WithAnchors_PersistsAnchors_SeedsTf_AndSurfacesViaSearch` (mirror its harness usage exactly — `TestHelpers.StoreScope`, `Tools()`, `ScriniaArtifactStore.LoadIndex()`):

```csharp
    [Fact]
    public async Task Store_ExtractsFields_SeedsTf_AndSurfacesViaSearch()
    {
        // End-to-end check of the mechanical field-extraction flow:
        //   1. Store() extracts frontmatter/labeled-line fields from content
        //   2. ArtifactEntry.Fields carries them (keys lowercase, values verbatim)
        //   3. Field tokens are TF-seeded at +3 so BM25 picks up field vocabulary
        //   4. A query using a field VALUE surfaces the entry
        using var scope = new TestHelpers.StoreScope();

        string content = "---\nStatus: quiescent\n---\n**Verdict:** rejected\nThe subsystem review notes.";
        await Tools().Store([content], "field-flow-test", description: "field extraction notes");

        // (1) + (2): Fields persisted, key lowercased, value verbatim.
        var entries = ScriniaArtifactStore.LoadIndex();
        var entry = entries.Single(e => e.Name == "field-flow-test");
        entry.Fields.Should().NotBeNull();
        entry.Fields!["status"].Should().Be("quiescent");
        entry.Fields["verdict"].Should().Be("rejected");

        // (3): field tokens seeded at +3 (baseline: "quiescent" appears once in content
        // as frontmatter is part of the stored text — assert ≥ 3 rather than an exact count).
        entry.TermFrequencies.Should().NotBeNull();
        entry.TermFrequencies!.Should().ContainKey("quiescent");
        entry.TermFrequencies["quiescent"].Should().BeGreaterThanOrEqualTo(3,
            because: "field key/value tokens must be TF-seeded at +3");

        // (4): querying by field value surfaces the entry.
        string result = await Tools().Search("quiescent");
        var parsed = ResponseParser.Parse(result);
        parsed.Status.Should().Be("success");
        parsed.Content.Should().Contain("field-flow-test");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Store_ExtractsFields" --nologo -v q`
Expected: FAIL — `entry.Fields` is null (extraction not wired).

- [ ] **Step 3: Wire extraction into `Store`**

In `src/Scrinia.Mcp/MemoryTools.Core.cs`, inside `Store`, directly after the anchor-seeding block (after the `cleanedAnchors` block ending ~line 177):

```csharp
        // Mechanically extract key:value fields from the content (frontmatter, line-start
        // pairs, bold labels) and seed their tokens into TF at +3 — between auto-keyword
        // (+2) and agent-keyword (+5). Fields land on the entry for field-score matching.
        var fields = FieldExtractor.Extract(joined);
        FieldExtractor.SeedTermFrequencies(fields, tf);
```

Then pass `Fields: fields` to both constructions in this method:
- the `EphemeralEntry` at ~line 204 (after `RetrievalAnchors: cleanedAnchors`)
- the `ArtifactEntry` at ~line 329 (after `RetrievalAnchors: cleanedAnchors`)

Add `using Scrinia.Core.Search;` if not already imported (check the file head — `TextAnalysis` is already used, so the namespace is likely imported).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Store_ExtractsFields" --nologo -v q`
Expected: PASS.
Then run the full suite to catch regressions (existing Store tests assert on TF/keywords):
`dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --nologo -v q`
Expected: all PASS. If an existing test asserts an exact TF count that field seeding shifted, inspect whether the test content genuinely contains a field-shaped line; adjust the test's content (preferred: make its content field-free) rather than weakening its assertion.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "feat(mcp): extract + persist content fields on store; seed TF at +3

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 4: MCP Append path — newer-wins merge

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (the `Append` method + `BuildAppendMetadata`, ~lines 1027–1242)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes: `FieldExtractor.Extract`, `FieldExtractor.MergeNewerWins`, `FieldExtractor.SeedTermFrequencies`; `Fields` populated by Store (Task 3).
- Produces: `BuildAppendMetadata` returns a 6-tuple — signature becomes:
  `(string Description, string[]? Tags, string[]? Keywords, Dictionary<string, int>? Tf, string[]? Anchors, Dictionary<string, string>? Fields)`
  with two new parameters: `string appendedContent` and `Dictionary<string, string>? existingFields`.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`:

```csharp
    [Fact]
    public async Task Append_MergesFields_NewerWins_PreservingUntouchedKeys()
    {
        using var scope = new TestHelpers.StoreScope();

        await Tools().Store(["status: active\ntype: finding\nInitial notes."], "field-append-test");
        await Tools().Append("status: done\nFollow-up notes.", "field-append-test");

        var entry = ScriniaArtifactStore.LoadIndex().Single(e => e.Name == "field-append-test");
        entry.Fields.Should().NotBeNull();
        entry.Fields!["status"].Should().Be("done",
            because: "append field merge is newer-wins — a changed status must update");
        entry.Fields["type"].Should().Be("finding",
            because: "keys untouched by the appended chunk survive");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append_MergesFields" --nologo -v q`
Expected: FAIL — `entry.Fields["status"]` is `"active"` or Fields is stale (append rebuilds the entry without fields merge).

- [ ] **Step 3: Implement the merge in `BuildAppendMetadata` and thread it through**

In `BuildAppendMetadata` (~line 1182): add parameters `string appendedContent` and `Dictionary<string, string>? existingFields`; extend the return tuple with `Dictionary<string, string>? Fields`. After the anchor-merge block, add:

```csharp
        // Fields: extract from the appended chunk only and merge newer-wins over the
        // entry's existing fields (a changed status: must update — first-wins would
        // freeze stale values). Seed merged field tokens into TF at +3, mirroring Store.
        var mergedFields = FieldExtractor.MergeNewerWins(
            existingFields, FieldExtractor.Extract(appendedContent));
        FieldExtractor.SeedTermFrequencies(mergedFields, tf);
```

and include `mergedFields` in the return tuple.

In `Append`, update both `BuildAppendMetadata` call sites:
- ephemeral (~line 1091): pass `content` and `existingEph?.Fields`; set `Fields: meta.Fields` on the `EphemeralEntry` (~line 1100).
- persistent (~line 1127): pass `content` and `existingEntry?.Fields`; set `Fields: meta.Fields` on the `ArtifactEntry` (~line 1145).

Note: the TF dict inside `BuildAppendMetadata` is recomputed from the full post-append text, and `SeedTermFrequencies(mergedFields, …)` seeds ALL merged fields (not just the new chunk's) — this is correct: TF is rebuilt from scratch on every append, so carried-forward fields must re-seed each time (same reason merged anchors re-seed).

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Append_MergesFields" --nologo -v q` — expected PASS.
Then full suite: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --nologo -v q` — expected all PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -m "feat(mcp): newer-wins field merge on append

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 5: Scorer slots

**Files:**
- Modify: `src/Scrinia.Core/Search/WeightedFieldScorer.cs` (`ScoreEntryTerm`, ~line 322; doc-comment score table, ~line 14)
- Test: `tests/Scrinia.Tests/FieldExtractorTests.cs` (scoring exercised through the public `FileMemoryStore.SearchAll` — `WeightedFieldScorer` is internal to Core and Core has no InternalsVisibleTo)

**Interfaces:**
- Consumes: `ArtifactEntry.Fields` (Task 2).
- Produces: three new match slots in entry term scoring — compound `key:value` pair exact = 50, field value exact = 40, field value contains = 12. All comparisons explicit `StringComparison.OrdinalIgnoreCase` (never rely on the dictionary comparer — it does not survive JSON deserialization).

- [ ] **Step 1: Write the failing tests**

Add to `tests/Scrinia.Tests/FieldExtractorTests.cs`. Ranking is asserted relatively: two entries identical except one carries the matching field must rank field-carrier first. Query terms pass through `ScoreEntry`'s whitespace split intact, so `status:quiescent` reaches `ScoreEntryTerm` as one term (colons are not split — the same mechanism `topic:subject` tag queries rely on).

```csharp
    private static Scrinia.Core.Models.ArtifactEntry MakeEntry(
        string name, Dictionary<string, string>? fields) =>
        new(
            Name: name,
            Uri: "file:///dev/null",
            OriginalBytes: 10,
            ChunkCount: 1,
            CreatedAt: DateTimeOffset.UtcNow,
            Description: "unrelated description",
            Fields: fields);

    [Fact]
    public void SearchAll_FieldValueExact_OutranksFieldlessEntry()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        store.Upsert(MakeEntry("with-field",
            new Dictionary<string, string> { ["verdict"] = "quiescent" }), "local");
        store.Upsert(MakeEntry("without-field", null), "local");

        var results = store.SearchAll("quiescent", scopes: null, limit: 10);

        var first = results.OfType<Scrinia.Core.Search.EntryResult>().First();
        first.Item.Entry.Name.Should().Be("with-field",
            because: "a field-value exact match (40) must outrank an entry with no match");
    }

    [Fact]
    public void SearchAll_CompoundKeyValueTerm_OutranksValueOnlyMatch()
    {
        using var scope = new TestHelpers.StoreScope();
        var store = new Scrinia.Core.FileMemoryStore(scope.WorkspaceDir);
        // Same value under different keys: the compound query "verdict:quiescent" must
        // prefer the entry whose KEY also matches (pair exact 50 > value exact 40).
        store.Upsert(MakeEntry("right-key",
            new Dictionary<string, string> { ["verdict"] = "quiescent" }), "local");
        store.Upsert(MakeEntry("wrong-key",
            new Dictionary<string, string> { ["mood"] = "quiescent" }), "local");

        var results = store.SearchAll("verdict:quiescent", scopes: null, limit: 10);

        var first = results.OfType<Scrinia.Core.Search.EntryResult>().First();
        first.Item.Entry.Name.Should().Be("right-key",
            because: "compound key:value exact (50) must outrank value-only exact (40)");
    }
```

Verified surfaces: `IMemoryStore.SearchAll(string query, string? scopes = null, int limit = 20)` (`src/Scrinia.Core/IMemoryStore.cs:27`); `EntryResult(ScopedArtifact Item, double Score)` lives in `Scrinia.Core.Search` (`src/Scrinia.Core/Search/SearchResults.cs:9`).

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~SearchAll_FieldValue|FullyQualifiedName~SearchAll_CompoundKeyValue" --nologo -v q`
Expected: FAIL — without scorer slots, neither entry gets field-derived score (first test may find no results at all; both must fail before wiring).

- [ ] **Step 3: Add the slots to `ScoreEntryTerm`**

In `src/Scrinia.Core/Search/WeightedFieldScorer.cs`, inside `ScoreEntryTerm`, after the anchor block (~line 380) and before the description check:

```csharp
        // Field matching — mechanically extracted key:value pairs (see FieldExtractor).
        // Compound "key:value" query term hitting a pair exactly scores 50 (tag-exact
        // parity); a term equal to a field value scores 40 (keyword-exact parity);
        // contained in a value, 12. Comparisons are explicit OrdinalIgnoreCase — the
        // dictionary's comparer does not survive JSON round-trips.
        if (entry.Fields is { Count: > 0 })
        {
            int sep = term.IndexOf(':', StringComparison.Ordinal);
            string? termKey = sep > 0 && sep < term.Length - 1 ? term[..sep] : null;
            string? termValue = termKey is not null ? term[(sep + 1)..] : null;

            foreach (var field in entry.Fields)
            {
                if (termKey is not null
                    && field.Key.Equals(termKey, StringComparison.OrdinalIgnoreCase)
                    && field.Value.Equals(termValue, StringComparison.OrdinalIgnoreCase))
                {
                    score = Math.Max(score, 50);
                    break;
                }

                if (field.Value.Equals(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 40);
                else if (field.Value.Contains(term, StringComparison.OrdinalIgnoreCase))
                    score = Math.Max(score, 12);
            }
        }
```

Update the class doc-comment score table (~line 14) — insert into the entry-scoring table in weight order:

```
/// Field key:value pair exact     50
/// Field value exact match        40
/// Field value contains           12
```

- [ ] **Step 4: Run tests**

Run the Step 2 filter — expected PASS. Then full suite: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --nologo -v q` — expected all PASS (existing ranking tests must not shift: entries without Fields take the `is { Count: > 0 }` early skip).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/WeightedFieldScorer.cs tests/Scrinia.Tests/FieldExtractorTests.cs
git commit -m "feat(core): field-match slots in weighted scorer (pair 50 / exact 40 / contains 12)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 6: Server orchestrator parity

**Files:**
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs` (`StoreAsync` ~lines 15–137, `AppendAsync` ~lines 139–259)
- Test: `tests/Scrinia.Server.Tests/` — add to the existing orchestrator/endpoint test file (locate with `grep -l "StoreAsync" tests/Scrinia.Server.Tests/*.cs`; follow its store-construction pattern)

**Interfaces:**
- Consumes: `FieldExtractor` (Task 1), `Fields` params (Task 2).
- Produces: REST-stored memories carry Fields identically to MCP-stored ones. The server already imports `Scrinia.Core.Search`.

- [ ] **Step 1: Write the failing test**

In the located server test file, following its existing pattern for calling `MemoryOrchestrator.StoreAsync` with an `IMemoryStore` (the tests construct a `FileMemoryStore` over a temp dir):

```csharp
    [Fact]
    public async Task StoreAsync_ExtractsFields_AndAppendMergesNewerWins()
    {
        // (arrange a store per the file's existing pattern)
        await MemoryOrchestrator.StoreAsync(store,
            new StoreRequest(["status: active\ntype: finding\nBody."], "server-fields-test"));

        var entry = store.LoadIndex("local").Single(e => e.Name == "server-fields-test");
        entry.Fields.Should().NotBeNull();
        entry.Fields!["status"].Should().Be("active");

        await MemoryOrchestrator.AppendAsync(store, "server-fields-test", "status: done\nMore.");

        entry = store.LoadIndex("local").Single(e => e.Name == "server-fields-test");
        entry.Fields!["status"].Should().Be("done", because: "server append merge is newer-wins");
        entry.Fields["type"].Should().Be("finding");
    }
```

(Adapt `StoreRequest` construction to its actual signature — check `src/Scrinia.Server/Models/` for the request DTO shape.)

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter "FullyQualifiedName~StoreAsync_ExtractsFields" --nologo -v q`
Expected: FAIL — Fields null.

- [ ] **Step 3: Wire extraction into both methods**

In `StoreAsync`, after the keyword-boost loop (~line 33):

```csharp
        // Mechanically extract key:value fields and seed their tokens at +3 —
        // same contract as the MCP store path (MemoryTools.Store).
        var fields = FieldExtractor.Extract(joined);
        FieldExtractor.SeedTermFrequencies(fields, tf);
```

Pass `Fields: fields` on both the `EphemeralEntry` (~line 57) and `ArtifactEntry` (~line 108) constructions.

In `AppendAsync`, after the full-text TF rebuild (~line 171), resolve the existing fields per path and merge. The existing entry lookups happen inside the ephemeral/persistent branches, so place the merge inside each branch before entry construction:

Ephemeral branch (after `existingEph` is loaded, ~line 188):
```csharp
            var mergedFields = FieldExtractor.MergeNewerWins(
                existingEph?.Fields, FieldExtractor.Extract(content));
            FieldExtractor.SeedTermFrequencies(mergedFields, tf);
```
and `Fields: mergedFields` on the `EphemeralEntry` (~line 194).

Persistent branch (after `existingEntry` is loaded, ~line 215):
```csharp
            var mergedFields = FieldExtractor.MergeNewerWins(
                existingEntry?.Fields, FieldExtractor.Extract(content));
            FieldExtractor.SeedTermFrequencies(mergedFields, tf);
```
and `Fields: mergedFields` on the `ArtifactEntry` (~line 229).

(The shared `tf` is branch-local by the time entries are built — seeding inside each branch keeps the one `tf` dict correct because only one branch runs.)

- [ ] **Step 4: Run tests**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --nologo -v q`
Expected: all PASS (new test + existing 64+ server tests).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Server/Services/MemoryOrchestrator.cs tests/Scrinia.Server.Tests/
git commit -m "feat(server): field extraction parity in MemoryOrchestrator store/append

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

### Task 7: Docs + bench sanity

**Files:**
- Modify: `docs/architecture/core.md` (ArtifactEntry model listing ~line 97; score table ~line 190; add a `FieldExtractor` paragraph next to the ReferenceExtractor section ~line 247)
- No product code changes.

**Interfaces:** none — documentation and verification only.

- [ ] **Step 1: Update `docs/architecture/core.md`**

Three edits:
1. Add `Dictionary<string, string>? Fields = null); // mechanical key:value extraction` to the `ArtifactEntry` listing (and mention on `EphemeralEntry`).
2. Add rows to the Weighted Field Scoring table:
   `| Field key:value pair exact | 50 |`, `| Field value exact | 40 |`, `| Field value contains | 12 |`.
3. Add a short section after ReferenceExtractor:

```markdown
### FieldExtractor

`FieldExtractor` in `Scrinia.Core.Search` mechanically extracts `key: value` fields from
memory content at write time — YAML frontmatter lines, line-start `key: value` pairs, and
`**Bold:** label` lines. Code fences and URL remainders are skipped; keys are lowercased,
values kept verbatim (max 32 fields, values ≤ 120 chars). Fields are stored on
`ArtifactEntry.Fields`/`EphemeralEntry.Fields`, seeded into term frequencies at +3, and
scored by `WeightedFieldScorer` (pair exact 50 / value exact 40 / value contains 12).
Appends merge newer-wins. Pre-fields entries have null `Fields` and degrade gracefully.
```

- [ ] **Step 2: Full build + full test suites**

Run: `dotnet build --nologo -v q` — expected clean.
Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --nologo -v q` and `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --nologo -v q` — expected all PASS.

- [ ] **Step 3: Bench sanity run (regression control)**

The benchmark harness ingests via its own retriever, so bench entries carry no extracted fields unless dialog text happens to contain field-shaped lines; this run is a cheap no-regression control, not a lift measurement. From the repo root, run the LoCoMo benchmark in its default configuration (see `benchmarks/Scrinia.Retrieval.Benchmarks/` — the kompress experiment docs in `docs/reviews/kompress-locomo/` record the exact invocation and the pre-change baseline numbers). Compare R@5/R@10/NDCG against that documented baseline.
Expected: identical or within noise. If numbers regress, STOP and investigate `ScoreEntryTerm` — a regression means fielded entries are being scored when `Fields` should be null.

- [ ] **Step 4: Commit**

```bash
git add docs/architecture/core.md
git commit -m "docs: field extraction in core architecture (model, scorer table, FieldExtractor)

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Out of scope (explicitly)

- Per-chunk fields; a `fields` filter on `search`/`list` (v2 — storage already supports it); backfill of pre-existing entries (forward-only per spec); the lossy copy/move fallback branches that already drop Keywords/TF; `UpdateMeta` field editing (fields are auto-extracted, not agent-curated, in v1).
