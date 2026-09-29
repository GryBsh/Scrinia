# Typed-Keyword Bench Sweep Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an `entity:`/`topic:` typed-keyword agent op to the retrieval bench and run a four-cell LoCoMo-dialog sweep (CE constant) comparing typed keywords vs anchors as the write-time signal.

**Architecture:** A new agent op (`GenerateTypedKeywordsAsync`) generates `prefix:value` tags from doc content; the bench retriever attaches them to `ArtifactEntry.Keywords` (field-score) and seeds their tokenized *values* into the BM25 TF dict at +5 via the already-shipped `TextAnalysis.IndexTokensForKeyword`. A `--agent` flag gains `typed` and comma-combos (`anchors,typed`) so the four cells share one cached generation pass. Bench-only — no Scrinia.Core changes.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, the existing bench agent harness (`Scrinia.Retrieval.Benchmarks.Agent`), `Scrinia.Core.Search.TextAnalysis`.

**Spec:** `docs/superpowers/specs/2026-06-02-typed-keyword-sweep-design.md`

---

## File Structure

- `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/IAgentHarness.cs` — extend `AgentModes` (new `TypedKeywords` flag, `Parse`), add interface method (default null).
- `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgentPrompts.cs` — typed-keyword prompts + `ParseTypedKeywords`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/LlmAgentHarness.cs` — real `GenerateTypedKeywordsAsync`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/NoOpAgentHarness.cs` — explicit null override.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs` — `ParseAgentMode` delegates to `AgentModes.Parse`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/IRetriever.cs` — ingest overload with `keywords`.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ScriniaRetriever.cs` — wire typed keywords into ingest.
- `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs` — generate + pass typed keywords.
- Tests: `tests/Scrinia.Retrieval.Benchmarks.Tests/{AgentPromptsTests,LlmAgentHarnessTests,ScriniaRetrieverTests}.cs`.

Conventions (verified in-repo): test project uses `FluentAssertions` + `Xunit`; `ImplicitUsings` is enabled (System.Linq available); the bench test assembly already references the bench project types. Run a single test project with:
`dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo`.

---

## Task 1: Extend `AgentModes` with a `TypedKeywords` flag + a public `Parse`

**Why public `Parse`:** `RetrieverFactory` is an `internal` class, so its `ParseAgentMode` isn't cleanly unit-testable from the test assembly. Moving the parse logic onto the public `AgentModes` type makes it testable and keeps `RetrieverFactory` a thin delegate.

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/IAgentHarness.cs` (the `AgentModes` record)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs:187-196` (`ParseAgentMode`)
- Modify: `tests/Scrinia.Retrieval.Benchmarks.Tests/LlmAgentHarnessTests.cs:149` (4-arg ctor fix)
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgentPromptsTests.cs`

- [ ] **Step 1: Write failing tests for the new label + parse behavior**

Add to `AgentPromptsTests.cs` (inside the `AgentPromptsTests` class, after `AgentModes_Label_ProducesStableTags`):

```csharp
    [Fact]
    public void AgentModes_Label_IncludesTypedKeywords()
    {
        AgentModes.TypedKeywordsOnly.Label().Should().Be("typed");
        new AgentModes(true, false, false, true).Label().Should().Be("anchors+typed");
    }

    [Fact]
    public void AgentModes_Parse_HandlesTypedAndCombos()
    {
        AgentModes.Parse("typed").Should().Be(AgentModes.TypedKeywordsOnly);
        AgentModes.Parse("anchors").Should().Be(AgentModes.AnchorsOnly);
        AgentModes.Parse("anchors,typed").Should().Be(new AgentModes(true, false, false, true));
        AgentModes.Parse("off").Should().Be(AgentModes.Off);
        AgentModes.Parse("all").Should().Be(AgentModes.All);
        AgentModes.Parse(null).Should().Be(AgentModes.Off);
    }

    [Fact]
    public void AgentModes_Parse_ThrowsOnUnknownComponent()
    {
        Action act = () => AgentModes.Parse("anchors,bogus");
        act.Should().Throw<ArgumentException>();
    }
```

- [ ] **Step 2: Run the tests to verify they fail to compile**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~AgentModes"`
Expected: BUILD FAILURE — `TypedKeywordsOnly`, the 4-arg ctor, and `Parse` don't exist yet.

- [ ] **Step 3: Replace the `AgentModes` record in `IAgentHarness.cs`**

Replace the entire `public readonly record struct AgentModes(...) { ... }` block (currently lines 57-78) with:

```csharp
public readonly record struct AgentModes(bool Anchors, bool Rewrite, bool Hyde, bool TypedKeywords)
{
    public static AgentModes Off { get; } = new(false, false, false, false);
    public static AgentModes AnchorsOnly { get; } = new(true, false, false, false);
    public static AgentModes RewriteOnly { get; } = new(false, true, false, false);
    public static AgentModes HydeOnly { get; } = new(false, false, true, false);
    public static AgentModes TypedKeywordsOnly { get; } = new(false, false, false, true);
    public static AgentModes All { get; } = new(true, true, true, true);

    public bool AnyEnabled => Anchors || Rewrite || Hyde || TypedKeywords;

    public string Label() => (Anchors, Rewrite, Hyde, TypedKeywords) switch
    {
        (false, false, false, false) => "off",
        (true, false, false, false) => "anchors",
        (false, true, false, false) => "rewrite",
        (false, false, true, false) => "hyde",
        (false, false, false, true) => "typed",
        (true, true, true, true) => "all",
        _ => string.Join("+",
            new[]
            {
                Anchors ? "anchors" : null,
                Rewrite ? "rewrite" : null,
                Hyde ? "hyde" : null,
                TypedKeywords ? "typed" : null,
            }.Where(s => s is not null)),
    };

    /// <summary>
    /// Parses an <c>--agent</c> value: <c>off</c>/<c>none</c>/empty → <see cref="Off"/>,
    /// <c>all</c> → <see cref="All"/>, otherwise a comma-separated list of components
    /// (<c>anchors</c>, <c>typed</c>, <c>rewrite</c>, <c>hyde</c>) combined into one mode.
    /// Throws <see cref="ArgumentException"/> on an unknown component so misconfiguration
    /// fails fast at startup rather than silently running the wrong mode.
    /// </summary>
    public static AgentModes Parse(string? raw)
    {
        string s = (raw ?? string.Empty).Trim().ToLowerInvariant();
        if (s is "" or "off" or "none" or "false") return Off;
        if (s == "all") return All;

        bool anchors = false, rewrite = false, hyde = false, typed = false;
        foreach (string part in s.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (part)
            {
                case "anchors": anchors = true; break;
                case "rewrite": rewrite = true; break;
                case "hyde": hyde = true; break;
                case "typed" or "typed-keywords" or "keywords": typed = true; break;
                default:
                    throw new ArgumentException(
                        $"unknown --agent component '{part}'. Expected: off | anchors | typed | rewrite | hyde | all " +
                        "(comma-separated combos allowed, e.g. 'anchors,typed').");
            }
        }
        return new AgentModes(anchors, rewrite, hyde, typed);
    }
}
```

- [ ] **Step 4: Point `RetrieverFactory.ParseAgentMode` at `AgentModes.Parse`**

In `RetrieverFactory.cs`, replace the `ParseAgentMode` method (lines 187-196) with:

```csharp
    /// <summary>Parses <c>--agent</c> into an <see cref="AgentModes"/>. Delegates to
    /// <see cref="AgentModes.Parse"/> so the parse logic lives with the type and stays unit-testable.</summary>
    public static AgentModes ParseAgentMode(string? raw) => AgentModes.Parse(raw);
```

- [ ] **Step 5: Fix the 4-arg ctor in the existing harness test**

In `LlmAgentHarnessTests.cs` line 149, change:

```csharp
            fake, NewCache(), "fake", "test-model", new AgentModes(false, true, true));
```

to:

```csharp
            fake, NewCache(), "fake", "test-model", new AgentModes(false, true, true, false));
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~AgentModes"`
Expected: PASS (3 new tests + existing `AgentModes_Label_ProducesStableTags`).

- [ ] **Step 7: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Agent/IAgentHarness.cs benchmarks/Scrinia.Retrieval.Benchmarks/Cli/RetrieverFactory.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgentPromptsTests.cs tests/Scrinia.Retrieval.Benchmarks.Tests/LlmAgentHarnessTests.cs
git commit -m "bench: add TypedKeywords flag and AgentModes.Parse for combos"
```

---

## Task 2: Typed-keyword prompts + parser in `AgentPrompts`

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgentPrompts.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/AgentPromptsTests.cs`

- [ ] **Step 1: Write failing tests for `ParseTypedKeywords`**

Add to `AgentPromptsTests.cs`:

```csharp
    [Fact]
    public void ParseTypedKeywords_KeepsEntityAndTopic_Lowercased()
    {
        string raw = "entity:Maria\ntopic:Home Renovation\nentity:Toyota Camry";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("entity:maria", "topic:home renovation", "entity:toyota camry");
    }

    [Fact]
    public void ParseTypedKeywords_DropsUnknownPrefixes()
    {
        // date:/person:/random: are not in the allowed set for this experiment.
        string raw = "entity:mara\ndate:2027-01-01\nperson:bob\nrandom:thing\ntopic:cooking";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("entity:mara", "topic:cooking");
    }

    [Fact]
    public void ParseTypedKeywords_DropsLinesWithoutPrefixValue()
    {
        string raw = "entity:\n:orphan\njust some text\ntopic:music";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("topic:music");
    }

    [Fact]
    public void ParseTypedKeywords_StripsBulletsAndQuotes()
    {
        string raw = "- entity:alice\n* topic:travel\n1. \"entity:bob\"";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("entity:alice", "topic:travel", "entity:bob");
    }

    [Fact]
    public void ParseTypedKeywords_DedupsCaseInsensitively()
    {
        string raw = "entity:Maria\nENTITY:maria\ntopic:cooking";
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().Equal("entity:maria", "topic:cooking");
    }

    [Fact]
    public void ParseTypedKeywords_CapsAt_MaxTypedKeywordsPerDoc()
    {
        string raw = string.Join('\n',
            Enumerable.Range(0, 20).Select(i => $"topic:theme{i}"));
        var tags = AgentPrompts.ParseTypedKeywords(raw);
        tags.Should().HaveCount(AgentPrompts.MaxTypedKeywordsPerDoc);
    }

    [Fact]
    public void ParseTypedKeywords_HandlesEmptyAndWhitespace()
    {
        AgentPrompts.ParseTypedKeywords(null).Should().BeEmpty();
        AgentPrompts.ParseTypedKeywords("").Should().BeEmpty();
        AgentPrompts.ParseTypedKeywords("  \n\r\n  ").Should().BeEmpty();
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~ParseTypedKeywords"`
Expected: BUILD FAILURE — `ParseTypedKeywords` / `MaxTypedKeywordsPerDoc` don't exist.

- [ ] **Step 3: Add the prompts + parser to `AgentPrompts.cs`**

Insert immediately before the `// ---------- helpers ----------` line (currently line 165):

```csharp
    // ---------- Typed keywords (entity / topic) ----------

    /// <summary>Maximum typed keywords retained per doc. Caps TF-dict bloat the same
    /// way <see cref="MaxAnchorsPerDoc"/> does for anchors.</summary>
    public const int MaxTypedKeywordsPerDoc = 8;

    /// <summary>Allowed type prefixes this experiment indexes. Unknown prefixes
    /// (date:, person:, random:) are dropped by <see cref="ParseTypedKeywords"/> — the
    /// sweep deliberately scopes to entity + topic (see the design spec).</summary>
    private static readonly string[] TypedKeywordPrefixes = ["entity", "topic"];

    public const string TypedKeywordsSystem =
        "You are helping index a memory for retrieval. List the ENTITIES and TOPICS this memory is about. " +
        "Output one tag per line: `entity:<name>` for a person, place, or object; `topic:<theme>` for an " +
        "activity, theme, or subject. Use short lowercased canonical forms (e.g. `entity:maria`, " +
        "`topic:home renovation`). 3 to 8 tags. No commentary, no numbering, no bullets.";

    /// <summary>Builds the typed-keyword user prompt from doc content (truncated to
    /// <see cref="MaxInputChars"/>). Content only — same anti-data-leakage contract as anchors.</summary>
    public static string TypedKeywordsUser(string content) =>
        "Memory content:\n\n" + Truncate(content, MaxInputChars);

    /// <summary>
    /// Parses typed keywords from raw LLM output. Each kept tag is `prefix:value` with
    /// prefix ∈ {entity, topic}, both lowercased. Tolerates leading bullets/numbering and
    /// surrounding quotes (same as <see cref="ParseAnchors"/>); drops lines without a
    /// valid prefix:value, unknown prefixes, and empty values; dedups case-insensitively;
    /// caps at <see cref="MaxTypedKeywordsPerDoc"/>. Returns an empty array when nothing
    /// parses cleanly (callers convert empty → null at the cache layer).
    /// </summary>
    public static string[] ParseTypedKeywords(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return Array.Empty<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tags = new List<string>(MaxTypedKeywordsPerDoc);

        foreach (string rawLine in raw.Split('\n', StringSplitOptions.None))
        {
            string line = rawLine.TrimEnd('\r').Trim();
            if (line.Length == 0) continue;

            // Strip leading bullets / numbering (repeat for "- 1. foo"), then quotes.
            string stripped = line;
            while (true)
            {
                string after = ListMarkerPrefix.Replace(stripped, string.Empty, count: 1);
                if (ReferenceEquals(after, stripped) || after == stripped) break;
                stripped = after;
            }
            stripped = stripped.Trim().Trim('"', '\'').Trim();

            int colon = stripped.IndexOf(':');
            if (colon <= 0 || colon >= stripped.Length - 1) continue;   // need prefix:value
            string prefix = stripped[..colon].Trim().ToLowerInvariant();
            if (Array.IndexOf(TypedKeywordPrefixes, prefix) < 0) continue;  // unknown type → drop
            string value = stripped[(colon + 1)..].Trim().ToLowerInvariant();
            if (value.Length == 0) continue;

            string tag = $"{prefix}:{value}";
            if (!seen.Add(tag)) continue;
            tags.Add(tag);
            if (tags.Count >= MaxTypedKeywordsPerDoc) break;
        }
        return tags.ToArray();
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~ParseTypedKeywords"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Agent/AgentPrompts.cs tests/Scrinia.Retrieval.Benchmarks.Tests/AgentPromptsTests.cs
git commit -m "bench: typed-keyword (entity/topic) prompt + parser"
```

