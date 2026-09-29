# Agentic-Ingest Faithful Ingestion + Honest Scoring — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the LoCoMo agentic-ingest arm ingest with the real tool loop (stop clobbering memories) and score honestly (judge decoupled from provenance; dialog-ref parser repairs the R@k lower bound).

**Architecture:** All changes live in `benchmarks/Scrinia.Retrieval.Benchmarks` and its test project. Production `Scrinia.Core`/`Scrinia.Mcp` are untouched. Five units: a pure dialog-ref parser; the harness's tool surface expanded to `memory_append`/`memory_recall`; a resolve-once full-content cache in the retriever; the provenance map rebuilt from parsed dialog ids over full content; and an `ISurfacedContentProvider` that feeds the judge memory content independent of provenance.

**Tech Stack:** .NET 10, C#, xUnit + FluentAssertions. Test project: `tests/Scrinia.Retrieval.Benchmarks.Tests` (flat layout, namespace `Scrinia.Retrieval.Benchmarks.Tests`).

## Global Constraints

- Scope is **LoCoMo, dialog granularity only**. No session granularity, no LongMemEval.
- **Do NOT modify production `Scrinia.Core` or `Scrinia.Mcp`.** All bench needs are met in the benchmark project. Production `FieldExtractor`/`ReferenceExtractor` are not touched.
- **WIP files:** the user has uncommitted edits to `.gitignore`, `LlmAgentHarness.cs`, `ScriniaRetriever.cs`, `OpenAiCompatibleBenchLlmClient.cs`, `plugins/opencode/scrinia.ts`, and untracked `tools/`, `plugins/opencode/package.json`. Do NOT touch or stage any of these. The single exception is `AgenticIngestPrompts.cs` (also WIP): Task 2 builds on its current on-disk content and commits it together — that is authorized. Never `git add -A`; stage only the exact files each task names.
- Commit trailer on every commit: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`.
- `docs/superpowers` is gitignored; plan/spec files are local scratch and are not committed.
- Dialog id shape is `D{session}:{index}` (e.g. `D2:1`). At dialog granularity these are the buffered doc ids, so citations map directly — no namespace mapping.

---

## Task 1: DialogRefParser (pure dialog-id parser)

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/DialogRefParser.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/DialogRefParserTests.cs`

**Interfaces:**
- Produces: `public static IReadOnlyList<string> DialogRefParser.ExtractDialogIds(string? content)` — returns every `D{n}:{i}` dialog id found in `content`, in every form (bare/inline, comma list, same-session range), de-duplicated in first-seen order. Does NOT filter against real docs (caller resolves).

- [ ] **Step 1: Write the failing tests**

Create `tests/Scrinia.Retrieval.Benchmarks.Tests/DialogRefParserTests.cs`:

```csharp
using FluentAssertions;
using Scrinia.Retrieval.Benchmarks.Core;
using Xunit;

namespace Scrinia.Retrieval.Benchmarks.Tests;

public sealed class DialogRefParserTests
{
    [Fact]
    public void BareId_ReturnsSingle() =>
        DialogRefParser.ExtractDialogIds("derived from D2:1 only").Should().Equal("D2:1");

    [Fact]
    public void CommaList_SplitsEach() =>
        DialogRefParser.ExtractDialogIds("ref: D2:1, D2:3, D2:5")
            .Should().Equal("D2:1", "D2:3", "D2:5");

    [Fact]
    public void FullRange_ExpandsInclusive() =>
        DialogRefParser.ExtractDialogIds("ref: D9:1-D9:5")
            .Should().Equal("D9:1", "D9:2", "D9:3", "D9:4", "D9:5");

    [Fact]
    public void ShortRange_ExpandsInclusive() =>
        DialogRefParser.ExtractDialogIds("ref: D9:1-3")
            .Should().Equal("D9:1", "D9:2", "D9:3");

    [Fact]
    public void CrossSessionRange_YieldsEndpointsOnly() =>
        DialogRefParser.ExtractDialogIds("see D9:15-D10:2")
            .Should().Equal("D9:15", "D10:2");

    [Fact]
    public void DescendingRange_YieldsEndpointsOnly() =>
        DialogRefParser.ExtractDialogIds("D3:5-D3:2")
            .Should().Equal("D3:5", "D3:2");

    [Fact]
    public void Duplicates_AreCollapsed_OrderPreserved() =>
        DialogRefParser.ExtractDialogIds("D2:1 and again D2:1 then D2:2")
            .Should().Equal("D2:1", "D2:2");

    [Fact]
    public void NoDialogIds_ReturnsEmpty() =>
        DialogRefParser.ExtractDialogIds("In 2024 they went to Lisbon. No ids here.")
            .Should().BeEmpty();

    [Fact]
    public void NullOrBlank_ReturnsEmpty()
    {
        DialogRefParser.ExtractDialogIds(null).Should().BeEmpty();
        DialogRefParser.ExtractDialogIds("   ").Should().BeEmpty();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~DialogRefParserTests" --nologo`
