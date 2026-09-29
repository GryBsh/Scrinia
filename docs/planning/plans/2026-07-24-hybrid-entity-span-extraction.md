# Hybrid Entity-Span Extraction Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extract entity spans from prose so the `EntityFactStore` has something to aggregate — mechanically at write time (`mention:` presence facts), then upgraded asynchronously by the embedder (typed `person/place/org/thing/topic:`).

**Architecture:** Two stages. Stage 1 is a synchronous, embedder-free rule in `AttributeExtractor` that writes capitalized-noun-phrase spans as `mention:<value>` into the existing attribute merge chain (below the agent's authoritative map). Stage 2 is a new best-effort `IMemoryEventSink` that, when an embedder is available, promotes `mention:<v>` → a typed key using hybrid typing (nearest existing entity → prototype centroid → stay `mention:`), writing back through a new atomic store primitive that fires no sinks.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions, Model2Vec/`IEmbeddingProvider`, existing `AttributeExtractor` / `KnownAttributes` / `FileMemoryStore` / `IMemoryEventSink` machinery.

**Spec:** `docs/planning/specs/2026-07-24-hybrid-entity-span-extraction-design.md`

## Global Constraints

- **Build:** `dotnet build`/`test` the in-scope projects directly, or pass `-p:NuGetAudit=false` — `Scrinia.sln` restore fails on a pre-existing AppHost MessagePack NuGet-audit error. Never `dotnet build Scrinia.sln` without that flag.
- **Merge precedence (exact):** `routedAttributes < content Extract < refAttributes < mention spans < explicit agent attributes`. An agent's explicit `person:x` must always win over a rule `mention:x`.
- **Parity:** the inline span rule is wired into **both** write call sites — `src/Scrinia.Mcp/MemoryTools.Core.cs` and `src/Scrinia.Server/Services/MemoryOrchestrator.cs`.
- **`EntityFact`, `EntityFactStore`, and the sidecar FORMAT are unchanged; sidecar version stays v1.** The data-model change is registering `mention` as multi-valued in `KnownAttributes`. One Piece-1 correctness fix is in scope (Task 4): `EntityFactSidecar.ComputeToken` becomes **content-aware** (hashes each entry's attributes, not just id + effective-date), so an attribute-only mutation invalidates `facts.json` without a timestamp bump. This is required so `TryUpdateEntry`/promotion never re-dates facts (fact dates stay = authoring date; no `UpdatedAt` stamping). Format is untouched; existing `facts.json` rebuild once on next load.
- **`TermFrequencies` is NOT recomputed on promotion** — promotion edits `Attributes` only (see spec §Write-back).
- **Typing is best-effort and `IsAvailable`-guarded:** the write never blocks on it and never fails if the embedder is absent; spans remain `mention:` and still aggregate.
- **Promotion writes back only via the atomic `TryUpdateEntry` primitive** (fires no sinks); it moves a value only if it is *still* under `mention:` on the current entry.
- **Typing thresholds are conservative** — `mention:` is the expected common case; typed keys are rare-but-right. Values are tunable consts, calibrated later on real-workflow signal, never a benchmark fit.
- **Entity value normalization is identical to `EntityRef.Of` / graph node identity:** `Trim().ToLowerInvariant()`, value kept whole (multi-word not tokenized).

---

### Task 1: Register `mention` as a multi-valued attribute key

**Files:**
- Modify: `src/Scrinia.Core/Search/KnownAttributes.cs`
- Test: `tests/Scrinia.Tests/KnownAttributesTests.cs` (create if absent; else append)

**Interfaces:**
- Consumes: nothing.
- Produces: `KnownAttributes.MentionKey` (`"mention"`); `KnownAttributes.IsMultiValued("mention") == true`; `KnownAttributes.Merge` unions `mention` values.

- [ ] **Step 1: Write the failing test**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class KnownAttributesTests
{
    [Fact]
    public void Mention_IsMultiValued_AndUnionsOnMerge()
    {
        KnownAttributes.MentionKey.Should().Be("mention");
        KnownAttributes.IsMultiValued("mention").Should().BeTrue();
        KnownAttributes.IsMultiValued("MENTION").Should().BeTrue(); // case-insensitive

        var existing = new Dictionary<string, string[]> { ["mention"] = ["seraphim"] };
        var incoming = new Dictionary<string, string[]> { ["mention"] = ["france"] };

        var merged = KnownAttributes.Merge(existing, incoming);

        merged!["mention"].Should().BeEquivalentTo(["seraphim", "france"]);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~KnownAttributesTests"`
Expected: FAIL — `MentionKey` does not exist / `IsMultiValued("mention")` is false.

- [ ] **Step 3: Add the key and include it in `IsMultiValued`**

In `KnownAttributes.cs`, add the const near `TagKey`/`AnchorKey`:

```csharp
    /// <summary>The neutral key untyped prose entity spans are written under at write time
    /// (Piece 2). Multi-valued — a memory mentions several spans. The async typing sink promotes
    /// individual values from here into the typed entity keys; anything it cannot confidently type
    /// stays here and still aggregates (it is an ordinary attribute key, so the entity graph and
    /// EntityFactStore already treat each mention:value as an entity).</summary>
    public const string MentionKey = "mention";
```

Update `IsMultiValued`:

```csharp
    /// <summary>Multi-valued keys accumulate across appends; everything else is scalar newer-wins.</summary>
    public static bool IsMultiValued(string key) =>
        EntityKeys.Contains(key)
        || ReferenceKeys.Contains(key)
        || string.Equals(key, MentionKey, StringComparison.OrdinalIgnoreCase);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~KnownAttributesTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/KnownAttributes.cs tests/Scrinia.Tests/KnownAttributesTests.cs
git commit -m "feat(attributes): register mention as a multi-valued key"
```

---

### Task 2: Mechanical span rule — `AttributeExtractor.ExtractMentionSpans`

**Files:**
- Modify: `src/Scrinia.Core/Search/AttributeExtractor.cs`
- Test: `tests/Scrinia.Tests/MentionSpanExtractionTests.cs` (create)

**Interfaces:**
- Consumes: `KnownAttributes.MentionKey` (Task 1).
- Produces: `AttributeExtractor.ExtractMentionSpans(string? content)` → `Dictionary<string,string[]>?` shaped `{ ["mention"] = normalizedValues }` or `null`; `AttributeExtractor.MaxMentionSpans` (`int`).

**Rule (stop-list guard):** a candidate span is a maximal run of consecutive capitalized word-tokens on one line, outside code fences, inline-code (backtick) spans, and YAML frontmatter. Multi-word runs are kept whole. A token joins a run only if it starts uppercase **and is not a stop-word**; stop-words break runs. The stop-list carries pronouns/articles/weekday/month names **plus common sentence-openers** (imperative verbs, conjunctions, auxiliaries, contractions) — this is what filters sentence-leading common words (`Set`, `Run`, `The`) while keeping sentence-leading **subjects** (real names like `Jolene`, real places like `France`). There is **no positional or echo logic** — it was dropped because it also dropped leading subjects, the most important entities. Values are `Trim().ToLowerInvariant()`, deduped, capped at `MaxMentionSpans`. Double-quoted prose names are kept (only code is excluded).

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

public class MentionSpanExtractionTests
{
    private static string[] Spans(string content) =>
        AttributeExtractor.ExtractMentionSpans(content)?["mention"] ?? [];

    [Fact]
    public void ExtractsProperNouns_MultiWordKeptWhole_Lowercased()
    {
        var spans = Spans("Jolene bought Seraphim in New York last week.");
        spans.Should().BeEquivalentTo(["jolene", "seraphim", "new york"]);
    }

    [Fact]
    public void LeadingSubjectsKept_CommonOpenerWordsDropped()
    {
        // Sentence-leading SUBJECTS are kept (no positional drop).
        Spans("Jolene arrived early.").Should().BeEquivalentTo(["jolene"]);
        Spans("France is nice.").Should().BeEquivalentTo(["france"]);
        // Sentence-leading common OPENER words are stop-listed, so nothing survives here.
        Spans("Set the flag. Run the migration.").Should().BeEmpty();
    }

    [Fact]
    public void CodeFencesAndInlineCode_Excluded()
    {
        Spans("```\nSeraphim Config\n```\nWe met Alice.").Should().BeEquivalentTo(["alice"]);
        Spans("Set `MaxRetries` and call Bob.").Should().BeEquivalentTo(["bob"]);
    }

    [Fact]
    public void StopWords_NeverEmitted()
    {
        Spans("The Monday meeting. I called Bob.").Should().BeEquivalentTo(["bob"]);
    }

    [Fact]
    public void NoProperNouns_ReturnsNull()
    {
        AttributeExtractor.ExtractMentionSpans("just some plain prose here").Should().BeNull();
        AttributeExtractor.ExtractMentionSpans(null).Should().BeNull();
        AttributeExtractor.ExtractMentionSpans("   ").Should().BeNull();
    }

    [Fact]
    public void CapsPerMemory()
    {
        var many = string.Join(" ", Enumerable.Range(0, 100).Select(i => $"We saw Name{i} there."));
        Spans(many).Length.Should().Be(AttributeExtractor.MaxMentionSpans);
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~MentionSpanExtractionTests"`
Expected: FAIL — `ExtractMentionSpans` not defined.

- [ ] **Step 3: Implement the rule**

Add to `AttributeExtractor` (the class is `static partial`). Uses the same fence/frontmatter tracking style as `Extract`.

```csharp
    public const int MaxMentionSpans = 32;

    // Capitalized words that are almost never a standalone prose entity: pronouns, articles,
    // weekday/month names, and common sentence-openers (imperative verbs, conjunctions, auxiliaries,
    // contractions). Matched case-sensitively against the raw (capitalized) token, so it only ever
    // affects a capitalized occurrence — a lowercase "set"/"in" mid-sentence already breaks a run.
    // This is the whole precision guard: it filters sentence-leading common words while keeping
    // sentence-leading SUBJECTS (real names/places). Tunable; extend as real usage reveals leaks.
    private static readonly HashSet<string> MentionStopWords =
        new(StringComparer.Ordinal)
        {
            "I", "A", "An", "The", "This", "That", "These", "Those", "It", "Its",
            "He", "His", "Him", "She", "Her", "They", "Their", "Them", "We", "Our", "Us", "You", "Your",
            "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
            "January", "February", "March", "April", "May", "June", "July",
            "August", "September", "October", "November", "December",
            // common sentence-openers
            "Set", "Get", "Run", "Use", "Add", "See", "Note", "Let", "Try", "Make", "Take",
            "Check", "Ensure", "Also", "Then", "When", "While", "If", "Else", "Because",
            "After", "Before", "Now", "Here", "There", "First", "Next", "Finally", "However",
            "Do", "Does", "Did", "Is", "Are", "Was", "Were", "Be", "Been", "Being",
            "Have", "Has", "Had", "Will", "Would", "Should", "Could", "Can", "May", "Might", "Must",
            "Not", "No", "Yes", "So", "But", "And", "Or", "For", "Of", "To", "Nor",
            "In", "On", "At", "By", "With", "From", "As", "Per", "Every", "Each", "Some", "Any", "All",
            "Don't", "Doesn't", "Didn't", "Can't", "Won't", "Isn't", "Aren't", "Wasn't", "Weren't",
            "It's", "That's", "There's", "Let's", "We're", "We've", "You're", "They're", "I'm", "I've",
        };

    /// <summary>
    /// Mechanical prose entity-span extraction (Piece 2, Stage 1). Emits capitalized-noun-phrase
    /// runs as <c>mention:&lt;value&gt;</c> presence facts. LLM-free; embedder-free. A run is a
    /// maximal sequence of consecutive capitalized, non-stop-word tokens taken outside code fences,
    /// inline-code spans, and YAML frontmatter; the value is the run joined and lowercased, kept
    /// whole. No positional/echo logic. Returns null when no span survives.
    /// </summary>
    public static Dictionary<string, string[]>? ExtractMentionSpans(string? content)
    {
        if (string.IsNullOrWhiteSpace(content))
            return null;

        var values = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        bool inFence = false, firstLine = true, inFrontmatter = false;
        foreach (string rawLine in content.Split('\n'))
        {
            string line = rawLine.TrimEnd('\r');
            if (line.StartsWith("```", StringComparison.Ordinal)) { inFence = !inFence; firstLine = false; continue; }
            if (inFence) { firstLine = false; continue; }
            if (line.Trim() == "---")
            {
                if (firstLine) inFrontmatter = true;
                else if (inFrontmatter) inFrontmatter = false;
                firstLine = false;
                continue;
            }
            firstLine = false;
            if (inFrontmatter) continue;

            if (!ScanMentionLine(StripInlineCode(line), values, seen))
                break; // per-memory cap reached
        }

        return values.Count > 0
            ? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase) { [KnownAttributes.MentionKey] = [.. values] }
            : null;
    }

    /// <summary>Blanks out inline `code` spans (backtick-delimited) with spaces so their tokens
    /// are never scanned, while preserving column positions.</summary>
    private static string StripInlineCode(string line)
    {
        if (line.IndexOf('`') < 0) return line;
        var chars = line.ToCharArray();
        bool inCode = false;
        for (int i = 0; i < chars.Length; i++)
        {
            if (chars[i] == '`') { inCode = !inCode; chars[i] = ' '; }
            else if (inCode) chars[i] = ' ';
        }
        return new string(chars);
    }

    /// <summary>Appends normalized mention values from one line into <paramref name="values"/>.
    /// Returns false when the per-memory cap is reached (caller stops scanning).</summary>
    private static bool ScanMentionLine(string line, List<string> values, HashSet<string> seen)
    {
        var run = new List<string>();

        bool Emit()
        {
            if (run.Count == 0) return true;
            string norm = string.Join(' ', run).Trim().ToLowerInvariant();
            run.Clear();
            if (norm.Length > 0 && seen.Add(norm))
            {
                values.Add(norm);
                if (values.Count >= MaxMentionSpans) return false;
            }
            return true;
        }

        int i = 0, n = line.Length;
        while (i < n)
        {
            char c = line[i];
            if (IsWordChar(c))
            {
                int start = i;
                while (i < n && IsWordChar(line[i])) i++;
                string token = line[start..i];
                if (char.IsUpper(token[0]) && !MentionStopWords.Contains(token))
                    run.Add(token);
                else if (!Emit())
                    return false;
                continue;
            }
            if (!Emit()) return false;
            i++;
        }
        return Emit();

        static bool IsWordChar(char ch) => char.IsLetterOrDigit(ch) || ch == '\'' || ch == '-';
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~MentionSpanExtractionTests"`
Expected: PASS (all 6).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/AttributeExtractor.cs tests/Scrinia.Tests/MentionSpanExtractionTests.cs
git commit -m "feat(extraction): mechanical prose entity-span rule (mention:)"
```

---

### Task 3: Wire the span rule into both write paths

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.Core.cs` (the entry-assembly line, currently ~192)
- Modify: `src/Scrinia.Server/Services/MemoryOrchestrator.cs` (the merge line, currently ~41)
- Test: `tests/Scrinia.Tests/MentionSpanWiringTests.cs` (create)

**Interfaces:**
- Consumes: `AttributeExtractor.ExtractMentionSpans` (Task 2), `KnownAttributes.Merge`.
- Produces: stored entries carry `mention:` attributes derived from prose; agent-supplied typed attributes override.

**Precedence:** fold `mention` spans in **below** `refAttributes` and **above** the explicit agent `attributes`. So: `Merge( Merge( Merge(routed, Extract), refAttributes), mentionSpans )` then `Merge(…, agentAttributes)`.

- [ ] **Step 1: Write the failing test** (drives the MCP path through the public tool)

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Mcp;
using Xunit;

namespace Scrinia.Tests;

public class MentionSpanWiringTests
{
    [Fact]
    public async Task Store_ExtractsMentionSpans_AgentAttributeOverrides()
    {
        using var tmp = new TempWorkspace();               // existing test helper pattern
        var tools = tmp.CreateTools();                     // resolves ScriniaMcpTools over a FileMemoryStore

        // Jolene appears only sentence-initial? No — mid-sentence, so kept. Seraphim mid-sentence.
        await tools.Remember(name: "/findings/trip",
            content: ["We saw Jolene and Seraphim in France."]);

        var entry = tmp.Store.LoadIndex("local:findings").Single(e => e.Name.EndsWith("trip"));
        entry.Attributes!["mention"].Should().Contain("jolene");
        entry.Attributes!["mention"].Should().Contain("seraphim");

        // Agent-supplied typed attribute wins: a value the agent typed is NOT duplicated under mention.
        await tools.Remember(name: "/findings/typed",
            content: ["We saw Jolene."],
            attributes: new Dictionary<string, string[]> { ["person"] = ["jolene"] });

        var typed = tmp.Store.LoadIndex("local:findings").Single(e => e.Name.EndsWith("typed"));
        typed.Attributes!["person"].Should().Contain("jolene");
        // mention may still contain other spans, but must not shadow the agent's person:jolene as a conflict.
        typed.Attributes.TryGetValue("mention", out var m);
        (m ?? []).Should().NotContain("jolene", "agent-typed value must not also appear as an untyped mention");
    }
}
```

> **Implementer note:** `TempWorkspace`/`CreateTools`/`Remember` signatures must match the existing test utilities and the current `ScriniaMcpTools` surface. Inspect a neighboring test (e.g. `ScriniaMcpToolsTests.cs`) and adapt the harness calls; the *assertions* above are the contract. To satisfy "agent value not shadowed as mention," de-duplicate: after building `mentionSpans`, drop any `mention` value that already exists under a typed entity key in the final merged attributes (see Step 3).

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~MentionSpanWiringTests"`
Expected: FAIL — no `mention` attribute present.

- [ ] **Step 3: Wire the MCP path** (`MemoryTools.Core.cs`, replace the entry-assembly block ~189-194)

```csharp
        // Assemble unified attributes: routed keywords ∪ content extraction ∪ references ∪ prose
        // mention spans, then fold in explicit agent attributes (they win scalar collisions), then
        // seed key/value tokens into TF.
        var entryAttributes = KnownAttributes.Merge(
            KnownAttributes.Merge(
                KnownAttributes.Merge(routedAttributes, AttributeExtractor.Extract(joined)),
                refAttributes),
            AttributeExtractor.ExtractMentionSpans(joined));
        entryAttributes = KnownAttributes.Merge(entryAttributes, NormalizeAttributes(attributes));
        entryAttributes = DropMentionsShadowedByTypedKeys(entryAttributes);
        AttributeExtractor.SeedTermFrequencies(entryAttributes, tf);
```

Add the helper (private static) near the other attribute helpers in `MemoryTools.Core.cs`:

```csharp
    /// <summary>Removes any <c>mention</c> value that also appears under one of the typed entity
    /// keys in the same entry — the typed key (agent-supplied or otherwise) is authoritative, and a
    /// duplicate untyped mention would double-count the entity in aggregation.</summary>
    private static Dictionary<string, string[]>? DropMentionsShadowedByTypedKeys(Dictionary<string, string[]>? attrs)
    {
        if (attrs is null || !attrs.TryGetValue(KnownAttributes.MentionKey, out var mentions) || mentions.Length == 0)
            return attrs;

        var typedValues = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in KnownAttributes.EntityKeys)
            if (attrs.TryGetValue(key, out var vals))
                foreach (var v in vals) typedValues.Add(v);

        var kept = mentions.Where(m => !typedValues.Contains(m)).ToArray();
        if (kept.Length == mentions.Length) return attrs;

        if (kept.Length == 0) attrs.Remove(KnownAttributes.MentionKey);
        else attrs[KnownAttributes.MentionKey] = kept;
        return attrs;
    }
```

- [ ] **Step 4: Wire the server path** (`MemoryOrchestrator.cs`, replace the merge line ~41)

```csharp
        var mentionAttributes = AttributeExtractor.ExtractMentionSpans(joined);
        var attributes = KnownAttributes.Merge(
            KnownAttributes.Merge(
                KnownAttributes.Merge(routedAttributes, AttributeExtractor.Extract(joined)),
                refAttributes),
            mentionAttributes);
```

> **Apply `DropMentionsShadowedByTypedKeys` at BOTH call sites, unconditionally, right after the final merge.** It is a no-op when nothing shadows, so there is no conditional to reason about. This matters because shadowing does NOT require an explicit agent-`attributes` map: `RouteKeywords` turns a `person:x`-shaped entry in `Keywords` (which `StoreRequest` DOES carry) into a typed entity value, so prose `mention:x` + keyword `person:x` collide on the server path exactly as they do on the MCP path. Extract `DropMentionsShadowedByTypedKeys` into one shared static helper on `AttributeExtractor` and call it from both — DRY over copy-paste.

- [ ] **Step 5: Run test + broader store tests to verify pass, no regressions**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~MentionSpanWiringTests|FullyQualifiedName~ScriniaMcpToolsTests"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.Core.cs src/Scrinia.Server/Services/MemoryOrchestrator.cs tests/Scrinia.Tests/MentionSpanWiringTests.cs
git commit -m "feat(extraction): write mention spans in both write paths, typed keys authoritative"
```

---

### Task 4: Atomic `TryUpdateEntry` store primitive

**Files:**
- Modify: `src/Scrinia.Core/IMemoryStore.cs` (add default-interface method)
- Modify: `src/Scrinia.Core/FileMemoryStore.cs` (atomic override)
- Test: `tests/Scrinia.Tests/TryUpdateEntryTests.cs` (create)

**Interfaces:**
- Consumes: `ArtifactEntry`, existing `Upsert`/`Remove` lock machinery.
- Produces: `bool IMemoryStore.TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)` — loads the current entry for `(subject, scope)`; if none, returns `false`; calls `mutate(current)`; if it returns `null`, returns `false` with no write; otherwise persists the returned entry and returns `true`. Fires no sinks. In `FileMemoryStore` the load-mutate-persist is atomic under the per-scope write lock and invalidates the fact store.

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Xunit;

namespace Scrinia.Tests;

public class TryUpdateEntryTests
{
    [Fact]
    public void MutateReturningEntry_Persists_AndInvalidatesFactStore()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(new ArtifactEntry("note", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new() { ["mention"] = ["seraphim"] }), "local:findings");

        var before = store.GetEntityFactStore().MentionCount(new EntityRef("mention", "seraphim"));
        before.Should().Be(1);

        bool changed = store.TryUpdateEntry("note", "local:findings", e =>
        {
            var attrs = new Dictionary<string, string[]>(e.Attributes!, StringComparer.OrdinalIgnoreCase)
            {
                ["thing"] = ["seraphim"],
            };
            attrs.Remove("mention");
            return e with { Attributes = attrs };
        });

        changed.Should().BeTrue();
        var reloaded = store.LoadIndex("local:findings").Single(x => x.Name == "note");
        reloaded.Attributes!.Should().ContainKey("thing").And.NotContainKey("mention");
        // fact store rebuilt: mention gone, thing present
        store.GetEntityFactStore().MentionCount(new EntityRef("mention", "seraphim")).Should().Be(0);
        store.GetEntityFactStore().MentionCount(new EntityRef("thing", "seraphim")).Should().Be(1);
    }

    [Fact]
    public void MutateReturningNull_WritesNothing()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(new ArtifactEntry("note", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new() { ["mention"] = ["seraphim"] }), "local:findings");

        bool changed = store.TryUpdateEntry("note", "local:findings", _ => null);

        changed.Should().BeFalse();
        store.LoadIndex("local:findings").Single(x => x.Name == "note")
            .Attributes!.Should().ContainKey("mention");
    }

    [Fact]
    public void MissingEntry_ReturnsFalse()
    {
        using var tmp = new TempWorkspace();
        tmp.Store.TryUpdateEntry("nope", "local:findings", e => e).Should().BeFalse();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~TryUpdateEntryTests"`
Expected: FAIL — `TryUpdateEntry` not defined.

- [ ] **Step 3: Add the default-interface method** (`IMemoryStore.cs`, in the CRUD region)

```csharp
    /// <summary>
    /// Atomically load the entry for <paramref name="subject"/> in <paramref name="scope"/>, apply
    /// <paramref name="mutate"/>, and persist the result. Returns false when the entry is missing or
    /// <paramref name="mutate"/> returns null (no write). Fires NO event sinks — it is the seam the
    /// async typing sink uses to write promotions back without re-entering the sink pipeline.
    /// <para>Default implementation is a non-atomic load→mutate→Upsert, adequate for in-memory/test
    /// stores with no concurrent writers. FileMemoryStore overrides it to run under its per-scope
    /// write lock.</para>
    /// </summary>
    bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        var current = LoadIndex(scope).FirstOrDefault(e => SanitizeName(e.Name) == SanitizeName(subject));
        if (current is null) return false;
        var updated = mutate(current);
        if (updated is null) return false;
        Upsert(updated, scope);
        return true;
    }
```

- [ ] **Step 4: Add the atomic override** (`FileMemoryStore.cs`)

Mirror `Remove`'s identity resolution (meta path) and `Upsert`'s persist/invalidate tail, under one lock acquisition:

```csharp
    public bool TryUpdateEntry(string subject, string scope, Func<ArtifactEntry, ArtifactEntry?> mutate)
    {
        using var fileLock = FileLock.AcquireExclusive(GetLockPath(scope));
        var lk = GetIndexLock(scope);
        lk.EnterWriteLock();
        try
        {
            string storeDir = GetStoreDirForScope(scope);
            string metaPath = Path.Combine(storeDir, SanitizeName(subject) + ".meta.json");
            if (!File.Exists(metaPath)) return false;

            var current = LoadEntriesFromSidecars(storeDir)
                .Select(e => e.NormalizeAttributesKey())
                .FirstOrDefault(e => SanitizeName(e.Name) == SanitizeName(subject));
            if (current is null) return false;

            var updated = mutate(current);
            if (updated is null) return false;

            WriteSidecar(updated, storeDir);

            _indexCache.TryRemove(scope, out _);
            var entries = LoadEntriesFromSidecars(storeDir).Select(e => e.NormalizeAttributesKey()).ToList();
            string? legacyDir = ResolveLegacyFallbackDir(scope);
            if (legacyDir is not null && Directory.Exists(legacyDir))
            {
                var legacyEntries = LoadEntriesFromSidecars(legacyDir);
                var namespacedNames = new HashSet<string>(entries.Select(e => e.Name), StringComparer.OrdinalIgnoreCase);
                foreach (var le in legacyEntries)
                    if (!namespacedNames.Contains(le.Name)) entries.Add(le.NormalizeAttributesKey());
            }
            _indexCache[scope] = new CachedIndex(entries);

            if (scope.StartsWith("local:", StringComparison.Ordinal)) _cachedTopics = null;
            lock (_factStoreLock) { _factStore = null; _factStoreGeneration++; }
            return true;
        }
        finally
        {
            lk.ExitWriteLock();
        }
    }
```

> **Implementer note:** confirm the exact names `LoadEntriesFromSidecars`, `WriteSidecar`, `GetLockPath`, `GetIndexLock`, `ResolveLegacyFallbackDir`, `_indexCache`, `_cachedTopics`, `CachedIndex`, `_factStoreLock`, `_factStoreGeneration` against the current `FileMemoryStore.cs` (they are the exact members `Upsert`/`Remove` use — copy from there). This method is `Upsert`'s body with a load+mutate spliced in before `WriteSidecar`.

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~TryUpdateEntryTests"`
Expected: PASS (all 3).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/IMemoryStore.cs src/Scrinia.Core/FileMemoryStore.cs tests/Scrinia.Tests/TryUpdateEntryTests.cs
git commit -m "feat(store): atomic TryUpdateEntry primitive (fires no sinks)"
```

---

### Task 5: `EntityTypeClassifier` — hybrid typing (nearest → centroid → none)

**Files:**
- Create: `src/Scrinia.Core/Search/EntityTypeClassifier.cs`
- Test: `tests/Scrinia.Tests/EntityTypeClassifierTests.cs` (create)

**Interfaces:**
- Consumes: `IEmbeddingProvider` (`EmbedAsync`), `KnownAttributes.EntityKeys`.
- Produces:
  - `sealed class EntityTypeClassifier(IEmbeddingProvider provider)`
  - `Task<string?> TypeAsync(string value, IReadOnlyList<(string Value, string Type)> existingTyped, CancellationToken ct)` — returns a typed key from `EntityKeys`, or `null` to stay `mention:`.

**Behavior:** embed `value` (cached by value string). (1) Nearest-existing: among `existingTyped`, find the max cosine to `value`; if `>= LinkThreshold`, return that neighbor's type. (2) Centroid: cosine to each per-type centroid (seed vocab embedded once, cached); if the best `>= CentroidThreshold` and beats the second-best by `>= Margin`, return that type. (3) Else `null`. Conservative thresholds.

- [ ] **Step 1: Write the failing tests** (with a deterministic stub embedder)

```csharp
using FluentAssertions;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Search;
using Xunit;

namespace Scrinia.Tests;

// Deterministic stub: returns a caller-provided vector per exact text; unknown text -> zero vector.
internal sealed class DictEmbeddingProvider : IEmbeddingProvider
{
    private readonly Dictionary<string, float[]> _map;
    private readonly int _dim;
    public DictEmbeddingProvider(Dictionary<string, float[]> map, int dim) { _map = map; _dim = dim; }
    public bool IsAvailable => true;
    public int Dimensions => _dim;
    public string Signature => "stub:dict";
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
        Task.FromResult<float[]?>(_map.TryGetValue(text, out var v) ? v : new float[_dim]);
    public void Dispose() { }
}

public class EntityTypeClassifierTests
{
    private static EntityTypeClassifier Make(Dictionary<string, float[]> map) =>
        new(new DictEmbeddingProvider(map, 3));

    [Fact]
    public async Task NearestExistingEntity_Wins()
    {
        // "seraphim" identical vector to an existing thing:seraphim -> typed thing.
        var map = new Dictionary<string, float[]>
        {
            ["seraphim"] = [0f, 0f, 1f],
            ["susie"] = [0f, 0f, 1f],
        };
        var c = Make(map);
        var t = await c.TypeAsync("seraphim", [("susie", "thing")], default);
        t.Should().Be("thing");
    }

    [Fact]
    public async Task CentroidFallback_WhenNoNeighbor()
    {
        // Give the classifier a place-pointing value and no existing typed entities.
        var map = new Dictionary<string, float[]> { ["france"] = [0f, 1f, 0f] };
        // The classifier embeds its own seed vocab; make the place seeds point at [0,1,0].
        foreach (var seed in EntityTypeClassifier.SeedVocab["place"]) map[seed] = [0f, 1f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["person"]) map[seed] = [1f, 0f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["thing"]) map[seed] = [0f, 0f, 1f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["org"]) map[seed] = [1f, 1f, 0f];
        foreach (var seed in EntityTypeClassifier.SeedVocab["topic"]) map[seed] = [0f, 1f, 1f];

        var c = Make(map);
        (await c.TypeAsync("france", [], default)).Should().Be("place");
    }

    [Fact]
    public async Task BelowThreshold_ReturnsNull()
    {
        // Zero vector for the value -> no cosine anywhere -> stays mention.
        var c = Make(new Dictionary<string, float[]>());
        (await c.TypeAsync("weirdtoken", [], default)).Should().BeNull();
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityTypeClassifierTests"`
Expected: FAIL — `EntityTypeClassifier` not defined.

- [ ] **Step 3: Implement the classifier**

```csharp
using System.Collections.Concurrent;
using Scrinia.Core.Embeddings;

namespace Scrinia.Core.Search;

/// <summary>
/// Hybrid entity typing for prose spans (Piece 2, Stage 2): nearest existing typed entity, then
/// prototype centroid, then null (stay <c>mention:</c>). Static bag-of-words embeddings type only
/// roughly, so thresholds are deliberately conservative — mention: is the expected common case.
/// Seeds are tunable on real-workflow signal, never a benchmark fit.
/// </summary>
public sealed class EntityTypeClassifier(IEmbeddingProvider provider)
{
    // Conservative floors; calibrate on real Model2Vec vectors later.
    private const double LinkThreshold = 0.60;
    private const double CentroidThreshold = 0.50;
    private const double Margin = 0.05;

    /// <summary>Per-type prototype seed vocabulary. Keys are <see cref="KnownAttributes.EntityKeys"/>.</summary>
    public static readonly IReadOnlyDictionary<string, string[]> SeedVocab =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            ["person"] = ["person", "people", "man", "woman", "name", "he", "she"],
            ["place"]  = ["place", "city", "country", "location", "region", "town"],
            ["org"]    = ["organization", "company", "corporation", "team", "agency", "institution"],
            ["thing"]  = ["thing", "object", "item", "device", "product", "tool"],
            ["topic"]  = ["topic", "subject", "concept", "theme", "idea", "matter"],
        };

    private readonly ConcurrentDictionary<string, float[]?> _vectorCache = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, float[]>? _centroids;

    public async Task<string?> TypeAsync(
        string value, IReadOnlyList<(string Value, string Type)> existingTyped, CancellationToken ct)
    {
        float[]? vec = await EmbedCachedAsync(value, ct);
        if (vec is null || IsZero(vec)) return null;

        // (1) nearest existing typed entity
        string? bestType = null;
        double bestSim = double.NegativeInfinity;
        foreach (var (ev, et) in existingTyped)
        {
            if (string.Equals(ev, value, StringComparison.OrdinalIgnoreCase)) { /* same token, different entry */ }
            float[]? evVec = await EmbedCachedAsync(ev, ct);
            if (evVec is null || IsZero(evVec)) continue;
            double sim = Cosine(vec, evVec);
            if (sim > bestSim) { bestSim = sim; bestType = et; }
        }
        if (bestType is not null && bestSim >= LinkThreshold) return bestType;

        // (2) prototype centroid
        var centroids = await GetCentroidsAsync(ct);
        string? topType = null, secondType = null;
        double top = double.NegativeInfinity, second = double.NegativeInfinity;
        foreach (var (type, cen) in centroids)
        {
            double sim = Cosine(vec, cen);
            if (sim > top) { second = top; secondType = topType; top = sim; topType = type; }
            else if (sim > second) { second = sim; secondType = type; }
        }
        if (topType is not null && top >= CentroidThreshold && (top - second) >= Margin)
            return topType;

        // (3) stay mention:
        return null;
    }

    private async Task<IReadOnlyDictionary<string, float[]>> GetCentroidsAsync(CancellationToken ct)
    {
        if (_centroids is not null) return _centroids;
        var built = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (type, seeds) in SeedVocab)
        {
            var vecs = new List<float[]>();
            foreach (var s in seeds)
            {
                var v = await EmbedCachedAsync(s, ct);
                if (v is not null && !IsZero(v)) vecs.Add(v);
            }
            if (vecs.Count > 0) built[type] = Mean(vecs);
        }
        _centroids = built;
        return built;
    }

    private async Task<float[]?> EmbedCachedAsync(string text, CancellationToken ct)
    {
        if (_vectorCache.TryGetValue(text, out var cached)) return cached;
        var v = await provider.EmbedAsync(text, ct);
        _vectorCache[text] = v;
        return v;
    }

    private static float[] Mean(List<float[]> vecs)
    {
        int dim = vecs[0].Length;
        var acc = new float[dim];
        foreach (var v in vecs) for (int i = 0; i < dim; i++) acc[i] += v[i];
        for (int i = 0; i < dim; i++) acc[i] /= vecs.Count;
        return acc;
    }

    private static bool IsZero(float[] v)
    {
        foreach (var x in v) if (x != 0f) return false;
        return true;
    }

    private static double Cosine(float[] a, float[] b)
    {
        double dot = 0, na = 0, nb = 0;
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
        if (na == 0 || nb == 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
```

> **Implementer note:** if a cosine helper already exists in `Scrinia.Core.Embeddings` (e.g. used by `HybridReranker`), use it instead of the private `Cosine` here (DRY). Keep the classifier's public surface (`TypeAsync`, `SeedVocab`) unchanged.

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityTypeClassifierTests"`
Expected: PASS (all 3).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Core/Search/EntityTypeClassifier.cs tests/Scrinia.Tests/EntityTypeClassifierTests.cs
git commit -m "feat(typing): EntityTypeClassifier — nearest-entity then centroid, conservative floor"
```

---

### Task 6: `EntityTypingEventHandler` — the async promotion sink

**Files:**
- Create: `src/Scrinia/Services/EntityTypingEventHandler.cs`
- Test: `tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs` (create)

**Interfaces:**
- Consumes: `IEmbeddingProvider`, `EntityTypeClassifier` (Task 5), `IMemoryStore.TryUpdateEntry` (Task 4), `KnownAttributes`.
- Produces: `sealed class EntityTypingEventHandler(IEmbeddingProvider provider) : IMemoryEventSink`.

**Flow (OnStored/OnAppended):** if `!provider.IsAvailable` return. Resolve `(scope, subject)`; read the current entry's `mention` values (via `LoadIndex` + `SanitizeName` match); if none, return. Gather existing typed entities from `store.ListScoped(null)` (values under `EntityKeys`). For each mention value, `await classifier.TypeAsync(value, existingTyped, ct)`. Collect the `value → type` moves. If any, call `store.TryUpdateEntry(subject, scope, mutate)` where `mutate` (sync, under the store lock) re-reads the current entry and moves **only** values still under `mention:`. `OnForgotten`: no-op. All wrapped best-effort (the sink pipeline already try/catches, but guard defensively).

- [ ] **Step 1: Write the failing tests**

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public class EntityTypingEventHandlerTests
{
    private static ArtifactEntry Mentioning(string name, params string[] mentions) =>
        new(name, "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = mentions });

    [Fact]
    public async Task Promotes_MentionToTypedKey_ViaNearestEntity()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        // an existing typed entity to link against
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["thing"] = ["susie"] }), "local:findings");
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f], ["susie"] = [0f, 0f, 1f] };
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "note");
        reloaded.Attributes!.TryGetValue("mention", out var m);
        (m ?? []).Should().NotContain("seraphim");
        reloaded.Attributes!["thing"].Should().Contain("seraphim");
    }

    [Fact]
    public async Task EmbedderAbsent_NoOp_MentionRemains()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var sink = new EntityTypingEventHandler(new UnavailableEmbeddingProvider());
        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        store.LoadIndex("local:findings").Single(e => e.Name == "note")
            .Attributes!["mention"].Should().Contain("seraphim");
    }

    [Fact]
    public async Task ConcurrentEdit_RemovedMention_NotClobbered()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(Mentioning("note", "seraphim"), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f] };
        foreach (var s in EntityTypeClassifier.SeedVocab["thing"]) map[s] = [0f, 0f, 1f];
        var sink = new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3));

        // Simulate a user edit between the sink's read and its write: replace the whole entry so
        // "seraphim" is no longer under mention (content changed to something without it).
        store.Upsert(new ArtifactEntry("note", "uri", 2, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = ["different"] }), "local:findings");

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        // The newer write is intact; no stale "seraphim" resurrected.
        var reloaded = store.LoadIndex("local:findings").Single(e => e.Name == "note");
        reloaded.Attributes!["mention"].Should().Contain("different");
        reloaded.Attributes!.Should().NotContainKey("thing");
    }
}

internal sealed class UnavailableEmbeddingProvider : Scrinia.Core.Embeddings.IEmbeddingProvider
{
    public bool IsAvailable => false;
    public int Dimensions => 3;
    public string Signature => "stub:unavailable";
    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) => Task.FromResult<float[]?>(null);
    public void Dispose() { }
}
```

> **Implementer note:** `TempWorkspace.Store` must be a `FileMemoryStore` (so the atomic `TryUpdateEntry` override and `GetEntityFactStore` are exercised). Reuse `DictEmbeddingProvider` from Task 5's test file (same test assembly — do not redefine it; if Task 5's is `internal` in the same assembly it is visible here). Match `OnStoredAsync`'s qualified-name format to what `store.ParseQualifiedName` expects — `/findings/note` round-trips to `("local:findings","note")`; verify against `FileMemoryStore.ParseQualifiedName`.

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityTypingEventHandlerTests"`
Expected: FAIL — `EntityTypingEventHandler` not defined.

- [ ] **Step 3: Implement the sink**

```csharp
using Scrinia.Core;
using Scrinia.Core.Embeddings;
using Scrinia.Core.Models;
using Scrinia.Core.Search;

namespace Scrinia.Services;

/// <summary>
/// Best-effort <see cref="IMemoryEventSink"/> that upgrades untyped <c>mention:</c> prose spans to
/// typed entity keys using <see cref="EntityTypeClassifier"/> (Piece 2, Stage 2). Runs out-of-band
/// after a write; guarded by <see cref="IEmbeddingProvider.IsAvailable"/>; writes back only via
/// <see cref="IMemoryStore.TryUpdateEntry"/> (fires no sinks). Never blocks or fails the write —
/// on the embedder being absent or unsure, spans simply stay <c>mention:</c>.
/// </summary>
public sealed class EntityTypingEventHandler(IEmbeddingProvider provider) : IMemoryEventSink
{
    private readonly EntityTypeClassifier _classifier = new(provider);

    public Task OnStoredAsync(string qualifiedName, string[] content, IMemoryStore store, CancellationToken ct)
        => PromoteAsync(qualifiedName, store, ct);

    public Task OnAppendedAsync(string qualifiedName, string content, IMemoryStore store, CancellationToken ct)
        => PromoteAsync(qualifiedName, store, ct);

    public Task OnForgottenAsync(string qualifiedName, bool wasDeleted, IMemoryStore store, CancellationToken ct)
        => Task.CompletedTask;

    private async Task PromoteAsync(string qualifiedName, IMemoryStore store, CancellationToken ct)
    {
        if (!provider.IsAvailable) return;

        var (scope, subject) = store.ParseQualifiedName(qualifiedName);

        var entry = store.LoadIndex(scope)
            .FirstOrDefault(e => store.SanitizeName(e.Name) == store.SanitizeName(subject));
        if (entry?.Attributes is null) return;
        if (!entry.Attributes.TryGetValue(KnownAttributes.MentionKey, out var mentions) || mentions.Length == 0)
            return;

        // Existing typed vocabulary across all scopes (values under the five entity keys).
        var existingTyped = new List<(string Value, string Type)>();
        foreach (var scoped in store.ListScoped(null))
        {
            if (scoped.Entry.Attributes is null) continue;
            foreach (var key in KnownAttributes.EntityKeys)
                if (scoped.Entry.Attributes.TryGetValue(key, out var vals))
                    foreach (var v in vals) existingTyped.Add((v, key));
        }

        // Classify (async, no lock held).
        var moves = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var value in mentions)
        {
            string? type = await _classifier.TypeAsync(value, existingTyped, ct);
            if (type is not null) moves[value] = type;
        }
        if (moves.Count == 0) return;

        // Apply under the store lock; move only values STILL under mention: on the current entry.
        store.TryUpdateEntry(subject, scope, current =>
        {
            if (current.Attributes is null ||
                !current.Attributes.TryGetValue(KnownAttributes.MentionKey, out var curMentions))
                return null;

            var attrs = new Dictionary<string, string[]>(current.Attributes, StringComparer.OrdinalIgnoreCase);
            var remaining = new List<string>();
            bool moved = false;

            foreach (var m in curMentions)
            {
                if (moves.TryGetValue(m, out var type))
                {
                    var existing = attrs.TryGetValue(type, out var tv) ? tv.ToList() : [];
                    if (!existing.Contains(m, StringComparer.OrdinalIgnoreCase)) existing.Add(m);
                    attrs[type] = [.. existing];
                    moved = true;
                }
                else
                {
                    remaining.Add(m);
                }
            }

            if (!moved) return null;

            if (remaining.Count == 0) attrs.Remove(KnownAttributes.MentionKey);
            else attrs[KnownAttributes.MentionKey] = [.. remaining];

            return current with { Attributes = attrs };
        });
    }
}
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityTypingEventHandlerTests"`
Expected: PASS (all 3).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia/Services/EntityTypingEventHandler.cs tests/Scrinia.Tests/EntityTypingEventHandlerTests.cs
git commit -m "feat(typing): async EntityTypingEventHandler promotes mention: spans"
```

---

### Task 7: Wire the typing sink into the CLI embedder paths

**Files:**
- Modify: `src/Scrinia/Commands/WorkspaceSetup.cs` (the two `CompositeEventSink` constructions where a `provider` exists — currently ~249 HTTP and ~292 built-in)
- Test: `tests/Scrinia.Tests/EntityTypingWiringTests.cs` (create)

**Interfaces:**
- Consumes: `EntityTypingEventHandler` (Task 6), the local `provider` / `eventHandler` in `WorkspaceSetup`.
- Produces: the process-default event sink includes `EntityTypingEventHandler` whenever an embedder is available.

- [ ] **Step 1: Write the failing test** (end-to-end through a composed sink)

```csharp
using FluentAssertions;
using Scrinia.Core;
using Scrinia.Core.Models;
using Scrinia.Core.Search;
using Scrinia.Services;
using Xunit;

namespace Scrinia.Tests;

public class EntityTypingWiringTests
{
    [Fact]
    public async Task CompositeSink_Containing_TypingHandler_PromotesEndToEnd()
    {
        using var tmp = new TempWorkspace();
        var store = tmp.Store;
        store.Upsert(new ArtifactEntry("known", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["thing"] = ["susie"] }), "local:findings");
        store.Upsert(new ArtifactEntry("note", "uri", 1, 1, DateTimeOffset.UtcNow,
            Attributes: new(StringComparer.OrdinalIgnoreCase) { ["mention"] = ["seraphim"] }), "local:findings");

        var map = new Dictionary<string, float[]> { ["seraphim"] = [0f, 0f, 1f], ["susie"] = [0f, 0f, 1f] };
        IMemoryEventSink sink = new CompositeEventSink([
            new EntityTypingEventHandler(new DictEmbeddingProvider(map, 3)),
            new MaintenanceEventSink(),
        ]);

        await sink.OnStoredAsync("/findings/note", ["We saw Seraphim."], store, default);

        store.LoadIndex("local:findings").Single(e => e.Name == "note")
            .Attributes!["thing"].Should().Contain("seraphim");
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityTypingWiringTests"`
Expected: it should actually PASS at the sink level already (it constructs the composite directly). If so, this test guards the composition shape; proceed to also update production wiring in Step 3 and rely on the existing suite for regression. (If it fails to compile because of a namespace/using, fix and continue.)

- [ ] **Step 3: Add the typing sink to both provider-bearing composition sites** (`WorkspaceSetup.cs`)

At the built-in path (~292) change:

```csharp
                MemoryEventSinkContext.Default = new CompositeEventSink([eventHandler, new MaintenanceEventSink()]);
```

to:

```csharp
                MemoryEventSinkContext.Default = new CompositeEventSink([
                    eventHandler,
                    new EntityTypingEventHandler(provider),
                    new MaintenanceEventSink(),
                ]);
```

Apply the identical change at the HTTP path (~249), which also has `eventHandler` and `provider` in scope.

> Leave the no-embedder composition sites (plugin-exe ~89/~369, HTTP-unavailable ~263, catch ~272, server `Program.cs`) unchanged — they have no local `IEmbeddingProvider`, and typing is best-effort enrichment. Server typing-sink wiring is a documented follow-up (see Deferred).

- [ ] **Step 4: Run test + build to verify pass**

Run: `dotnet build src/Scrinia/Scrinia.csproj -p:NuGetAudit=false && dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false --filter "FullyQualifiedName~EntityTypingWiringTests"`
Expected: build succeeds; test PASSES.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia/Commands/WorkspaceSetup.cs tests/Scrinia.Tests/EntityTypingWiringTests.cs
git commit -m "feat(typing): wire EntityTypingEventHandler into CLI embedder paths"
```

---

## Deferred (documented, not built here)

- **Server typing-sink wiring** (`Program.cs`): the server composes its sink from `BuiltInEmbeddingsService`; wiring the typing sink there needs an `IEmbeddingProvider` handle in that path. Follow-up. The inline span rule (Task 3) already runs on the server write path, so server writes still get `mention:` spans — only the async promotion is CLI-only for now.
- **In-content event-dates and quantities** — the whole date/quantity family (`EntityFact.EventDate`, `EntityFact.Quantity`) is deferred to a future faithful-binding piece per the spec's Fidelity discipline.

## Final full-suite gate (run before the whole-branch review)

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj -p:NuGetAudit=false`
Expected: green. Then the server project if touched: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj -p:NuGetAudit=false`.

## Self-Review (author checklist — completed)

- **Spec coverage:** Stage-1 rule → Task 2; `mention` multi-valued → Task 1; both write paths → Task 3; hybrid typing (nearest→centroid→mention) → Task 5; async sink w/ agent-authoritative + idempotent + concurrency-safe → Task 6; `TryUpdateEntry` atomic, no-sink, invalidates fact store → Task 4; wiring → Task 7; TF-left-intact & sidecar-v1 & no `EntityFact` change → Global Constraints + no task touches them. Event-dates/quantities → Deferred (matches spec).
- **Placeholder scan:** none — every code step carries complete code; the two "Implementer note" blocks resolve real identity-matching ambiguities against existing members, not TODOs.
- **Type consistency:** `ExtractMentionSpans`, `MaxMentionSpans`, `MentionKey`, `TryUpdateEntry(subject, scope, mutate)`, `EntityTypeClassifier.TypeAsync(value, existingTyped, ct)` / `SeedVocab`, `EntityTypingEventHandler(provider)` are used identically across tasks.