---

## Task 3: `GenerateTypedKeywordsAsync` op (interface + NoOp + LlmAgentHarness)

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/IAgentHarness.cs` (interface)
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/NoOpAgentHarness.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Agent/LlmAgentHarness.cs`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/LlmAgentHarnessTests.cs`

- [ ] **Step 1: Write failing tests for the harness op**

Add to `LlmAgentHarnessTests.cs`:

```csharp
    [Fact]
    public async Task GenerateTypedKeywordsAsync_HitsLlmOnce_AndCachesResult()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "entity:maria\ntopic:home renovation" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.TypedKeywordsOnly);

        var first = await harness.GenerateTypedKeywordsAsync("content body", CancellationToken.None);
        var second = await harness.GenerateTypedKeywordsAsync("content body", CancellationToken.None);

        first.Should().NotBeNull().And.Equal("entity:maria", "topic:home renovation");
        second.Should().NotBeNull().And.Equal("entity:maria", "topic:home renovation");
        fake.CompleteCallCount.Should().Be(1);
    }

    [Fact]
    public async Task GenerateTypedKeywordsAsync_DisabledMode_ReturnsNull_WithoutCallingLlm()
    {
        var fake = new RecordingFakeLlm { CompleteResult = "entity:x" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.AnchorsOnly);  // typed disabled

        var result = await harness.GenerateTypedKeywordsAsync("content", CancellationToken.None);
        result.Should().BeNull();
        fake.CompleteCallCount.Should().Be(0);
    }

    [Fact]
    public async Task GenerateTypedKeywordsAsync_NullLlmResult_ReturnsNull_AndCachesNull()
    {
        var fake = new RecordingFakeLlm { CompleteResult = null };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.TypedKeywordsOnly);

        (await harness.GenerateTypedKeywordsAsync("content", CancellationToken.None)).Should().BeNull();
        (await harness.GenerateTypedKeywordsAsync("content", CancellationToken.None)).Should().BeNull();
        fake.CompleteCallCount.Should().Be(1);  // null cached, no retry
    }

    [Fact]
    public async Task GenerateTypedKeywordsAsync_PromptCarriesOnlyDocContent()
    {
        const string docContent = "Maria spent the weekend renovating her kitchen.";
        var fake = new RecordingFakeLlm { CompleteResult = "entity:maria" };
        var harness = new LlmAgentHarness(
            fake, NewCache(), "fake", "test-model", AgentModes.TypedKeywordsOnly);

        await harness.GenerateTypedKeywordsAsync(docContent, CancellationToken.None);

        fake.LastUserPrompt.Should().Contain(docContent);
        fake.LastSystemPrompt.Should().NotContain(docContent);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~GenerateTypedKeywordsAsync"`
Expected: BUILD FAILURE — `GenerateTypedKeywordsAsync` doesn't exist on the harness.

- [ ] **Step 3: Add the method to the `IAgentHarness` interface (default null)**

In `IAgentHarness.cs`, inside the `interface IAgentHarness` block, after the `GenerateHypotheticalAsync` declaration (line 50), add:

```csharp

    /// <summary>
    /// Generates typed retrieval keywords (`entity:<name>` / `topic:<theme>`) for one
    /// doc at ingest. These are agent-curated content-descriptor tags whose values are
    /// indexed into BM25 (and whose full `prefix:value` form feeds the field scorer).
    /// <para>Default implementation returns null (op disabled); the LLM-backed harness
    /// overrides it. Null when disabled or the call fails.</para>
    /// </summary>
    Task<IReadOnlyList<string>?> GenerateTypedKeywordsAsync(string content, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
```

- [ ] **Step 4: Add an explicit override to `NoOpAgentHarness.cs`**

After the `GenerateHypotheticalAsync` method (line 19-20), add:

```csharp

    public Task<IReadOnlyList<string>?> GenerateTypedKeywordsAsync(string content, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<string>?>(null);
```

- [ ] **Step 5: Implement the op in `LlmAgentHarness.cs`**

In `LlmAgentHarness.cs`, add the op key constant next to the others (after line 27 `private const string HydeOp = "hyde";`):

```csharp
    private const string TypedKeywordsOp = "typed";
```

Add the budget next to the others (after line 33):

```csharp
    private static readonly TimeSpan TypedKeywordsBudget = TimeSpan.FromSeconds(60);
```

Add the max-token budget next to the others (after line 40):

```csharp
    private const int TypedKeywordsMaxTokens = 200;
```

Add the method after `GenerateHypotheticalAsync` (after line 170, before the closing brace of the class):

```csharp

    public async Task<IReadOnlyList<string>?> GenerateTypedKeywordsAsync(string content, CancellationToken ct)
    {
        if (!_modes.TypedKeywords) return null;
        if (string.IsNullOrWhiteSpace(content)) return null;

        string hash = LlmResponseCache.Hash(content);
        if (_cache.TryGetStringArray(TypedKeywordsOp, hash, out string[]? cached))
            return cached;  // may be null — null is cached to skip retries

        string? raw;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TypedKeywordsBudget);
            raw = await _llm.CompleteAsync(
                AgentPrompts.TypedKeywordsSystem,
                AgentPrompts.TypedKeywordsUser(content),
                maxTokens: TypedKeywordsMaxTokens,
                jsonMode: false,
                cts.Token);
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            Console.Error.WriteLine(
                $"[scrinia-bench] agent:typed failed for hash {hash[..8]}: {ex.GetType().Name}: {ex.Message}");
            raw = null;
        }

        string[] parsed = raw is null ? Array.Empty<string>() : AgentPrompts.ParseTypedKeywords(raw);
        string[]? result = parsed.Length == 0 ? null : parsed;
        _cache.PutStringArray(TypedKeywordsOp, hash, result);
        return result;
    }
```

- [ ] **Step 6: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~GenerateTypedKeywordsAsync"`
Expected: PASS (4 tests).

- [ ] **Step 7: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Agent/IAgentHarness.cs benchmarks/Scrinia.Retrieval.Benchmarks/Agent/NoOpAgentHarness.cs benchmarks/Scrinia.Retrieval.Benchmarks/Agent/LlmAgentHarness.cs tests/Scrinia.Retrieval.Benchmarks.Tests/LlmAgentHarnessTests.cs
git commit -m "bench: GenerateTypedKeywordsAsync agent op (cached, content-only)"
```

---

## Task 4: Ingest wiring — `IRetriever` overload + `ScriniaRetriever`

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/IRetriever.cs`
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/ScriniaRetriever.cs:120-254`
- Test: `tests/Scrinia.Retrieval.Benchmarks.Tests/ScriniaRetrieverTests.cs`

- [ ] **Step 1: Write the failing test**

Add to `ScriniaRetrieverTests.cs`:

```csharp
    [Fact]
    public async Task Typed_keyword_value_is_retrievable_even_when_absent_from_content()
    {
        // Content never contains "zorptree"; only the typed keyword carries it.
        // Positional args: (doc, agentAnchors, agentKeywords, ct).
        await _retriever.IngestAsync(
            new HaystackDoc("d1", "the subsystem orchestrates lifecycle hooks", null),
            null,
            new[] { "topic:zorptree" });
        await _retriever.IngestAsync(new HaystackDoc("d2", "completely unrelated gardening notes", null));

        var hits = await _retriever.RetrieveAsync("zorptree", topK: 5);

        hits.Should().NotBeEmpty();
        hits[0].Id.Should().Be("d1",
            because: "the typed keyword's value must be seeded into BM25 even when absent from content");
    }
```

- [ ] **Step 2: Run the test to verify it fails**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~Typed_keyword_value_is_retrievable"`
Expected: BUILD FAILURE — no 3-arg `IngestAsync(doc, anchors, keywords)` overload.

- [ ] **Step 3: Add the ingest overload to `IRetriever.cs`**

After the existing anchors overload (lines 23-24), add:

```csharp

    /// <summary>
    /// Ingest overload that additionally accepts agent-supplied typed keywords
    /// (`entity:`/`topic:` tags). Default forwards to the anchors overload, dropping
    /// the keywords — implementations that participate in the typed-keyword experiment
    /// (currently <c>ScriniaRetriever</c>) override this to index keyword values into BM25.
    /// </summary>
    Task IngestAsync(HaystackDoc doc, IReadOnlyList<string>? anchors, IReadOnlyList<string>? keywords, CancellationToken ct = default) =>
        IngestAsync(doc, anchors, ct);
```

- [ ] **Step 4: Thread keywords through `ScriniaRetriever` ingest overloads**

Replace the three ingest entry points (lines 120-153) with:

```csharp
    public Task IngestAsync(HaystackDoc doc, CancellationToken ct = default) =>
        IngestAsync(doc, agentAnchors: null, agentKeywords: null, ct);

    /// <inheritdoc/>
    public Task IngestAsync(HaystackDoc doc, IReadOnlyList<string>? agentAnchors, CancellationToken ct = default) =>
        IngestAsync(doc, agentAnchors, agentKeywords: null, ct);

    /// <summary>
    /// Full ingest overload. Agent anchors flow into <see cref="ArtifactEntry.RetrievalAnchors"/>
    /// and seed BM25 at +2; agent typed keywords are appended to <see cref="ArtifactEntry.Keywords"/>
    /// (for the field scorer) and their tokenized values seed BM25 at +5 — prod-faithful weights
    /// (Store: agent keywords +5, anchors +2). Both null preserves baseline behavior.
    /// </summary>
    public async Task IngestAsync(
        HaystackDoc doc,
        IReadOnlyList<string>? agentAnchors,
        IReadOnlyList<string>? agentKeywords,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(doc);
        ct.ThrowIfCancellationRequested();

        if (!_chunked)
        {
            await IngestEntryAsync(doc.Id, doc.Text, doc.Timestamp, agentAnchors, agentKeywords, ct);
            return;
        }

        var windows = TextChunker.SliceWindows(doc.Text, _chunkWindowSize, _chunkOverlap);
        if (windows.Count == 0) return;
        foreach (var (idx, text) in windows)
        {
            string chunkId = $"{doc.Id}{ChunkSeparator}{idx}";
            await IngestEntryAsync(chunkId, text, doc.Timestamp, agentAnchors, agentKeywords, ct);
        }
    }
```

- [ ] **Step 5: Wire keywords into `IngestEntryAsync`**

Replace the `IngestEntryAsync` signature and its keyword/TF/entry-build section (lines 155-203) with the following. (Everything from the embedding block at line 214 onward is unchanged — do not touch it.)

```csharp
    private async Task IngestEntryAsync(
        string id, string text, DateTimeOffset? timestamp,
        IReadOnlyList<string>? agentAnchors, IReadOnlyList<string>? agentKeywords, CancellationToken ct)
    {
        string sanitized = SanitizeName(id);
        var (keywords, tf) = TextAnalysis.AnalyzeText(text);

        // Materialise agent signals as plain arrays.
        string[]? anchorSource = agentAnchors is { Count: > 0 }
            ? (agentAnchors as string[] ?? agentAnchors.ToArray())
            : null;
        string[]? keywordSource = agentKeywords is { Count: > 0 }
            ? (agentKeywords as string[] ?? agentKeywords.ToArray())
            : null;

        // Seed agent-derived tokens into the TF dict. Prod-faithful weights: anchor
        // tokens +2 (Store anchor seeding), typed-keyword VALUE tokens +5 (Store agent
        // keyword seeding). IndexTokensForKeyword strips the type prefix and tokenizes
        // the value so "topic:home renovation" seeds [home, renovation], not a dead
        // monolithic key.
        Dictionary<string, int> mergedTf = tf;
        if (anchorSource is { Length: > 0 } || keywordSource is { Length: > 0 })
        {
            mergedTf = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in tf)
            {
                mergedTf.TryGetValue(k, out int existing);
                mergedTf[k] = existing + v;
            }
            if (anchorSource is { Length: > 0 })
            {
                foreach (string anchor in anchorSource)
                    foreach (string token in TextAnalysis.Tokenize(anchor))
                    {
                        mergedTf.TryGetValue(token, out int count);
                        mergedTf[token] = count + 2;
                    }
            }
            if (keywordSource is { Length: > 0 })
            {
                foreach (string kw in keywordSource)
                    foreach (string token in TextAnalysis.IndexTokensForKeyword(kw))
                    {
                        mergedTf.TryGetValue(token, out int count);
                        mergedTf[token] = count + 5;
                    }
            }
        }

        // Entry keywords = auto-extracted ∪ typed keywords, so the field scorer's
        // exact/contains paths see the full `prefix:value` tags.
        string[] entryKeywords = keywordSource is { Length: > 0 }
            ? keywords.Concat(keywordSource).Distinct(StringComparer.OrdinalIgnoreCase).ToArray()
            : keywords;

        var entry = new ArtifactEntry(
            Name: sanitized,
            Uri: $"bench://{id}",
            OriginalBytes: text.Length,
            ChunkCount: 0,
            CreatedAt: timestamp ?? DateTimeOffset.UtcNow,
            Description: string.Empty,
            Keywords: entryKeywords,
            TermFrequencies: mergedTf,
            ContentPreview: text,
            RetrievalAnchors: anchorSource);

        lock (_ingestLock)
        {
            _store.Upsert(entry);
        }
```

(The method continues with the existing `if (_embeddingProvider is not null)` block unchanged.)

- [ ] **Step 6: Run the test to verify it passes**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~Typed_keyword_value_is_retrievable"`
Expected: PASS.

- [ ] **Step 7: Run the full retriever test class to confirm no regression**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo --filter "FullyQualifiedName~ScriniaRetrieverTests"`
Expected: PASS (existing 3 + new 1).

- [ ] **Step 8: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/IRetriever.cs benchmarks/Scrinia.Retrieval.Benchmarks/Core/ScriniaRetriever.cs tests/Scrinia.Retrieval.Benchmarks.Tests/ScriniaRetrieverTests.cs
git commit -m "bench: index agent typed keywords into BM25 (value +5) and field scorer"
```

---

## Task 5: Generate + pass typed keywords in `BenchmarkRunner`

**Files:**
- Modify: `benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs:159-185`

**Note on testing:** There is no existing `BenchmarkRunner` unit test and no fake-`IRetriever`/fake-harness scaffolding in the suite; standing one up to assert a two-line pass-through would add a heavyweight harness with no precedent. This change is a mechanical mirror of the proven anchor call, and it is exercised end-to-end by the sweep in Task 6 (the `typed` and `anchors,typed` cells will produce non-null typed-keyword cache entries and move the numbers). Validation = build green + full suite + the sweep. This is a deliberate, documented gap, not an omission.

- [ ] **Step 1: Update both ingest paths in `IngestHaystackAsync`**

Replace the method body (lines 159-185) with:

```csharp
    private static async Task IngestHaystackAsync(
        IRetriever retriever,
        IReadOnlyList<HaystackDoc> docs,
        int concurrency,
        IAgentHarness agent,
        CancellationToken ct)
    {
        if (concurrency <= 1)
        {
            foreach (var d in docs)
            {
                var anchors = await agent.GenerateAnchorsAsync(d.Text, ct);
                var typed = await agent.GenerateTypedKeywordsAsync(d.Text, ct);
                await retriever.IngestAsync(d, anchors, typed, ct);
            }
            return;
        }
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = concurrency,
            CancellationToken = ct,
        };
        await Parallel.ForEachAsync(docs, options, async (doc, c) =>
        {
            var anchors = await agent.GenerateAnchorsAsync(doc.Text, c);
            var typed = await agent.GenerateTypedKeywordsAsync(doc.Text, c);
            await retriever.IngestAsync(doc, anchors, typed, c);
        });
    }
```

- [ ] **Step 2: Build the bench project to verify it compiles**

Run: `dotnet build benchmarks/Scrinia.Retrieval.Benchmarks/Scrinia.Retrieval.Benchmarks.csproj -c Debug --nologo -v q`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Commit**

```bash
git add benchmarks/Scrinia.Retrieval.Benchmarks/Core/BenchmarkRunner.cs
git commit -m "bench: runner generates and passes typed keywords at ingest"
```

---

## Task 6: Full verification + run the four-cell sweep

**Files:** none (build + execute).

- [ ] **Step 1: Full bench test suite green**

Run: `dotnet test tests/Scrinia.Retrieval.Benchmarks.Tests/Scrinia.Retrieval.Benchmarks.Tests.csproj --nologo`
Expected: PASS, 0 failed.

- [ ] **Step 2: Confirm the local LLM endpoint is up and set the key**

The sweep uses the local `qwen3` model at `http://localhost:8080/v1`. Set the API key in the shell so it never lands in a file or shell history file:

```powershell
$env:LLAMACTL_KEY = "<paste the local llamactl key here at runtime>"
```

(Confirm the endpoint responds before launching — e.g. the model server is running.)

- [ ] **Step 3: Cell 1 — CE-alone baseline (fast; no LLM ingest)**

```powershell
./scripts/run-benchmarks.ps1 -Benchmark locomo -ExtraArgs "--granularity dialog --rerank cross-encoder --chunk prod --rerank-threads 2 --ingest-concurrency 2 --agent off"
```

Expected: a result JSON `…locomo_bm25_embeddings__chunked_ce_*.json`. Record R@5 / R@10 / R@50 / NDCG@10.

- [ ] **Step 4: Cell 2 — CE + anchors**

```powershell
./scripts/run-benchmarks.ps1 -Benchmark locomo -ExtraArgs "--granularity dialog --rerank cross-encoder --chunk prod --rerank-threads 2 --ingest-concurrency 2 --agent anchors --agent-provider openai --agent-base-url http://localhost:8080/v1 --agent-model qwen3 --agent-api-key $env:LLAMACTL_KEY"
```

Expected: `…_ce_agent-anchors_*.json`. (First cell that pays anchor generation; subsequent cells reuse the cache.)

- [ ] **Step 5: Cell 3 — CE + typed keywords**

```powershell
./scripts/run-benchmarks.ps1 -Benchmark locomo -ExtraArgs "--granularity dialog --rerank cross-encoder --chunk prod --rerank-threads 2 --ingest-concurrency 2 --agent typed --agent-provider openai --agent-base-url http://localhost:8080/v1 --agent-model qwen3 --agent-api-key $env:LLAMACTL_KEY"
```

Expected: `…_ce_agent-typed_*.json`. (Pays typed generation once.)

- [ ] **Step 6: Cell 4 — CE + both**

```powershell
./scripts/run-benchmarks.ps1 -Benchmark locomo -ExtraArgs "--granularity dialog --rerank cross-encoder --chunk prod --rerank-threads 2 --ingest-concurrency 2 --agent anchors,typed --agent-provider openai --agent-base-url http://localhost:8080/v1 --agent-model qwen3 --agent-api-key $env:LLAMACTL_KEY"
```

Expected: `…_ce_agent-anchors+typed_*.json`. (All cache hits — no new LLM calls.)

- [ ] **Step 7: Token-economy tally (proxy metric)**

The agent cache `benchmarks/Scrinia.Retrieval.Benchmarks/cache/agent-openai-qwen3.ndjson` holds the parsed outputs per op. Compare the total characters of stored `anchors` vs `typed` outputs as a proxy for generation size:

```powershell
$cache = "benchmarks/Scrinia.Retrieval.Benchmarks/cache/agent-openai-qwen3.ndjson"
Get-Content $cache | ForEach-Object { $_ | ConvertFrom-Json } |
  Group-Object op |
  ForEach-Object {
    $chars = ($_.Group | ForEach-Object { ($_.value -join ' ').Length } | Measure-Object -Sum).Sum
    [pscustomobject]@{ Op = $_.Name; Records = $_.Count; TotalChars = $chars }
  }
```

(If the ndjson field names differ, adjust `op`/`value` to match the actual cache schema — inspect the first line with `Get-Content $cache -TotalCount 1`.) Record the anchors-vs-typed character totals.

- [ ] **Step 8: Record results and decide**

Tabulate the four cells (R@5/R@10/R@50/NDCG@10) + the token tally. Apply the decision criterion from the spec: typed ≥ anchors across the recall/NDCG metrics → recommend switching the prod write slot to typed keywords (parity is a win once token savings count); `both` shows complementary vs redundant. Capture the conclusion in the benchmark methodology findings doc (`docs/research/benchmark-methodology-findings.md`) as a new finding, mirroring the existing entries' style. Commit that doc update separately.

---

## Self-Review

**Spec coverage:**
- Type vocab entity+topic → Task 2 (`TypedKeywordPrefixes`). ✓
- Generation approach B (two focused cached calls) → Task 3 (separate `typed` op, cached). ✓
- Prod-faithful weights (typed +5 / anchors +2) → Task 4 (IngestEntryAsync seeding). ✓
- CLI `anchors`/`typed`/`anchors,typed` → Task 1 (`AgentModes.Parse`). ✓
- 4-cell run plan → Task 6. ✓
- Token-economy proxy → Task 6 Step 7. ✓
- Testing (ParseTypedKeywords, AgentModes, harness op, ingest) → Tasks 1-4. ✓
- Anti-leakage (content only) → Task 3 prompt-content test. ✓
- Non-goals (no related:/date:/Core changes) → respected; no task touches them. ✓

**Placeholder scan:** The only intentional non-literal is the runtime API key (`$env:LLAMACTL_KEY`) — a secret that must NOT be committed; this is correct, not a placeholder gap. All code blocks are complete.

**Type consistency:** `GenerateTypedKeywordsAsync` signature identical across interface/NoOp/Llm/tests. `IngestAsync(doc, anchors, keywords, ct)` arg order consistent across IRetriever default, ScriniaRetriever overloads, the runner call, and the test (positional). `AgentModes` 4-arg ctor `(Anchors, Rewrite, Hyde, TypedKeywords)` used consistently. `ParseTypedKeywords` / `MaxTypedKeywordsPerDoc` / `TypedKeywordsSystem` / `TypedKeywordsUser` names match between Task 2 (definition) and Task 3 (use).