Expected: FAIL — `DialogRefParser` does not exist (compile error).

- [ ] **Step 3: Implement the parser**

Create `benchmarks/Scrinia.Retrieval.Benchmarks/Core/DialogRefParser.cs`:

```csharp
using System.Text.RegularExpressions;

namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// Parses LoCoMo dialog-id citations (<c>D{session}:{index}</c>) out of arbitrary
/// agent-written memory content, in every form the model emits: bare/inline ids,
/// comma-separated lists (<c>D2:1, D2:3</c>), and same-session ranges
/// (<c>D9:1-D9:17</c> or <c>D9:1-17</c>). Pure and dataset-shape-only: it does NOT
/// check ids against real docs — the caller resolves them. Ids are returned
/// de-duplicated in first-seen order.
/// </summary>
public static partial class DialogRefParser
{
    // D{n}:{a}, with an optional range tail "- D{m}:{b}" or "- {b}" (spaces allowed).
    // Group 1 = n, 2 = a, 3 = m (range-end session, optional), 4 = b (range end, optional).
    [GeneratedRegex(@"D(\d+):(\d+)(?:\s*-\s*(?:D(\d+):)?(\d+))?")]
    private static partial Regex DialogIdPattern();

    public static IReadOnlyList<string> ExtractDialogIds(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return [];

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>();

        void Add(string id)
        {
            if (seen.Add(id)) result.Add(id);
        }

        foreach (Match m in DialogIdPattern().Matches(content))
        {
            int n = int.Parse(m.Groups[1].Value);
            int a = int.Parse(m.Groups[2].Value);

            if (m.Groups[4].Success)
            {
                int endSession = m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : n;
                int b = int.Parse(m.Groups[4].Value);
                if (endSession == n && b >= a)
                {
                    for (int i = a; i <= b; i++) Add($"D{n}:{i}");
                }
                else
                {
                    // Cross-session or descending range → emit the two endpoints only.
                    Add($"D{n}:{a}");
                    Add($"D{endSession}:{b}");
                }
            }
            else
            {
                Add($"D{n}:{a}");
            }
        }

        return result;
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~DialogRefParserTests" --nologo`
Expected: PASS — 9/9.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/DialogRefParser.cs tests/Scrinia.Retrieval.Benchmarks.Tests/DialogRefParserTests.cs
git commit -m "feat(bench): DialogRefParser for D{n}:{i} citations (lists, ranges)"
```

---

## Task 2: Faithful ingestion toolset (append + recall)

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestPrompts.cs` (WIP file — build on current content; authorized)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestHarness.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs`

**Interfaces:**
- Consumes: real `Scrinia.Mcp.ScriniaMcpTools.MemoryAppend(string path, string content, string? description, string[]? tags, string[]? keywords, string[]? anchors, CancellationToken)` and `MemoryRecall(string path, int? chunk, bool withBuiltin, CancellationToken)`.
- Produces: `AgenticIngestPrompts.PromptVersion == "v2"`; `Tools()` includes `memory_append` and `memory_recall`; the harness dispatches and replays both.

- [ ] **Step 1: Write the failing test**

Add to `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs` (add `using Scrinia.Mcp;` at the top if absent):

```csharp
    [Fact]
    public async Task Append_AccumulatesOntoRemember_DoesNotClobber()
    {
        Directory.CreateDirectory(_dir);
        var store = new FileMemoryStore(_dir);
        MemoryStoreContext.Current = store;
        try
        {
            var remember = new ToolCallRequest("c1", "memory_remember",
                """{"path":"/findings/x","content":["FIRST-FACT about Lisbon.\nref: D1:1"]}""");
            var append = new ToolCallRequest("c2", "memory_append",
                """{"path":"/findings/x","content":"SECOND-FACT about the flight.\nref: D1:2"}""");
            var client = new ScriptedToolClient(
                new ToolTurnResult(null, [remember], "tool_calls"),
                new ToolTurnResult(null, [append], "tool_calls"),
                new ToolTurnResult("done", [], "stop"));
            var harness = new AgenticIngestHarness(client, maxTurns: 8);
            var docs = new List<HaystackDoc> { new("D1:1", "Maria: Booked Lisbon.", null, "s1") };

            var result = await harness.RunEpisodeAsync(docs, CancellationToken.None);

            result.Succeeded.Should().BeTrue();
            result.ToolCallErrors.Should().Be(0);

            // Read the stored memory back: BOTH facts must be present (append accumulated,
            // memory_remember did NOT clobber the first).
            string recalled = await new ScriniaMcpTools().MemoryRecall("/findings/x", null, false, CancellationToken.None);
            recalled.Should().Contain("FIRST-FACT");
            recalled.Should().Contain("SECOND-FACT");
        }
        finally { MemoryStoreContext.Current = null; }
    }

    [Fact]
    public async Task PromptVersion_IsV2_ForFourToolRegime() =>
        AgenticIngestPrompts.PromptVersion.Should().Be("v2");
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticIngestHarnessTests" --nologo`
Expected: FAIL — `memory_append` is dispatched as unknown tool (accumulation assertion fails), and `PromptVersion` is still `"v1"`.

