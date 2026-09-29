# Attributes Model — Plan 3: Tool Surface Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the agent-facing memory tools their new shape — `remember(path, content, attributes?, keywords?)`, `append(path, content, attributes?, keywords?)`, `search(..., attributeFilter?)`, and attribute-based display — where `attributes` is a `Dictionary<string,string[]>` that flows into the existing `Fields` substrate and drives the `review`/`file` behaviors.

**Architecture:** The internal `Fields` model (`Dictionary<string,string[]>` on `ArtifactEntry`) already **is** the attributes substrate, and `KnownFields.Merge` + `FieldExtractor.SeedTermFrequencies` + the `fields["file"]` code-ref hashing already exist. So this plan is mostly **surface plumbing**: the internal `Store`/`Append` gain an *additive* `attributes` parameter that merges into `Fields` and derives review/file behavior; the public `[McpServerTool]` methods drop the six legacy params (`description`, `tags`, `anchors`, `reviewAfter`, `reviewWhen`, `codeRefs`) and expose only `attributes` + `keywords`; `search` swaps its `tags` AND-filter for an attribute filter; and the display swaps the tags line/column for attributes. The `Tags`/`RetrievalAnchors`/`Description`/`ReviewAfter`/`ReviewWhen`/`CodeRefs` **model fields and internal `Store` params are kept** as the dual-read path — removing them is the cleanup plan.

**Tech Stack:** C# / .NET 10, ModelContextProtocol 1.0.0, xUnit + FluentAssertions (`tests/Scrinia.Tests`).

## Global Constraints

- This feature is **approved product surgery** on `Scrinia.Core` + `Scrinia.Mcp`. Do NOT touch the user's bench WIP files (`.gitignore`, `benchmarks/.../AgenticIngestPrompts.cs`, `LlmAgentHarness.cs`, `ScriniaRetriever.cs`, `OpenAiCompatibleBenchLlmClient.cs`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `benchmarks/.../tools/`, `package.json`).
- **Additive internals, surgical surface.** The internal `Store`/`Append`/`BuildAppendMetadata` methods only *gain* an `attributes` parameter — they keep `description`/`tags`/`anchors`/`reviewAfter`/`reviewWhen`/`codeRefs` so existing internal/test callers still compile and the `Tags`/`Anchors`/`Description`/`Review*`/`CodeRefs` model fields stay populated for dual-read. Removing those params/fields and their scoring is the **cleanup plan (Plan 6)**, not this one.
- **Only the agent-facing `[McpServerTool]` methods lose params.** `MemoryRemember`, `MemoryAppend`, `MemorySearch` get their new signatures; the C# method names are unchanged; the tool `Name = "memory_*"` strings are unchanged (the rename is Plan 4).
- **`UpdateMeta` is out of scope** — it is internal, not an MCP tool, and keeps its `description`/`reviewAfter`/`reviewWhen` params. Do not touch it.
- **No new behavior on existing data.** A memory written the old way must still read, list, and search identically (the model fields it carries are untouched). New writes simply route through `attributes`.
- Commit trailer, exactly:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
  then `Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g`
- Never push. `TreatWarningsAsErrors` is on for `Scrinia.Core`/`Scrinia.Mcp` (CA1859 etc.) — build clean. Branch off `main` before Task 1.

## File Structure

- `src/Scrinia.Mcp/MemoryTools.Core.cs` — MODIFY: `Store` (+`attributes` param, merge into `fields`, derive review from `fields["review"]`), `Append`/`BuildAppendMetadata` (+`attributes`), `Search` (`tags`→`attributeFilter`, `MatchesAttributes`, drop `ParseTagFilter`/`MatchesTags`), `AppendResultBlock`/`+AppendAttributesLine`, `BuildSummary` (attributes aggregation). Add `NormalizeAttributes` helper.
- `src/Scrinia.Mcp/MemoryTools.cs` — MODIFY: `MemoryRemember`/`MemoryAppend`/`MemorySearch` signatures + `[Description]` strings; `MemoryList` `[Description]` refresh.
- `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` — EXTEND/EDIT: new attribute round-trip + filter + display tests; rewrite the 3 tests that assert the old `tags:`/`anchors:` search lines or use `MemorySearch(tags:)`.

Read the existing method/test bodies before editing and mirror their construction helpers. Test files are flat in `tests/Scrinia.Tests/`.

