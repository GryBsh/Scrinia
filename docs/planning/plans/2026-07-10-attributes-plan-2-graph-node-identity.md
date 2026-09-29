# Attributes Model — Plan 2: Uniform graph node identity Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make `EntityGraph` node identity uniformly `type:value` for every attribute key, closing the cross-link gap Plan 1's uniform feed opened (arbitrary keys like `status:done` + `outcome:done` currently collapse to a shared bare `"done"` node and falsely link).

**Architecture:** Replace the two-pass conflict-split heuristic (`Build` pass 1 `realTypesByValue` + `ResolveNodeKey`'s "split to `type:value` only when 2+ RealTypes dispute a value") with a flat `type:value` node for every `(type, value)`. Update `SurfaceOf` to strip the type prefix for **any** key (not just RealTypes) so seeding still finds nodes by their bare value. This is a **net simplification** — it removes machinery.

**Tech Stack:** C# / .NET 10, xUnit + FluentAssertions (`tests/Scrinia.Tests`).

## Global Constraints

- **NON-PARITY, by design.** This deliberately changes the live entity-graph signal: (1) non-RealType keys become type-scoped (the fix — they stop cross-linking on a shared value); (2) the legacy `entity:` marker becomes an `entity:value` node and **no longer bridges** to typed entities (`entity:paris` ≠ `place:paris`). Both are intended (more conservative; migration converts legacy data anyway). Existing tests that assert the OLD conflict-split/bare-value behavior must be **reconciled to the new behavior — updated, never weakened to pass trivially.**
- Approved product surgery on `Scrinia.Core`. Do NOT touch the bench WIP files (`.gitignore`, `benchmarks/.../AgenticIngestPrompts.cs`, `LlmAgentHarness.cs`, `ScriniaRetriever.cs`, `OpenAiCompatibleBenchLlmClient.cs`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `benchmarks/.../tools/`, `package.json`).
- Do NOT change PPR propagation (`Score`'s power iteration), `AddDoc`, `ParseTypedEntities`, or `ParseEntityFields` — only node-key *construction* (`Build`/`ResolveNodeKey`) and the seeding *surface* (`SurfaceOf`).
- `RealTypes` stays (still used by `ParseTypedEntities` to recognize legacy typed keywords) — only its doc updates.
- `TreatWarningsAsErrors` is on for `Scrinia.Core`; if an analyzer blocks a build, adapt minimally and disclose it in the report.
- Commit trailer, exactly: `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>` then `Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g`
- Never push. Branch off `main` before starting.

## File Structure

- `src/Scrinia.Core/Search/EntityGraph.cs` — MODIFY: `Build` (3-tuple overload) drops the two-pass; remove `ResolveNodeKey`; rewrite `SurfaceOf`; refresh class-level, `RealTypes`, and `Build` XML docs to the uniform model.
- `tests/Scrinia.Tests/EntityGraphTests.cs` — MODIFY: add the new type-scoping/seeding tests; rewrite `Build_LegacyEntity_...`; refresh stale "split/conflict/bare" names+comments on tests whose assertions still pass.
- Reconcile any other suite that breaks: run the full `tests/Scrinia.Tests` suite; `EntityGraphSourceSearchTests.cs` / `EntityGraphScoreContributorTests.cs` may assert old node behavior — read and update them to the uniform model if they fail.

---

### Task 1: Uniform `type:value` node identity

**Files:**
- Modify: `src/Scrinia.Core/Search/EntityGraph.cs`
- Test: `tests/Scrinia.Tests/EntityGraphTests.cs`

**Interfaces:**
- Consumes: `ParseTypedEntities`, `ParseEntityFields` (unchanged), `AddDoc`, `Score` (unchanged).
- Produces: `EntityGraph.Build(...)` now assigns every `(type, value)` the node key `"{type}:{value}"`; `ResolveNodeKey` is removed; `SurfaceOf` strips the first `type:` prefix for any node.

- [ ] **Step 1: Write the failing tests (the fix + the intended behavior change)**

Add these to `tests/Scrinia.Tests/EntityGraphTests.cs` (existing class). They use the 3-tuple `Build` overload `(DocKey, Keywords, Fields)`; `"zz…"` values are seed anchors exclusive to one doc so PPR reachability proves (or disproves) a link.

```csharp
    [Fact]
    public void Build_NonRealTypeKeys_AreTypeScoped_DoNotCrossLinkOnSharedValue()
    {
        // status:done and outcome:done share the VALUE "done" but different keys -> distinct nodes.
        // Seeded via statusDoc-exclusive "zztarget": if the two collapsed to one "done" node,
        // outcomeDoc would be reached. Type-scoped, it must not be.
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)[]
        {
            ("statusDoc", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" }, ["thing"] = new[] { "zztarget" } }),
            ("outcomeDoc", null, new Dictionary<string, string[]> { ["outcome"] = new[] { "done" } }),
        });

        var scores = g.Score("what about zztarget", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("statusDoc");
        scores.Should().NotContainKey("outcomeDoc");   // no false bridge via a shared bare "done"
    }

    [Fact]
    public void Build_SameKeyValue_StillLinks()
    {
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)[]
        {
            ("docA", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" }, ["thing"] = new[] { "zzseed" } }),
            ("docB", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" } }),
        });

        var scores = g.Score("about zzseed", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("docB");   // reached via zzseed -> docA -> status:done -> docB
    }

    [Fact]
    public void Build_NonRealTypeNode_SeedsFromBareValue()
    {
        // SurfaceOf now strips ANY type prefix, so a status:done node seeds from the query word "done".
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?, IReadOnlyDictionary<string, string[]>?)[]
        {
            ("doc", null, new Dictionary<string, string[]> { ["status"] = new[] { "done" } }),
        });

        var scores = g.Score("is it done yet", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("doc");   // seeded via SurfaceOf("status:done") == "done"
    }
```

And REWRITE the existing `Build_LegacyEntity_FoldsToBareValue_AndNeverSplits` (its premise is now false) to:

```csharp
    [Fact]
    public void Build_LegacyEntity_IsTypeScoped_NoLongerBridgesToTypedEntity()
    {
        // Under uniform (key,value) identity, the legacy entity: marker is a normal "entity:value"
        // node -- it no longer folds to a bare value, so entity:paris and place:paris are DISTINCT
        // nodes and do not link. (More conservative; migration converts legacy data anyway.)
        var g = EntityGraph.Build(new (string, IReadOnlyList<string>?)[]
        {
            ("legacyDoc", new[] { "entity:paris", "entity:zzsecret" }),
            ("placeDoc", new[] { "place:paris" }),
        });

        // Seed ONLY via the legacy-exclusive value "zzsecret". Old behavior bridged
        // legacyDoc -> (shared bare "paris") -> placeDoc; uniform identity makes entity:paris and
        // place:paris distinct, so placeDoc is unreachable.
        var scores = g.Score("tell me about zzsecret", alpha: 0.15, iterations: 20);

        scores.Should().ContainKey("legacyDoc");
        scores.Should().NotContainKey("placeDoc");   // no false bridge
    }
```

- [ ] **Step 2: Run the new tests to verify they FAIL**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~Build_NonRealTypeKeys_AreTypeScoped|FullyQualifiedName~Build_NonRealTypeNode_SeedsFromBareValue|FullyQualifiedName~Build_LegacyEntity_IsTypeScoped"`
Expected: FAIL — today `status:done`/`outcome:done` collapse to bare `"done"` (so `outcomeDoc` IS reached → cross-link test fails), `SurfaceOf("status:done")` returns the whole key (so the seed-from-value test fails), and legacy `entity:paris` bridges to `place:paris` (so the non-bridge test fails). `Build_SameKeyValue_StillLinks` should already PASS (same key+value links under both models).

- [ ] **Step 3: Implement — flat node identity in `Build`, remove `ResolveNodeKey`, rewrite `SurfaceOf`**

In `EntityGraph.cs`, replace the two-pass `Build(3-tuple)` body and delete `ResolveNodeKey`:

```csharp
    public static EntityGraph Build(
        IEnumerable<(string DocKey, IReadOnlyList<string>? Keywords, IReadOnlyDictionary<string, string[]>? Fields)> docs)
    {
        var graph = new EntityGraph();
        if (docs is null) return graph;

        // Uniform node identity: every (type, value) is the node "type:value". No conflict-split
        // pass — a value under two different keys (person:paris vs place:paris; status:done vs
        // outcome:done) is two distinct nodes by construction, so unrelated keys never cross-link.
        foreach (var (docKey, keywords, fields) in docs)
        {
            if (string.IsNullOrWhiteSpace(docKey)) continue;
            var nodeKeys = new List<string>();
            foreach (var (type, value) in ParseTypedEntities(keywords)) nodeKeys.Add($"{type}:{value}");
            foreach (var (type, value) in ParseEntityFields(fields)) nodeKeys.Add($"{type}:{value}");
            if (nodeKeys.Count == 0) continue;
            graph.AddDoc(docKey, nodeKeys);
        }
        return graph;
    }
```

Delete the `ResolveNodeKey` method entirely (it is now unused). Rewrite `SurfaceOf`:

```csharp
    /// <summary>The surface form a node seeds on: the value after the type prefix. Every node built
    /// by <see cref="Build"/> is <c>type:value</c>, so <c>place:paris</c> and <c>status:done</c> both
    /// seed from their bare value. A node with no <c>:</c> (defensive — direct <see cref="AddDoc"/>
    /// callers may pass bare values) seeds on the whole key.</summary>
    private static string SurfaceOf(string nodeKey)
    {
        int colon = nodeKey.IndexOf(':');
        return colon > 0 && colon < nodeKey.Length - 1 ? nodeKey[(colon + 1)..] : nodeKey;
    }
```

Refresh the XML docs to the uniform model (remove "conflict-driven"/"split" language):
- Class-level `<summary>` (top of file): entities are now "attribute `(key, value)` nodes — any key, not just person/place/org/thing/topic."
- `RealTypes` `<summary>`: it is "the typed-keyword markers `ParseTypedEntities` recognizes" — drop the "gets split into per-type nodes by Build" clause (Build no longer splits).
- `Build(3-tuple)` `<summary>`: describe **uniform `type:value` node identity** (every key distinct by construction; no conflict pass); keep the "per-doc tags are the union of ParseTypedEntities ∪ ParseEntityFields" note.

- [ ] **Step 4: Reconcile existing tests to the uniform model**

Run the full suite: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`. Expected outcome + required reconciliation:
- The 3 new tests + the rewritten legacy test PASS.
- `Build_SingleTypeValue_DoesNotSplit_AndAssociationReaches`, `Build_ConflictingTypes_SplitNodes_BlockFalseCrossTypeBridge`, `Build_SplitNode_StillSeedsFromSurfaceForm`, `Build_TopicIsAGraphNode_AssociatesMemoriesByTheme` — their **assertions still pass** (reachability unchanged), but their NAMES/COMMENTS reference "split"/"conflict"/"bare nodes" which no longer exist. Update the names+comments to the uniform model (e.g. "person:alice / org:acme are distinct `type:value` nodes; association still reaches"). Do NOT change their assertions.
- Any OTHER failing test (e.g. in `EntityGraphSourceSearchTests.cs` / `EntityGraphScoreContributorTests.cs`) that fails because it asserted the old bare-value/conflict-split node behavior: read it, and update its assertions/comments to the correct new uniform behavior. **Reconcile to correct new behavior — never weaken an assertion just to make it green.** If a test's intent genuinely conflicts with the new design, report it as DONE_WITH_CONCERNS and describe it rather than guessing.

- [ ] **Step 5: Run the full suite to confirm green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj`
Expected: all green (count ≥ the prior 917 + the 3 net-new tests).

- [ ] **Step 6: Commit**

```bash
git add src/Scrinia.Core/Search/EntityGraph.cs tests/Scrinia.Tests/EntityGraphTests.cs   # + any other reconciled test files
git commit -F- <<'EOF'
feat(core): uniform (key,value) entity-graph node identity

Every attribute (type, value) is now the node "type:value" — replacing the
two-pass conflict-split heuristic (realTypesByValue + ResolveNodeKey). This
closes the cross-link gap the uniform feed opened: unrelated keys sharing a
value (status:done + outcome:done; tag:paris + place:paris) are distinct nodes
and no longer falsely link. SurfaceOf now strips any type prefix so non-real-type
nodes still seed by their bare value. Net simplification (removes machinery).

NON-PARITY by design: non-real-type keys become type-scoped; the legacy entity:
marker is now an entity:value node and no longer bridges to typed entities.
Reconciled the affected EntityGraph tests to the uniform behavior.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

## Verification

1. `dotnet build src/Scrinia.Core/Scrinia.Core.csproj` clean.
2. `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` green.
3. Confirm the diff removed `ResolveNodeKey` and the `realTypesByValue` pass, and that `SurfaceOf` no longer references `RealTypes`.
4. Grep the reconciled tests: no assertion was deleted/weakened to pass — only names/comments updated, plus the one legacy-test rewrite and the 3 additions.

## Self-review (done during authoring)

- **Spec coverage:** implements the spec's "Node identity `(key, value)` IS in this plan set (Plan 2)" — flat `type:value`, retire the conflict-split, `SurfaceOf` universal-strip; leaves propagation weighting + anti-hub to the tuning effort.
- **Non-parity is intended and scoped:** the two behavior changes (non-real-type type-scoping; legacy `entity:` no longer bridging) are each covered by a dedicated test; PPR propagation and `AddDoc`/`ParseTypedEntities`/`ParseEntityFields` are untouched.
- **Type consistency:** node keys are `"{type}:{value}"` everywhere; `SurfaceOf` inverts it (strip first colon); `AddDoc` still lowercases (idempotent on already-lowercased keys).
- **Risk:** other suites (`EntityGraphSourceSearchTests`, `EntityGraphScoreContributorTests`) may assert old node behavior — Step 4 requires reading and reconciling them to correct new behavior, not weakening.