- [ ] **Step 3: Bump PromptVersion, add tool specs + schemas, update addendum**

In `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestPrompts.cs`:

Change the version constant:

```csharp
    public const string PromptVersion = "v2";
```

Replace the `Addendum` const (build on the current on-disk wording; add the append-not-overwrite guidance):

```csharp
    private const string Addendum = """

        ---
        You are an impartial memory agent who stores session memories using memory_remember, adds to an existing memory using memory_append, reads one back using memory_recall, and finds earlier ones using memory_search. You are not a human, and you do not have opinions or preferences. You are a tool that helps the user store and retrieve factual information.
        memory_remember OVERWRITES whatever is already at a path. Before writing, memory_search for a related memory you already stored this session; if one exists, memory_recall it and memory_append the new facts rather than calling memory_remember on the same path. Use a fresh, specific path only for a genuinely new topic.
        When you store or append, you must include one or more ref: <doc-id> lines in the content, so the memory can be traced back to its source turns (the [doc-id] labels on the transcript). You must also include a path, description, tags, keywords, and anchors if applicable.
        When you are done storing, stop calling tools.

        Store 1 or more memories from the session transcript below.
        """;
```

Replace the `Tools()` method to add the two tools:

```csharp
    public static IReadOnlyList<ToolSpec> Tools() =>
    [
        new ToolSpec("memory_remember",
            "Persist a NEW memory at a path (OVERWRITES anything already there). content is an array — " +
            "each element becomes one independently retrievable chunk. Include ref: <doc-id> lines naming the source turns.",
            RememberSchema),
        new ToolSpec("memory_append",
            "Append a chunk to an EXISTING memory without overwriting it. Use after memory_recall to add facts " +
            "to a memory you already stored. Include ref: <doc-id> lines naming the source turns.",
            AppendSchema),
        new ToolSpec("memory_recall",
            "Read back a memory you stored earlier this session, by its path, to decide whether to append to it.",
            RecallSchema),
        new ToolSpec("memory_search",
            "Find earlier memories you stored in this session by keyword or phrase.",
            SearchSchema),
    ];
```

Add the two new schema consts next to `RememberSchema`:

```csharp
    // Mirrors memory_append (path, content as a SINGLE string, description?, tags?, keywords?, anchors?).
    private const string AppendSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Existing memory path to append to." },
            "content": { "type": "string", "description": "New chunk text. Include ref: <doc-id> lines." },
            "description": { "type": "string" },
            "tags": { "type": "array", "items": { "type": "string" } },
            "keywords": { "type": "array", "items": { "type": "string" } },
            "anchors": { "type": "array", "items": { "type": "string" } }
          },
          "required": ["path", "content"]
        }
        """;

    private const string RecallSchema = """
        {
          "type": "object",
          "properties": {
            "path": { "type": "string", "description": "Memory path to read back." },
            "chunk": { "type": "integer", "description": "Optional 1-based chunk index." }
          },
          "required": ["path"]
        }
        """;
```

- [ ] **Step 4: Add dispatch + counting in the harness**

In `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestHarness.cs`:

Add the two cases to the `switch (call.ToolName)` inside `ExecuteCallAsync` (alongside `memory_remember`/`memory_search`):

```csharp
                case "memory_append":
                    return await DispatchAppendAsync(tools, root, ct);
                case "memory_recall":
                    return await DispatchRecallAsync(tools, root, ct);
```

Add the two dispatch methods next to `DispatchRememberAsync`:

```csharp
    private static async Task<(string, bool)> DispatchAppendAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "path", out string? path) || string.IsNullOrWhiteSpace(path))
            return ("error: memory_append requires 'path' and 'content'", true);
        if (!TryGetString(root, "content", out string? content) || string.IsNullOrWhiteSpace(content))
            return ("error: memory_append requires 'path' and 'content'", true);

        TryGetString(root, "description", out string? description);
        TryGetStringArray(root, "tags", out string[]? tags);
        TryGetStringArray(root, "keywords", out string[]? keywords);
        TryGetStringArray(root, "anchors", out string[]? anchors);

        string result = await tools.MemoryAppend(path, content, description, tags, keywords, anchors, ct);
        return (result, false);
    }

    private static async Task<(string, bool)> DispatchRecallAsync(
        ScriniaMcpTools tools, JsonElement root, CancellationToken ct)
    {
        if (!TryGetString(root, "path", out string? path) || string.IsNullOrWhiteSpace(path))
            return ("error: memory_recall requires 'path'", true);

        int? chunk = null;
        if (root.TryGetProperty("chunk", out var chunkEl) && chunkEl.ValueKind == JsonValueKind.Number
            && chunkEl.TryGetInt32(out int parsedChunk))
            chunk = parsedChunk;

        string result = await tools.MemoryRecall(path, chunk, withBuiltin: false, ct);
        return (result, false);
    }
```

In `RunEpisodeAsync`, extend the per-call counting so appends are visible in the summary. Change the counter declarations line:

```csharp
        int rememberCount = 0, searchCount = 0, appendCount = 0, malformedCount = 0;
```

and the post-execute counting block:

```csharp
                executed.Add(new EpisodeToolCall(call.ToolName, call.ArgumentsJson));
                if (call.ToolName == "memory_remember") rememberCount++;
                else if (call.ToolName == "memory_append") appendCount++;
                else if (call.ToolName == "memory_search") searchCount++;
```

Thread `appendCount` through the three `LogSummary(...)` call sites and update the method:

```csharp
    private static void LogSummary(int turns, int rememberCount, int appendCount, int searchCount, int malformedCount, bool succeeded)
    {
        Console.Error.WriteLine(
            $"[scrinia-bench:agentic-ingest] episode done: turns={turns} remember={rememberCount} " +
            $"append={appendCount} search={searchCount} malformed={malformedCount} succeeded={succeeded}");
    }
```

Update all three `LogSummary(turnsUsed, rememberCount, searchCount, malformedCount, ...)` calls to `LogSummary(turnsUsed, rememberCount, appendCount, searchCount, malformedCount, ...)`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticIngestHarnessTests" --nologo`
Expected: PASS — including `Append_AccumulatesOntoRemember_DoesNotClobber` and `PromptVersion_IsV2_ForFourToolRegime`.

- [ ] **Step 6: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestPrompts.cs benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgenticIngestHarness.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticIngestHarnessTests.cs
git commit -m "feat(bench): give agentic-ingest the append+recall loop; bump PromptVersion v2"
```

---

## Task 3: Resolve-once full-content cache (retire surfaceFullContent)

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`