**Test harness (AUTHORITATIVE — overrides the `(tools, store)` shorthand in the sketches below):** `ScriniaMcpToolsTests` has `private static ScriniaMcpTools Tools() => new();` — it returns ONLY the tools; there is **no `(tools, store)` tuple** and no `MakeStore`/`CreateWorkspaceFile` helper. The store is ambient (`ScriniaArtifactStore`, configured by the test base). So, everywhere a sketch writes `var (tools, store) = Tools();` … `store.LoadIndex("local")`, instead write `await Tools().MemoryRemember(...)` / `Tools().Store(...)` / `Tools().MemorySearch(...)` and read the persisted index back via the static **`ScriniaArtifactStore.LoadIndex()`** (default local scope). Mirror these existing tests exactly:
- write + index readback → `Store_WithTags_StoresTagsInIndex` (~L306–313), `Store_AutoDescription_UsesFirst200Chars` (~L283–285);
- search-output assertions → `Search_SurfacesAnchorsAndTagsWhenPresent` (~L2000);
- the `file` code-ref test → `tests/Scrinia.Tests/ReferenceTests.cs::Store_WithCodeRefs_RecordsHashes` for the real workspace-file setup (there is no `CreateWorkspaceFile`).

---

### Task 1: `attributes` into the `Store` write path

Add an additive `attributes` parameter to the internal `Store`, merge it into `Fields` (so it seeds BM25, scores via the weight-scaled Fields block, and feeds the graph — all already wired), and derive the `review` behavior from the `review` attribute when the legacy review params are absent. The `file` code-ref hashing already reads `fields["file"]`, so it needs no change once `attributes.file` lands in `fields`.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`Store`, ~L106–368; add `NormalizeAttributes` helper)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (add to the existing class)

**Interfaces:**
- Consumes: `KnownFields.Merge` (`Dictionary<string,string[]>?, Dictionary<string,string[]>? → Dictionary<string,string[]>?`), `KnownFields.ReviewKey` (`"review"`), `FieldExtractor.SeedTermFrequencies`.
- Produces: `Store(..., IReadOnlyDictionary<string,string[]>? attributes = null, string actionLabel = "stored", CancellationToken ct)` — attributes merge into the entry's `Fields`; `review` attribute values that parse as dates set `ReviewAfter`, the rest set `ReviewWhen`; `file` attribute values are hashed into `CodeRefs`.

- [ ] **Step 1: Write the failing tests**

Read `ScriniaMcpToolsTests.cs` first for its `Tools()` / store-construction helpers, then add (mirroring the existing `Store_With…` tests that call the internal `Store` and read back via `store.LoadIndex(scope)`):

```csharp
[Fact]
public async Task Store_Attributes_LandInFields()
{
    var (tools, store) = Tools();   // use this file's existing construction helper
    await tools.Store(["Acme signed the renewal."], "acme-note",
        attributes: new Dictionary<string, string[]>
        {
            ["person"] = ["maria"],
            ["org"] = ["acme"],
            ["status"] = ["done"],   // arbitrary key — must land too
        });

    var entry = store.LoadIndex("local").First(e => e.Name == "acme-note");
    entry.Fields.Should().ContainKey("person");
    entry.Fields!["person"].Should().Contain("maria");
    entry.Fields.Should().ContainKey("status");
    entry.Fields!["status"].Should().Contain("done");
}

[Fact]
public async Task Store_ReviewAttribute_DateSetsReviewAfter_TextSetsReviewWhen()
{
    var (tools, store) = Tools();
    await tools.Store(["note"], "rev-note",
        attributes: new Dictionary<string, string[]>
        {
            ["review"] = ["2026-08-01", "when the auth system changes"],
        });

    var entry = store.LoadIndex("local").First(e => e.Name == "rev-note");
    entry.ReviewAfter.Should().NotBeNull();
    entry.ReviewAfter!.Value.Year.Should().Be(2026);
    entry.ReviewWhen.Should().Be("when the auth system changes");
}

[Fact]
public async Task Store_FileAttribute_RecordsCodeRefHash()
{
    var (tools, store) = Tools();
    // Reuse this file's pattern for a real workspace-relative file if one exists; otherwise
    // point at a file that exists under the store's workspace root (see ReferenceTests.cs
    // Store_WithCodeRefs_RecordsHashes for the workspace-file setup helper to mirror).
    string relPath = CreateWorkspaceFile(store, "src/Probe.cs", "class Probe {}");
    await tools.Store(["depends on probe"], "file-note",
        attributes: new Dictionary<string, string[]> { ["file"] = [relPath] });

    var entry = store.LoadIndex("local").First(e => e.Name == "file-note");
    entry.CodeRefs.Should().NotBeNull();
    entry.CodeRefs!.Keys.Should().Contain(relPath);
}
```

If the file lacks a `(tools, store)` tuple helper or `CreateWorkspaceFile`, mirror the exact store/workspace setup the neighbouring `Store_*` / `ReferenceTests` tests use — do not invent a new harness.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Store_Attributes_LandInFields|FullyQualifiedName~Store_ReviewAttribute|FullyQualifiedName~Store_FileAttribute"`
Expected: FAIL to compile — `Store` has no `attributes` parameter (CS1739).

- [ ] **Step 3: Add the `attributes` parameter and merge logic**

In `Store`, add the parameter immediately before `actionLabel` (keeps positional callers — which never pass `actionLabel` positionally — intact):

```csharp
        [Description("Optional file paths this memory depends on. Hashes are recorded to detect drift.")] string[]? codeRefs = null,
        IReadOnlyDictionary<string, string[]>? attributes = null,
        string actionLabel = "stored",
        CancellationToken cancellationToken = default)
```

Merge attributes into `fields` right after the existing `fields` assembly (currently ~L187), so attributes seed TF and reach scoring/graph like any field. Explicit agent attributes win scalar collisions (merged last):

```csharp
        // Assemble unified fields: routed keywords ∪ content extraction ∪ references,
        // then fold in explicit agent attributes (they win scalar collisions), then seed
        // key/value tokens into TF.
        var fields = KnownFields.Merge(KnownFields.Merge(routedFields, FieldExtractor.Extract(joined)), refFields);
        fields = KnownFields.Merge(fields, NormalizeAttributes(attributes));
        FieldExtractor.SeedTermFrequencies(fields, tf);
```

Derive review behavior from the `review` attribute when the legacy params weren't supplied. Insert this **before** the existing `parsedReviewAfter` parse block (currently ~L305), reusing the existing parse + count-pattern logic below it:

```csharp
        // New model: the `review` attribute carries review dates and/or free-text conditions.
        // Feed date-parseable values into the existing reviewAfter parse (first wins); join the
        // rest into reviewWhen. Only when the legacy params weren't explicitly supplied.
        if (reviewAfter is null && reviewWhen is null
            && fields is not null && fields.TryGetValue(KnownFields.ReviewKey, out var reviewVals))
        {
            var whenParts = new List<string>();
            foreach (string v in reviewVals)
            {
                if (reviewAfter is null && DateTimeOffset.TryParse(v, out _))
                    reviewAfter = v;
                else
                    whenParts.Add(v);
            }
            if (whenParts.Count > 0)
                reviewWhen = string.Join("; ", whenParts);
        }
```

Add the normalization helper (near the other private statics in this file):

```csharp
    /// <summary>Lowercases attribute keys and trims/drops empty values, matching the Fields
    /// convention (FieldExtractor lowercases keys). Null when nothing survives.</summary>
    private static Dictionary<string, string[]>? NormalizeAttributes(IReadOnlyDictionary<string, string[]>? attributes)
    {
        if (attributes is not { Count: > 0 }) return null;
        var result = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in attributes)
        {
            if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value is null) continue;
            var vals = kv.Value.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v.Trim()).ToArray();
            if (vals.Length == 0) continue;
            result[kv.Key.Trim().ToLowerInvariant()] = vals;
        }
        return result.Count > 0 ? result : null;
    }
```

Note: the code-ref hashing block (~L322 `fields.TryGetValue("file", out var fileRefs)`) already reads `fields["file"]`, so `attributes.file` is hashed with no further change.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Store_Attributes_LandInFields|FullyQualifiedName~Store_ReviewAttribute|FullyQualifiedName~Store_FileAttribute"`
Expected: PASS (3/3). Then run the full `Store_*` filter to confirm no existing `Store` test regressed:
`dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ScriniaMcpToolsTests.Store_"` → all green.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -F- <<'EOF'
feat(mcp): Store accepts attributes, merges into Fields + drives review/file

Adds an additive `attributes` (IReadOnlyDictionary<string,string[]>) param to the
internal Store. Attributes fold into the entry's Fields (so they seed BM25, score
via the weight-scaled Fields block, and feed the graph — all already wired). The
`review` attribute now drives ReviewAfter (date values) + ReviewWhen (text); the
`file` attribute is hashed into CodeRefs via the existing fields["file"] path.
Legacy params/fields are untouched (dual-read). Public tool surface is next.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 2: `remember` + `append` public surfaces

Give the two agent-facing write tools their new signatures — `(path, content, attributes?, keywords?)` — dropping the six legacy params, and thread `attributes` through `Append`/`BuildAppendMetadata`. Rewrite their `[Description]` strings to the attributes model.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (`MemoryRemember` L79–125, `MemoryAppend` L192–222)
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`Append` L1063–1094 create path, its two `BuildAppendMetadata` call sites, `BuildAppendMetadata` signature L1226–1231)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`

**Interfaces:**
- Consumes: `Store(..., attributes:, actionLabel:)` (Task 1).
- Produces: `MemoryRemember(string path, string[] content, Dictionary<string,string[]>? attributes = null, string[]? keywords = null, CancellationToken ct)`; `MemoryAppend(string path, string content, Dictionary<string,string[]>? attributes = null, string[]? keywords = null, CancellationToken ct)`; `Append(..., IReadOnlyDictionary<string,string[]>? attributes = null, CancellationToken ct)`; `BuildAppendMetadata(..., IReadOnlyDictionary<string,string[]>? attributes)`.

- [ ] **Step 1: Write the failing tests**

```csharp
[Fact]
public async Task MemoryRemember_Attributes_RoundTripToFields()
{
    var (tools, store) = Tools();
    await tools.MemoryRemember(
        path: "/findings/acme",
        content: ["Acme renewed."],
        attributes: new Dictionary<string, string[]> { ["org"] = ["acme"], ["topic"] = ["renewal"] },
        keywords: ["renewed", "annual contract"]);

    var entry = store.LoadIndex("local").First(e => e.Name.Contains("acme"));
    entry.Fields.Should().ContainKey("org");
    entry.Fields!["org"].Should().Contain("acme");
    entry.Keywords.Should().Contain("annual contract");
}