**Rationale (supersedes spec Part 3's gate note):** provenance (Task 4) must parse refs from full content unconditionally (refs can sit past the 500-char preview), so full content is resolved for every stored memory on every run. The old `surfaceFullContent` flag — which gated the resolve on `--judge` — would make R@k quality depend on whether the judge is on, so it is retired. Full content is resolved once, after episodes, into a cache both Task 4 and Task 5 read.

**Interfaces:**
- Produces: field `private Dictionary<string, string> _fullContentByEntryName` (entry name → decoded full plaintext), populated once in `EnsureEpisodesRunAsync`; read by `RetrieveAsync` (for `hit.Text`), Task 4 (provenance), Task 5 (surfaced content). Constructor loses the `surfaceFullContent` parameter.

- [ ] **Step 1: Update the failing test**

In `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`, the `MakeRetriever` overloads currently pass `surfaceFullContent`. Replace the two `MakeRetriever` helpers with a single one (the flag is gone; full content is always surfaced):

```csharp
    private (AgenticScriniaRetriever retriever, ScriptedToolClient client, EpisodeCache cache) MakeRetriever(
        params ToolTurnResult?[] turns)
    {
        var client = new ScriptedToolClient(turns);
        var harness = new AgenticIngestHarness(client, maxTurns: 8);
        var cache = new EpisodeCache(_cacheFile);
        var retriever = new AgenticScriniaRetriever(_rootDir, harness, cache, modelSlug: "test-model");
        return (retriever, client, cache);
    }
```

Replace the existing `SurfaceFullContent_JudgePath_UsesFullMemory_NotTruncatedPreview` test body's first line (drop the `surfaceFullContent: true` argument) and rename it:

```csharp
    [Fact]
    public async Task FullContent_UsesFullMemory_NotTruncatedPreview()
    {
        string longContent = "Lisbon trip planning. " + new string('x', 520) + " TAILTOKEN-AFTER-500\\nref: D1:1";
        var remember = new ToolCallRequest("c1", "memory_remember",
            "{\"path\":\"summer-plans-summary\",\"content\":[\"" + longContent + "\"]}");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "some doc text", null, "s1"));
        var hits = await retriever.RetrieveAsync("Lisbon", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("D1:1");
        hits[0].Text.Should().Contain("TAILTOKEN-AFTER-500",
            "full content (not the 500-char preview) must be surfaced");
    }
```

Note: any other test in this file that calls `MakeRetriever(surfaceFullContent: ..., ...)` must drop that argument.

- [ ] **Step 2: Run tests to verify they fail (compile error)**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticScriniaRetrieverTests" --nologo`
Expected: FAIL — the constructor still has a `surfaceFullContent` parameter / the two-arg `MakeRetriever` overload was removed (compile error), and `FullContent_UsesFullMemory_NotTruncatedPreview` is new.

- [ ] **Step 3: Add the cache field and resolve-once pass; remove the flag**

In `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`:

Remove the `private readonly bool _surfaceFullContent;` field. In the constructor, remove the `bool surfaceFullContent = false` parameter and the `_surfaceFullContent = surfaceFullContent;` assignment.

Add the cache field next to `_provenance`:

```csharp
    // entryName -> full decoded plaintext, resolved ONCE after episodes (the store is
    // immutable during the query phase). Read by RetrieveAsync, BuildProvenanceMap, and
    // GetSurfacedContentsAsync so full content is resolved at most once per Reset.
    private Dictionary<string, string> _fullContentByEntryName = new(StringComparer.Ordinal);
```

Add the resolve-once method (place near `BuildProvenanceMap`):

```csharp
    /// <summary>
    /// Resolves every stored memory's full plaintext once, via the entry's own Uri
    /// (the authoritative file://…nmp2 path) + NMP/2 decode. On failure, falls back to
    /// the entry's ContentPreview and logs once per entry.
    /// </summary>
    private async Task BuildFullContentCacheAsync(CancellationToken ct)
    {
        var cache = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var sa in _store.ListScoped())
        {
            ArtifactEntry entry = sa.Entry;
            string content;
            try
            {
                string artifact = await _store.ResolveArtifactAsync(entry.Uri, ct);
                content = System.Text.Encoding.UTF8.GetString(Nmp2Strategy.Instance.Decode(artifact));
            }
            catch (Exception ex)
            {
                if (_loggedResolveFailures.Add(entry.Name))
                    Console.Error.WriteLine(
                        $"[scrinia-bench:agentic-ingest] full-content resolve failed for {entry.Name}: {ex.Message}; using preview");
                content = entry.ContentPreview ?? string.Empty;
            }
            cache[entry.Name] = content;
        }
        _fullContentByEntryName = cache;
    }
```

In `EnsureEpisodesRunAsync`, resolve full content before building provenance:

```csharp
            await BuildFullContentCacheAsync(ct);
            BuildProvenanceMap();
            _ingested = true;
```

- [ ] **Step 4: Rewrite RetrieveAsync's content resolution to read the cache**

In `RetrieveAsync`, delete the per-call `resolvedFullContent` dictionary declaration and the whole `if (_surfaceFullContent) { … }` block. Replace the content assignment so `hit.Text` comes from the cache (falling back to preview):

```csharp
            string entryContent = _fullContentByEntryName.TryGetValue(entryName, out var full)
                ? full
                : previewContent;
```

The `entryUri` local (declared as `string entryUri;` and assigned in both switch cases) is now unused — remove its declaration and both `entryUri = …Entry.Uri;` assignments from the switch. Keep `entryName`, `previewContent`, and `score`. Everything below (`if (!_provenance.TryGetValue(entryName, …)) continue;` and the doc-id hit loop) is unchanged — R@k stays provenance-gated.

- [ ] **Step 5: Reset the cache in ResetAsync**

In `ResetAsync`, add alongside the other per-question resets (where `_provenance` is reset):

```csharp
        _fullContentByEntryName = new Dictionary<string, string>(StringComparer.Ordinal);
```

- [ ] **Step 6: Drop the constructor argument at the call site**

In `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs`, find the `new AgenticScriniaRetriever(...)` construction and remove the `surfaceFullContent: HasFlag(args, "--judge")` named argument (leave the rest of the arguments unchanged). If `HasFlag` becomes unused after this, leave it — it is still used by the judge wiring.

- [ ] **Step 7: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticScriniaRetrieverTests" --nologo`
Expected: PASS — including `FullContent_UsesFullMemory_NotTruncatedPreview`.

- [ ] **Step 8: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs
git commit -m "refactor(bench): resolve agentic memory full-content once; retire surfaceFullContent flag"
```

---

## Task 4: Provenance from parsed dialog ids over full content

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs` (`BuildProvenanceMap`)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`

**Interfaces:**
- Consumes: `DialogRefParser.ExtractDialogIds` (Task 1); `_fullContentByEntryName` (Task 3).
- Produces: `_provenance[entryName]` built from parsed dialog ids resolved against `_docsById`; stat counters `_memoriesWritten`/`_memoriesWithoutRefs`/`_unresolvableRefs` reflect parsed ids.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`:

```csharp
    [Fact]
    public async Task Provenance_ParsesCommaListRefs_MapsAllCitedDialogs()
    {
        var remember = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/trip","content":["Trip planning consolidated.\nref: D1:1, D1:2"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));
        await retriever.IngestAsync(new HaystackDoc("D1:2", "Maria: Flight Tuesday.", null, "s1"));

        var hits = await retriever.RetrieveAsync("trip planning", topK: 5);

        hits.Select(h => h.Id).Should().BeEquivalentTo(new[] { "D1:1", "D1:2" });
        retriever.Stats.UnresolvableRefs.Should().Be(0);
    }

    [Fact]
    public async Task Provenance_UnknownCitedId_CountsUnresolvable()
    {
        var remember = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/trip","content":["Cites a missing dialog.\nref: D9:99"]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));

        var hits = await retriever.RetrieveAsync("missing dialog", topK: 5);

        hits.Should().BeEmpty("D9:99 is not a buffered doc");
        retriever.Stats.UnresolvableRefs.Should().Be(1);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticScriniaRetrieverTests" --nologo`
Expected: FAIL — current `BuildProvenanceMap` reads `Fields["ref"]`, which captures `"D1:1, D1:2"` as one opaque value that matches no doc, so `hits` is empty.

- [ ] **Step 3: Rewrite BuildProvenanceMap to parse full content**

In `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`, replace the body of `BuildProvenanceMap` with:

```csharp
    private void BuildProvenanceMap()
    {
        var provenance = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);

        foreach (var sa in _store.ListScoped())
        {
            ArtifactEntry entry = sa.Entry;
            _memoriesWritten++;

            string content = _fullContentByEntryName.TryGetValue(entry.Name, out var full)
                ? full
                : string.Empty;
            var citedIds = DialogRefParser.ExtractDialogIds(content);

            if (citedIds.Count == 0)
            {
                _memoriesWithoutRefs++;
                provenance[entry.Name] = [];
                continue;
            }

            var resolved = new List<string>(citedIds.Count);
            foreach (string id in citedIds)
            {
                if (_docsById.ContainsKey(id))
                    resolved.Add(id);
                else
                    _unresolvableRefs++;
            }
            provenance[entry.Name] = resolved;
        }

        _provenance = provenance;
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticScriniaRetrieverTests" --nologo`
Expected: the two NEW provenance tests PASS, but several PRE-EXISTING tests in this file now FAIL — they use synthetic `d1`/`d2` doc ids and `ref: d1` lines, which `DialogRefParser` (D{n}:{i}-only) does not recognize, so their provenance is now empty. This is correct: those ids were never realistic. Migrate them in the next step, then re-run.

- [ ] **Step 4b: Migrate the pre-existing tests to realistic dialog ids**

In `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`, in each of these five tests apply the same rename everywhere the id appears (in `HaystackDoc` ids, `ref:` lines in memory content, and `Id` assertions): `d1` → `D1:1`, `d2` → `D1:2`.

- `ProvenanceExpansion_ReturnsCitedDocs_InOrder` — docs become `HaystackDoc("D1:1", …)` / `HaystackDoc("D1:2", …)`; content `"Booked the Lisbon trip.\nref: D1:1\nref: D1:2"`; assertion `.Should().Equal("D1:1", "D1:2")`.
- `DedupAcrossMemories_DocAppearsOnce` — refs `ref: D1:1` and `ref: D1:1\nref: D1:2`; docs `D1:1`/`D1:2`; assertion `h.Id == "D1:1"`.
- `CacheReplay_RoundTrips_WithoutSecondLlmCall` — content `"…\nref: D1:1"`; doc `HaystackDoc("D1:1", …)`.
- `FailedAttempt_PartialWrite_IsRolledBackBeforeRetry` — first memory `ref: D1:1`, retry memory `ref: D1:2`; docs `D1:1`/`D1:2`; assertions `Contain("D1:2")` / `NotContain("D1:1")`.
- `RetrievedHit_Text_IsMemoryContent_NotDocText` — content `"MEMORY-SUMMARY-TOKEN about Lisbon.\nref: D1:1"`; doc `HaystackDoc("D1:1", "RAW-DOC-TOKEN Maria booked Lisbon.", …)`; assertion `hits[0].Id.Should().Be("D1:1")`.

(Tests that do not rely on a resolvable ref — `FailedEpisode_OnDoubleNull_*`, `NoRefMemory_*`, `MalformedToolCall_*`, `ToolExecutionThrows_InvalidPath_*` — need no change: they already assert empty hits or stats, independent of dialog-id parsing.)

Re-run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticScriniaRetrieverTests" --nologo`
Expected: PASS — all retriever tests green.

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs
git commit -m "feat(bench): build agentic provenance from parsed dialog ids, not Fields[ref]"
```

---

## Task 5: Judge decoupling via ISurfacedContentProvider

**Files:**
- Create: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ISurfacedContentProvider.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs`, `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs`

**Interfaces:**
- Produces: `public interface ISurfacedContentProvider { Task<IReadOnlyList<string>> GetSurfacedContentsAsync(string query, int k, CancellationToken ct = default); }`. `AgenticScriniaRetriever` implements it (top-k BM25-matching memories' full content, no provenance gate). `BenchmarkRunner` sources the judge's contents from it when available.

- [ ] **Step 1: Write the failing tests**

Add to `tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs` (proves the decoupling — a memory with NO resolvable ref still surfaces content for the judge):

```csharp
    [Fact]
    public async Task GetSurfacedContents_ReturnsMemoryContent_EvenWithNoResolvableRefs()
    {
        var remember = new ToolCallRequest("c1", "memory_remember",
            """{"path":"/findings/trip","content":["JUDGE-VISIBLE consolidated Lisbon plan with no citation."]}""");
        var (retriever, _, _) = MakeRetriever(
            new ToolTurnResult(null, [remember], "tool_calls"),
            new ToolTurnResult("done", [], "stop"));

        await retriever.IngestAsync(new HaystackDoc("D1:1", "Maria: Booked Lisbon.", null, "s1"));

        // R@k drops it (no resolvable ref) ...
        (await retriever.RetrieveAsync("Lisbon plan", topK: 5)).Should().BeEmpty();
        // ... but the judge still sees the memory content.
        var surfaced = await retriever.GetSurfacedContentsAsync("Lisbon plan", k: 10);
        surfaced.Should().ContainSingle().Which.Should().Contain("JUDGE-VISIBLE");
    }
```

Add to `tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs` a wiring test. First add a capturing LLM client and a dual retriever near the existing `StubRetriever`/`ScriptedLlmClient`:

```csharp
    private sealed class CapturingLlmClient : IBenchLlmClient
    {
        public string? LastAnswererUser { get; private set; }
        private int _n;
        public Task<bool> IsAvailableAsync(CancellationToken ct) => Task.FromResult(true);
        public Task<string?> CompleteAsync(string s, string u, int m, bool j, CancellationToken ct)
        {
            // First call is the answerer (captures the retrieved contents), second is the grader.
            if (_n++ == 0) LastAnswererUser = u;
            return Task.FromResult<string?>(_n == 1 ? "Lisbon" : "correct");
        }
    }

    private sealed class SurfacingStubRetriever : IRetriever, ISurfacedContentProvider
    {
        public Task IngestAsync(HaystackDoc doc, CancellationToken ct = default) => Task.CompletedTask;
        public Task<IReadOnlyList<RetrievalHit>> RetrieveAsync(string query, int topK, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<RetrievalHit>>([new RetrievalHit("D1:1", "HITS-TEXT-TOKEN", 1.0)]);
        public Task<IReadOnlyList<string>> GetSurfacedContentsAsync(string query, int k, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<string>>(["SURFACED-TOKEN memory content"]);
        public Task ResetAsync(CancellationToken ct = default) => Task.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public async Task Judge_SourcesContent_FromSurfacedContentProvider_NotHits()
    {
        var retriever = new SurfacingStubRetriever();
        var question = new BenchmarkQuestion(
            "q1", "Where did Maria go?", [], ["D1:1"], [], "single-hop", GoldAnswer: "Lisbon");
        var llm = new CapturingLlmClient();
        var judge = new AnswerabilityJudge(llm, new LlmResponseCache(_cacheFile), "test", "test-model", judgeK: 10);

        await new BenchmarkRunner(NoOpAgentHarness.Instance, judge).RunAsync(
            "bench-test", "judge-test",
            ToAsync(new[] { question }), retriever, topK: 10,
            BenchmarkScorer.ScoreAnyHitById, ct: CancellationToken.None);

        llm.LastAnswererUser.Should().Contain("SURFACED-TOKEN");
        llm.LastAnswererUser.Should().NotContain("HITS-TEXT-TOKEN");
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticScriniaRetrieverTests|FullyQualifiedName~RunnerJudgeIntegrationTests" --nologo`
Expected: FAIL — `ISurfacedContentProvider` / `GetSurfacedContentsAsync` do not exist (compile error).

- [ ] **Step 3: Create the interface**

Create `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ISurfacedContentProvider.cs`:

```csharp
namespace Scrinia.Retrieval.Benchmarks.Core;

/// <summary>
/// A retriever that can surface "what the store returns for a query" as raw content
/// strings for the LLM judge, independent of the R@k doc-id mapping. The agentic-ingest
/// arm implements this so the judge sees the matching memories' content even when their
/// citations don't resolve to a gold doc id (R@k stays a provenance-gated lower bound).
/// Retrievers that don't implement it fall back to their RetrieveAsync hit texts.
/// </summary>
public interface ISurfacedContentProvider
{
    Task<IReadOnlyList<string>> GetSurfacedContentsAsync(
        string query, int k, CancellationToken ct = default);
}
```

- [ ] **Step 4: Implement it on the retriever**

In `benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs`, change the class declaration:

```csharp
public sealed class AgenticScriniaRetriever : IRetriever, ISurfacedContentProvider
```

Add the method (place after `RetrieveAsync`):

```csharp
    /// <summary>
    /// Top-<paramref name="k"/> matching memories' full content, deduped by entry, with
    /// NO provenance gate — this is "what the store surfaces for the query" for the judge.
    /// </summary>
    public async Task<IReadOnlyList<string>> GetSurfacedContentsAsync(
        string query, int k, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        ct.ThrowIfCancellationRequested();
        await EnsureEpisodesRunAsync(ct);

        // Over-fetch so chunk-results collapsing to the same parent memory still yield k distinct memories.
        int searchLimit = Math.Max(k * 4, k);
        IReadOnlyList<SearchResult> results = _store.SearchAll(query, scopes: null, limit: searchLimit);

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var contents = new List<string>(k);
        foreach (var r in results)
        {
            if (contents.Count >= k) break;
            string? name = r switch
            {
                EntryResult er => er.Item.Entry.Name,
                ChunkEntryResult cr => cr.ParentItem.Entry.Name,
                _ => null,
            };
            if (name is null || !seen.Add(name)) continue;
            contents.Add(_fullContentByEntryName.TryGetValue(name, out var full) ? full : string.Empty);
        }
        return contents;
    }
```

- [ ] **Step 5: Wire the runner to prefer the provider**

In `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs`, in the `RunAsync` judge block, replace:

```csharp
                var verdict = await _judge.JudgeAsync(
                    q.Id, q.QueryText, q.GoldAnswer,
                    hits.Select(h => h.Text).ToList(), ct);
```

with:

```csharp
                var judgeContents = retriever is ISurfacedContentProvider scp
                    ? await scp.GetSurfacedContentsAsync(queryText, _judge.JudgeK, ct)
                    : hits.Select(h => h.Text).ToList();
                var verdict = await _judge.JudgeAsync(
                    q.Id, q.QueryText, q.GoldAnswer, judgeContents, ct);
```

Make the identical change in the `RunGroupedAsync` judge block (same three-line `JudgeAsync` call over `hits.Select(h => h.Text).ToList()`, with `q`/`queryText` in that scope — use the `queryText` local already computed there).

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --filter "FullyQualifiedName~AgenticScriniaRetrieverTests|FullyQualifiedName~RunnerJudgeIntegrationTests" --nologo`
Expected: PASS — including `GetSurfacedContents_ReturnsMemoryContent_EvenWithNoResolvableRefs` and `Judge_SourcesContent_FromSurfacedContentProvider_NotHits`. The existing `RunAsync_WithJudge_*` / `RunGroupedAsync_WithJudge_*` tests use `StubRetriever` (no `ISurfacedContentProvider`) so they still fall back to `hits.Text` and stay green.

- [ ] **Step 7: Full suite + commit**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo`
Expected: PASS — whole suite green.

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/ISurfacedContentProvider.cs benchmarks/Scrinia.Retrieval.Benchmarks/Core/AgenticScriniaRetriever.cs benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgenticScriniaRetrieverTests.cs tests/Scrinia.Retrieval.Benchmarks.Tests/RunnerJudgeIntegrationTests.cs
git commit -m "feat(bench): decouple judge from provenance via ISurfacedContentProvider"
```

---

## Final verification

After Task 5, run the full benchmark test project once more and confirm green:

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo`
Expected: all tests pass, no warnings introduced beyond the pre-existing CA1707/CA1861 style warnings.

Manual sanity (optional, user-run — needs the bench server + `LLMACTL_KEY`): a fresh `locomo --granularity dialog --agentic-ingest --judge …` run should now show `append=` activity in the episode logs, a non-zero `answerable@10`, and non-zero provenance R@k where the model cited dialog ids. The `episodes-openai-qwen3.ndjson` cache re-runs from scratch (PromptVersion v2).