[Fact]
public async Task MemoryAppend_Attributes_MergeIntoFields()
{
    var (tools, store) = Tools();
    await tools.MemoryRemember(path: "/sessions/s1", content: ["first"],
        attributes: new Dictionary<string, string[]> { ["person"] = ["alice"] });
    await tools.MemoryAppend(path: "/sessions/s1", content: "second",
        attributes: new Dictionary<string, string[]> { ["person"] = ["bob"], ["topic"] = ["standup"] });

    var entry = store.LoadIndex("local").First(e => e.Name == "s1");
    entry.Fields!["person"].Should().Contain("alice").And.Contain("bob");   // multi-valued union
    entry.Fields.Should().ContainKey("topic");
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MemoryRemember_Attributes_RoundTripToFields|FullyQualifiedName~MemoryAppend_Attributes_MergeIntoFields"`
Expected: FAIL to compile — `MemoryRemember`/`MemoryAppend` have no `attributes` param (CS1739).

- [ ] **Step 3: Rewrite the public signatures**

`MemoryRemember` — replace the param list (keep the method body's validation unchanged; change only the `Store` call to pass `attributes`/`keywords` by name):

```csharp
    [McpServerTool(Name = "memory_remember"), Description(
        "Persist a memory at a path. content is an array — each element becomes one independently " +
        "retrievable chunk. attributes is a map of typed facets that link and filter memories " +
        "(e.g. person/org/topic/file/review); keywords are the distinctive terms and query phrasings " +
        "a searcher would use. Example: memory_remember({ path: '/findings/SEC-001', " +
        "content: ['observation', 'recommendation'], attributes: { org: ['acme'], topic: ['auth'] }, " +
        "keywords: ['oauth flow', 'token refresh'] }).")]
    public async Task<string> MemoryRemember(
        [Description("Memory path (e.g. '/findings/SEC-001', '/patterns/retry', '/temp/scratch', or 'topic:subject' for v1 compat).")] string path,
        [Description("Content array — each element becomes one independently retrievable chunk.")] string[] content,
        [Description("Optional typed attributes — a map of key to values. Recommended keys: person, place, " +
                     "org, thing, topic (entities that link related memories); file, ref (dependencies, hashed " +
                     "for drift detection); review (an ISO 8601 date and/or a free-text condition). Any key is " +
                     "allowed. Example: { person: ['maria'], org: ['acme'], review: ['2026-08-01'] }.")]
        Dictionary<string, string[]>? attributes = null,
        [Description("Optional keywords — the distinctive terms AND the phrasings a searcher is likely to use. Merged with auto-extracted terms and seeded into search.")] string[]? keywords = null,
        CancellationToken cancellationToken = default)
    {
        // Skill-path routing: '/skill/{name}' → SkillCreate.
        var skillRoute = TryRouteSkillRemember(path, content, cancellationToken);
        if (skillRoute is not null) return await skillRoute;

        // ... existing content/path/length/byte validation UNCHANGED ...

        string result = await Store(content, path,
            keywords: keywords, attributes: attributes,
            actionLabel: "remembered", cancellationToken: cancellationToken);
        string? typoHint = ReservedPrefixGuard.HintFor(path);
        if (typoHint is not null)
            result = McpResponseExtensions.InjectInfoHint(result, typoHint);
        return result;
    }
```

`MemoryAppend` — same treatment:

```csharp
    [McpServerTool(Name = "memory_append"), Description(
        "Append a new chunk to an existing memory (or create it if missing). Preserves the memory's " +
        "existing retrieval metadata and merges in any new attributes (multi-valued keys union) and " +
        "keywords (deduplicated). Example: memory_append({ path: '/sessions/2026-05-11', " +
        "content: 'session note', attributes: { topic: ['standup'] } }).")]
    public async Task<string> MemoryAppend(
        [Description("Memory path to append to.")] string path,
        [Description("Text content for the new chunk.")] string content,
        [Description("Optional attributes merged into the memory's existing attributes (multi-valued keys union, scalar keys newer-wins). Same key vocabulary as memory_remember.")]
        Dictionary<string, string[]>? attributes = null,
        [Description("Optional keywords merged with the memory's existing + auto-extracted terms (deduplicated).")] string[]? keywords = null,
        CancellationToken cancellationToken = default)
    {
        // ... existing content/path/byte validation UNCHANGED ...

        string result = await Append(content, path, keywords: keywords, attributes: attributes, cancellationToken);
        string? typoHint = ReservedPrefixGuard.HintFor(path);
        if (typoHint is not null)
            result = McpResponseExtensions.InjectInfoHint(result, typoHint);
        return result;
    }
```

In `MemoryTools.Core.cs`, thread `attributes` through `Append`:

- Add the param to `Append` (before `ct`): `IReadOnlyDictionary<string, string[]>? attributes = null,`
- Create-via-append path (~L1088) — pass it to `Store`:
  ```csharp
          return await this.Store([content], name,
              keywords: keywords, attributes: attributes,
              cancellationToken: cancellationToken);
  ```
- Both `BuildAppendMetadata(...)` call sites (~L1131, L1169) — add `attributes` as the final argument.
- `BuildAppendMetadata` — add the param and fold it into `incoming` (it already builds `incoming`):
  ```csharp
      private (string Description, string[]? Tags, string[]? Keywords, Dictionary<string, int>? Tf, string[]? Anchors, Dictionary<string, string[]>? Fields)
          BuildAppendMetadata(
              string fullText,
              string? newDescription, string[]? newTags, string[]? newKeywords, string[]? newAnchors,
              string? existingDescription, string[]? existingTags, string[]? existingKeywords, string[]? existingAnchors,
              string appendedContent, Dictionary<string, string[]>? routedNewFields, Dictionary<string, string[]>? existingFields,
              IReadOnlyDictionary<string, string[]>? attributes)
      {
          // ... unchanged up to the `incoming` assembly ...
          var incoming = KnownFields.Merge(
              KnownFields.Merge(routedNewFields, FieldExtractor.Extract(appendedContent)),
              FieldExtractor.ExtractRefFields(appendedContent));
          incoming = KnownFields.Merge(incoming, NormalizeAttributes(attributes));   // explicit agent attributes
          var mergedFields = KnownFields.Merge(existingFields, incoming);
          FieldExtractor.SeedTermFrequencies(mergedFields, tf);
          // ... unchanged return ...
      }
  ```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~MemoryRemember_Attributes_RoundTripToFields|FullyQualifiedName~MemoryAppend_Attributes_MergeIntoFields"`
Expected: PASS (2/2). Then `--filter "FullyQualifiedName~ScriniaMcpToolsTests.MemoryRemember_|FullyQualifiedName~ScriniaMcpToolsTests.Append_"` → all green (existing remember/append behavior — reserved-prefix hints, curated-metadata carry-forward — unchanged, since the internal path is additive).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -F- <<'EOF'
feat(mcp): remember/append expose (path, content, attributes, keywords)

The two agent-facing write tools drop the six legacy params (description, tags,
anchors, reviewAfter, reviewWhen, codeRefs) in favour of the attributes map +
keywords. attributes threads through Append/BuildAppendMetadata into Fields.
Internal Store/Append keep the legacy params for dual-read; the model fields are
untouched. Descriptions rewritten to the new model.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 3: `search` attribute filter

Replace the `search` tool's `tags` AND-filter with an `attributeFilter` map. A candidate matches when, for every filter key, its `Fields` carries that key and (if the filter lists values) at least one value matches.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (`MemorySearch` L155–172)
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`Search` L709–762, `ApplyPostFilters` L771–788, replace `ParseTagFilter`/`MatchesTags` with `MatchesAttributes`)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (rewrite `Search_TagsFilter_AndSemantics_RequiresAllTags`)

**Interfaces:**
- Consumes: `SearchResult` variants (`EntryResult.Item.Entry.Fields`, `ChunkEntryResult.ParentItem.Entry.Fields`).
- Produces: `MemorySearch(..., Dictionary<string,string[]>? attributeFilter = null, CancellationToken ct)`; `Search(..., IReadOnlyDictionary<string,string[]>? attributeFilter = null, CancellationToken ct)`; `MatchesAttributes(SearchResult, IReadOnlyDictionary<string,string[]>?) → bool`.

- [ ] **Step 1: Write the failing test (rewrite the tags-filter test)**

Replace `Search_TagsFilter_AndSemantics_RequiresAllTags` (L2140+) with an attribute-filter equivalent — store via attributes, filter via `attributeFilter`:

```csharp
[Fact]
public async Task Search_AttributeFilter_RequiresKeyAndValue()
{
    var (tools, _) = Tools();
    await tools.MemoryRemember(path: "attr-hit", content: ["security refactor entry"],
        attributes: new Dictionary<string, string[]> { ["topic"] = ["security", "refactor"] });
    await tools.MemoryRemember(path: "attr-miss", content: ["security entry only"],
        attributes: new Dictionary<string, string[]> { ["topic"] = ["other"] });
    await tools.MemoryRemember(path: "attr-nokey", content: ["security entry no topic"]);

    string yaml = await tools.MemorySearch(query: "entry",
        attributeFilter: new Dictionary<string, string[]> { ["topic"] = ["security"] });

    yaml.Should().Contain("attr-hit");
    yaml.Should().NotContain("attr-miss");    // has key `topic` but not value `security`
    yaml.Should().NotContain("attr-nokey");   // lacks key `topic` entirely
}

[Fact]
public async Task Search_AttributeFilter_KeyOnly_MatchesAnyValue()
{
    var (tools, _) = Tools();
    await tools.MemoryRemember(path: "haskey", content: ["entry one"],
        attributes: new Dictionary<string, string[]> { ["review"] = ["2026-08-01"] });
    await tools.MemoryRemember(path: "nokey", content: ["entry two"]);

    string yaml = await tools.MemorySearch(query: "entry",
        attributeFilter: new Dictionary<string, string[]> { ["review"] = [] });   // key-only

    yaml.Should().Contain("haskey");
    yaml.Should().NotContain("nokey");
}
```

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Search_AttributeFilter"`
Expected: FAIL to compile — `MemorySearch` has no `attributeFilter` param.

- [ ] **Step 3: Implement the attribute filter**

`MemorySearch` (`MemoryTools.cs`) — replace the `tags` param with `attributeFilter` and pass it through:

```csharp
    [McpServerTool(Name = "memory_search"), Description(
        "Find memories by keyword or phrase. Returns ranked matches with a content preview and the " +
        "entry's attributes per hit so you can decide what to recall without a follow-up call. " +
        "Example: memory_search({ query: 'oauth flow', pathPrefix: '/findings/', " +
        "attributeFilter: { topic: ['auth'] } }).")]
    public async Task<string> MemorySearch(
        [Description("Search query string.")] string query,
        [Description("Optional context terms appended to the query for scoring only — bridges vocabulary mismatches when you know synonyms or related concepts the user didn't type.")] string? context = null,
        [Description("Optional comma-separated scope order, e.g. 'local,api,ephemeral'.")] string? scopes = null,
        [Description("Maximum results to return (default 20). Rich-result format is verbose, so prefer 10 for broad exploratory queries.")] int limit = 20,
        [Description("Optional comma-separated topic names to exclude from results.")] string? excludeTopics = null,
        [Description("Optional path prefix to scope results, e.g. '/findings/' or '/skill/'. Matches the qualified memory name.")] string? pathPrefix = null,
        [Description("Optional attribute filter — a map of key to values. Returns only memories whose attributes " +
                     "contain EVERY listed key; for a key with values, the memory must carry at least one of them. " +
                     "Pass an empty array to require only the key's presence. Example: { topic: ['auth'], review: [] }.")]
        Dictionary<string, string[]>? attributeFilter = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            return ResponseBuilder.Error(
                "memory_search requires 'query'.",
                ErrorCodes.InvalidParameter,
                "memory_search({ query: 'keywords or phrase' })").ToYaml();

        return await Search(query, scopes, limit, excludeTopics, context, pathPrefix, attributeFilter, cancellationToken);
    }
```

`Search` (`MemoryTools.Core.cs`) — replace the `tags` param and the post-filter call:

```csharp
        [Description("Optional path prefix to scope results, e.g. '/findings/'.")] string? pathPrefix = null,
        [Description("Optional attribute filter (map key→values); AND across keys, OR within a key's values.")] IReadOnlyDictionary<string, string[]>? attributeFilter = null,
        CancellationToken cancellationToken = default)
```

and, in the post-filter section (~L751–755):

```csharp
        // Post-filter by path prefix and attribute filter. Both are AND filters.
        matches = ApplyPostFilters(store, matches, pathPrefix, attributeFilter);
```

Rewrite `ApplyPostFilters` to take the filter, delete `ParseTagFilter` and `MatchesTags`, and add `MatchesAttributes`:

```csharp
    private static IReadOnlyList<SearchResult> ApplyPostFilters(
        IMemoryStore store,
        IReadOnlyList<SearchResult> matches,
        string? pathPrefix,
        IReadOnlyDictionary<string, string[]>? attributeFilter)
    {
        if (string.IsNullOrWhiteSpace(pathPrefix) && (attributeFilter is null || attributeFilter.Count == 0))
            return matches;

        var kept = new List<SearchResult>(matches.Count);
        foreach (var match in matches)
        {
            if (!MatchesPathPrefix(store, match, pathPrefix)) continue;
            if (!MatchesAttributes(match, attributeFilter)) continue;
            kept.Add(match);
        }
        return kept;
    }

    private static bool MatchesAttributes(SearchResult match, IReadOnlyDictionary<string, string[]>? filter)
    {
        if (filter is null || filter.Count == 0) return true;
        Dictionary<string, string[]>? fields = match switch
        {
            EntryResult er => er.Item.Entry.Fields,
            ChunkEntryResult cr => cr.ParentItem.Entry.Fields,
            _ => null,   // TopicResult carries no attributes — excluded when a filter is active
        };
        if (fields is null || fields.Count == 0) return false;
        foreach (var want in filter)
        {
            if (!fields.TryGetValue(want.Key, out var have) || have is null || have.Length == 0)
                return false;
            if (want.Value is { Length: > 0 })
            {
                bool any = false;
                foreach (string wv in want.Value)
                {
                    foreach (string hv in have)
                        if (string.Equals(hv, wv, StringComparison.OrdinalIgnoreCase)) { any = true; break; }
                    if (any) break;
                }
                if (!any) return false;
            }
        }
        return true;
    }
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Search_AttributeFilter"`
Expected: PASS (2/2). Then `--filter "FullyQualifiedName~ScriniaMcpToolsTests.Search_"` — all green except any old tags-filter assertions you replaced (there should be none left referencing `tags:`).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Mcp/MemoryTools.Core.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -F- <<'EOF'
feat(mcp): search takes an attributeFilter, replacing the tags AND-filter

memory_search now filters on the attributes map (AND across keys, OR within a
key's values; empty array = key-presence). Matches against the entry's Fields.
Removes ParseTagFilter/MatchesTags; adds MatchesAttributes. The old tags-filter
test is rewritten to the attribute-filter semantics.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 4: Attribute display (search results + list) and `[Description]` finalize

Show attributes where the tools used to show tags — a per-hit `attributes:` line in search results and a Top-attributes block in the list summary — and refresh the `memory_list` description string. Search hits keep showing the content preview (the old `description` line, which is the content preview for new writes) and keep the anchors line for dual-read of old memories.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (`AppendResultBlock` L867–913 + new `AppendAttributesLine`; `BuildSummary` L539–608)
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (`MemoryList` `[Description]` L174–175)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` (rewrite `Search_SurfacesAnchorsAndTagsWhenPresent`; keep `Search_OmitsAnchorsLineWhenEntryHasNoAnchors`)

**Interfaces:**
- Consumes: `ArtifactEntry.Fields`.
- Produces: search-result blocks emit `    attributes:` (from `Entry.Fields`) instead of `    tags:`; list summary emits a `### Top attributes` section.

- [ ] **Step 1: Write the failing test (rewrite the tags-surfacing test)**

Replace `Search_SurfacesAnchorsAndTagsWhenPresent` (L2000+) with an attributes-surfacing version. Store via attributes so `Fields` is populated:

```csharp
[Fact]
public async Task Search_SurfacesAttributesWhenPresent()
{
    var (tools, _) = Tools();
    await tools.MemoryRemember(path: "surfaced", content: ["auth token refresh flow"],
        attributes: new Dictionary<string, string[]> { ["topic"] = ["security"], ["org"] = ["acme"] });

    string yaml = await tools.MemorySearch(query: "auth token");

    yaml.Should().Contain("attributes:");
    yaml.Should().Contain("topic: security");
    yaml.Should().Contain("org: acme");
}
```

`Search_OmitsAnchorsLineWhenEntryHasNoAnchors` stays as-is (new writes carry no anchors, so the omit-anchors assertion still holds).

- [ ] **Step 2: Run to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Search_SurfacesAttributesWhenPresent"`
Expected: FAIL — output has no `attributes:` line yet.

- [ ] **Step 3: Implement attribute display**

In `AppendResultBlock`, swap the entry + chunk tags lines for attributes (leave `TopicResult`'s `AppendTagsLine(tr.Tags)` — topics have no `Fields`):

```csharp
            case EntryResult er:
                {
                    // ... header unchanged ...
                    AppendAttributesLine(sb, er.Item.Entry.Fields);
                    AppendDescription(sb, er.Item.Entry.Description);
                    AppendAnchors(sb, er.Item.Entry.RetrievalAnchors);
                    break;
                }
            case ChunkEntryResult cr:
                {
                    // ... header unchanged ...
                    AppendAttributesLine(sb, cr.ParentItem.Entry.Fields);
                    AppendDescription(sb, cr.Chunk.ContentPreview ?? cr.ParentItem.Entry.Description);
                    AppendAnchors(sb, cr.ParentItem.Entry.RetrievalAnchors);
                    break;
                }
```

Add the renderer (keep `AppendTagsLine` — still used by `TopicResult`):

```csharp
    private static void AppendAttributesLine(System.Text.StringBuilder sb, Dictionary<string, string[]>? fields)
    {
        if (fields is null || fields.Count == 0) return;
        sb.Append("    attributes:\n");
        foreach (var kv in fields)
        {
            sb.Append("      ").Append(kv.Key).Append(": ");
            sb.Append(string.Join(", ", kv.Value)).Append('\n');
        }
    }
```

In `BuildSummary`, add a Top-attributes block after the Top-keywords block (aggregate `Fields` key→value counts):

```csharp
        // Top attributes — aggregate attribute values across all entries (new model's facets).
        var attrCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in entries)
        {
            if (item.Entry.Fields is { Count: > 0 })
                foreach (var kv in item.Entry.Fields)
                    foreach (var v in kv.Value)
                        attrCounts[$"{kv.Key}:{v}"] = attrCounts.GetValueOrDefault($"{kv.Key}:{v}") + 1;
        }
        if (attrCounts.Count > 0)
        {
            var topAttrs = attrCounts
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .Take(10)
                .Select(kv => kv.Key);
            sb.AppendLine("### Top attributes");
            sb.AppendLine(string.Join(", ", topAttrs));
            sb.AppendLine();
        }
```

Refresh `MemoryList`'s `[Description]` (`MemoryTools.cs` L174) — replace the "top keywords" wording:

```csharp
    [McpServerTool(Name = "memory_list"), Description(
        "List stored memories. mode 'summary' (default) shows scopes + top keywords and attributes; " +
        "'full' paginates the table; 'drift' surfaces memories whose file attributes have changed.")]
```

- [ ] **Step 4: Run to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Search_SurfacesAttributesWhenPresent|FullyQualifiedName~Search_OmitsAnchorsLineWhenEntryHasNoAnchors"`
Expected: PASS (2/2). Then the whole file: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ScriniaMcpToolsTests"` → green.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Mcp/MemoryTools.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -F- <<'EOF'
feat(mcp): show attributes in search hits and the list summary

Search result blocks emit an `attributes:` line (from Fields) in place of `tags:`;
the list summary gains a `### Top attributes` block. Topic results keep their tags
line (topics have no attributes). memory_list description refreshed. The old
tags-surfacing search test is rewritten to assert the attributes line.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

## Verification

1. `dotnet build src/Scrinia.Mcp/Scrinia.Mcp.csproj` clean (TreatWarningsAsErrors on — watch CA1859 on the new dictionary params; `IReadOnlyDictionary` on internal methods is fine, concrete `Dictionary` on the tool params is required for schema gen).
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` fully green. The only tests that changed are the three rewritten ones (`Search_TagsFilter…`→`Search_AttributeFilter…`, `Search_SurfacesAnchorsAndTagsWhenPresent`→`Search_SurfacesAttributesWhenPresent`); every other existing test compiles and passes unchanged because the internal `Store`/`Append`/`Search` kept their legacy params.
3. Confirm the agent surface: `MemoryRemember`/`MemoryAppend` expose exactly `(path, content, attributes?, keywords?)`; `MemorySearch` exposes `attributeFilter` and no `tags`; no `[McpServerTool]` method mentions `description`/`tags`/`anchors`/`reviewAfter`/`reviewWhen`/`codeRefs`.
4. Sanity: a memory written the OLD way (construct an `ArtifactEntry` with `Tags`/`Description`/`ReviewAfter` and `store.Upsert`, as the existing tests do) still lists, searches, and shows correctly — the dual-read fields are untouched.

## Self-review (done during authoring)

- **Spec coverage:** implements the spec's Unit 3 "Tool surfaces" — `remember`/`append` new signatures (Tasks 1–2), `search` attribute filter (Task 3), `list` attribute display + `[Description]` rewrites (Task 4). Deferred by design: the tool **rename** (Plan 4), `scri migrate` (Plan 5), removal of the `Tags`/`Anchors`/`Description` model fields + legacy scoring and the authored guide rewrite (Plan 6/7).
- **Scoping guard:** the internal `Store`/`Append`/`BuildAppendMetadata` stay additive (gain `attributes`, keep legacy params) so the ~40 internal-caller tests keep compiling and the model fields stay populated for dual-read — this is the approved dual-read-then-cleanup sequence, not a shortcut. Removing the internal params/fields is Plan 6.
- **Feasibility:** `Dictionary<string,string[]>` tool params generate a valid JSON schema (object + additionalProperties: array-of-string) on .NET 10 / MCP SDK 1.0.0 with no source-gen — verified against the SDK's `JsonSchemaExporter` path and the absence of any `McpServerOptions.SerializerOptions` override.
- **Type consistency:** `attributes` is `Dictionary<string,string[]>?` on the public tools (schema gen) and `IReadOnlyDictionary<string,string[]>?` on the internal `Store`/`Append`/`BuildAppendMetadata` (a `Dictionary` satisfies it); `NormalizeAttributes` and `MatchesAttributes` both take `IReadOnlyDictionary<string,string[]>?`. `KnownFields.Merge` takes concrete `Dictionary?` — `NormalizeAttributes` returns concrete `Dictionary?`, so the merge calls type-check.
- **Behavior parity for old data:** display swaps tags→attributes for entries/chunks but leaves `TopicResult` (no `Fields`) on the tags line; `AppendDescription`/`AppendAnchors` remain for dual-read; list `description` column and drift markers are unchanged.
