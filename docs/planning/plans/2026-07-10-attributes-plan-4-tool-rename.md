# Attributes Model — Plan 4: Tool Rename (`memory_*` → bare) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Drop the redundant `memory_` prefix from the agent-facing MCP tool names so the agent sees `scrinia_remember` instead of `scrinia_memory_remember`, and update every functional and runtime-agent-loaded reference to those names in lockstep — including the auth permission map and every place the running system tells an agent which tool to call.

**Architecture:** The `[McpServerTool(Name = "…")]` attribute string is the single source of truth the SDK advertises; the C# method names (`MemoryRemember`, …) stay unchanged, which bounds the blast radius (the CLI and bench call the methods directly). Two surfaces hold **parallel hand-maintained copies** of the tool-name strings and must move together: `McpPermissionFilter` (name→permission map — a missed key silently breaks auth gating) and the runtime agent-facing strings (error/recovery hints, `followUp`s, `ScriniaPrimer`→generated `AGENTS.md`, and the runtime-loaded prompt/skill markdown). Human docs and the *authored* guide rewrite are out of scope.

**Tech Stack:** C# / .NET 10, ModelContextProtocol 1.0.0, xUnit + FluentAssertions.

## Global Constraints

- **Rename these 10 tool names** (the ONLY change to each tool — its C# method name and everything else stay put). This mapping is the single source of truth for every task:

  | old `Name` | new `Name` |
  |---|---|
  | `memory_remember` | `remember` |
  | `memory_recall` | `recall` |
  | `memory_search` | `search` |
  | `memory_list` | `list` |
  | `memory_append` | `append` |
  | `memory_forget` | `forget` |
  | `memory_compact` | `compact` |
  | `memory_link` | `link` |
  | `memory_restore` | `restore` |
  | `memory_reconcile` | `reconcile` |

  `guide` is already bare (the naming precedent). The token `memory_<verb>` for these 10 verbs appears ONLY in string literals, prose, and comments — never in a C# identifier (methods are PascalCase `MemoryRemember`), so a targeted token rename is safe. Do NOT touch `MemoryTools`, `MemoryRemember`, `MemoryStoreContext`, `MemoryNaming`, reserved paths (`/temp/`, `topic:`), or any other `memory`-containing identifier.
- **Scope = functional + runtime-agent-loaded.** In scope: the `Name` strings; the permission map; every agent-facing string that *directs the agent to call a tool* (error/recovery hints, `followUp`s, `[Description]` example snippets); `ScriniaPrimer` (→ generated `AGENTS.md` + MCP `instructions`); and the runtime-loaded prompt markdown (`guide.md`, `scrinia-memory-skill.md`, `prompts/scaffolds/*.md`, `skills/*.md`) as a **mechanical token rename**. **Deferred (do NOT touch):** the *authored* guide rewrite (Plan 7 rewrites `guide.md`'s prose/structure — Plan 4 only fixes its tool-name tokens); human docs (`docs/**`, `README.md`, `CHANGELOG.md`).
- **Do NOT touch bench WIP or plugins:** anything under `benchmarks/`, all of `tests/Scrinia.Retrieval.Benchmarks.Tests/**` (the bench defines its OWN tool-name strings that do NOT flow through `[McpServerTool(Name)]`), and `src/Scrinia.Mcp/plugins/opencode/*`. Also leave `.superpowers/**` planning scratch alone.
- The permission map (`McpPermissionFilter.RequiredPermission`) and its test `[InlineData]` rows MUST rename in the same commit — a divergence silently changes an operation's auth gate. `restore` stays **ungated** (no map key; falls to the `_ => null` default), same as `guide`.
- Commit trailer, exactly:
  `Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>`
  then `Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g`
- Never push. `TreatWarningsAsErrors` is on for `Scrinia.Core`/`Scrinia.Mcp`/`Scrinia.Server` — build clean. Branch off `main` (`aeda415`) before Task 1.

## File Structure

Per-task file lists are drawn from a full inventory; line numbers are guides (read the file and rename by token, don't trust a stale line number). After each task, verify with:
`git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- <the task's files>` returns **nothing** (all renamed), and `dotnet build` + the task's tests are green.

- Task 1: `src/Scrinia.Mcp/MemoryTools.cs` (Name strings only), `src/Scrinia.Server/Auth/McpPermissionFilter.cs`, `tests/Scrinia.Server.Tests/McpPermissionFilterTests.cs`, `tests/Scrinia.Server.Tests/McpEndpointTests.cs`, + new completeness test.
- Task 2: the MCP tool-body agent-facing strings across `src/Scrinia.Mcp/*.cs`, + `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` coupled assertions.
- Task 3: `src/Scrinia.Mcp/ScriniaPrimer.cs`, `src/Scrinia/Commands/Hint.cs`, + `tests/Scrinia.Tests/Hooks/AgentsMdWriterTests.cs`, `tests/Scrinia.Tests/HintCommandTests.cs`.
- Task 4: `src/Scrinia.Mcp/prompts/guide.md`, `src/Scrinia.Mcp/prompts/scrinia-memory-skill.md`, `src/Scrinia.Mcp/prompts/scaffolds/*.md`, `src/Scrinia.Mcp/skills/*.md`, + `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs`, `tests/Scrinia.Tests/Hooks/SkillWriterTests.cs`, and the guide assertion in `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs`.

---

### Task 1: Rename the tool `Name` strings + permission map + completeness test

The correctness core: rename the 10 `[McpServerTool(Name)]` strings, move the `McpPermissionFilter` map in lockstep, and add a test that enumerates the registered tools and asserts each has a permission mapping (or is intentionally ungated) — so a future missed key can't silently slip through.

**Files:**
- Modify: `src/Scrinia.Mcp/MemoryTools.cs` (the 10 `Name = "memory_*"` strings only — lines 76, 131, 155, 182, 196, 232, 248, 265, 288, 297; leave the `[Description]` bodies and everything else for Task 2)
- Modify: `src/Scrinia.Server/Auth/McpPermissionFilter.cs` (the `RequiredPermission` switch keys, L49–54, + doc-comment refs L40, L42)
- Modify: `tests/Scrinia.Server.Tests/McpPermissionFilterTests.cs` (`[InlineData]` rows L15–23, L31)
- Modify: `tests/Scrinia.Server.Tests/McpEndpointTests.cs` (`tools/call` `"name":"memory_remember"` payloads, L89, L111)
- Test: add the completeness test to `McpPermissionFilterTests.cs`

**Interfaces:**
- Consumes: `ModelContextProtocol.Server.McpServerToolAttribute` (its `.Name` property) via reflection over `typeof(Scrinia.Mcp.ScriniaMcpTools)`.
- Produces: the SDK now advertises `remember`/`recall`/… ; `RequiredPermission("tools/call", "remember")` → `"store"`, etc.

- [ ] **Step 1: Write the failing completeness test**

Add to `McpPermissionFilterTests.cs` (needs `using System.Reflection;`, `using ModelContextProtocol.Server;`, `using Scrinia.Mcp;`):

```csharp
[Fact]
public void EveryRegisteredTool_HasAPermissionMapping_OrIsIntentionallyUngated()
{
    // Guards the hand-maintained parallel copy: a tool advertised to agents that the
    // permission map doesn't know about would silently fall through to the ungated
    // default. Bootstrap/recovery tools are ungated on purpose (parity with REST).
    var intentionallyUngated = new HashSet<string>(StringComparer.Ordinal) { "guide", "restore" };

    var toolNames = typeof(ScriniaMcpTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Select(m => m.GetCustomAttribute<McpServerToolAttribute>()?.Name)
        .Where(n => !string.IsNullOrEmpty(n))
        .Select(n => n!)
        .ToList();

    toolNames.Should().NotBeEmpty("the tool type must expose [McpServerTool] methods");

    using var scope = new AssertionScope();
    foreach (var name in toolNames)
    {
        if (intentionallyUngated.Contains(name)) continue;
        McpPermissionFilter.RequiredPermission("tools/call", name)
            .Should().NotBeNull($"tool '{name}' must have an explicit permission mapping");
    }
}
```

(Add `using FluentAssertions.Execution;` for `AssertionScope`.)

- [ ] **Step 2: Run it to verify it fails**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj --filter FullyQualifiedName~EveryRegisteredTool_HasAPermissionMapping`
Expected: FAIL — the registered tools are still `memory_*`, none of which (except the ungated set) match the map keys once you *think* of them as bare… actually today they DO match (`memory_*` keys exist), so this test **passes before the rename**. Its real job is a **regression guard for Step 3**: it must stay GREEN after you rename BOTH the `Name` strings and the map keys, and would go RED if you rename the `Name` strings but forget a map key. To see it bite, after renaming the `Name` strings (below) but before fixing the map, it fails with e.g. "tool 'remember' must have an explicit permission mapping". Confirm that transient RED during Step 3, then fix the map to return GREEN.

- [ ] **Step 3: Rename the `Name` strings, the map, and the existing test rows**

In `MemoryTools.cs`, change only the 10 `Name = "memory_<verb>"` → `Name = "<verb>"` per the mapping table. Leave the `Description(...)` bodies untouched (Task 2).

In `McpPermissionFilter.cs`, rename the switch keys and the two doc-comment references:

```csharp
    /// ... map an MCP request to the permission it requires, or null ... the bootstrap/recovery
    /// tools `guide` and `restore` ...
    ///
    /// <para>The mutating tools `compact`/`link`/`reconcile` map to `store`: ...</para>
    public static string? RequiredPermission(string method, string? toolName) => method switch
    {
        "tools/call" => toolName switch
        {
            "search" or "list" => "search",
            "recall" => "read",
            "remember" or "compact" or "link" or "reconcile" => "store",
            "append" => "append",
            "forget" => "forget",
            _ => null, // guide, restore, or unknown → no gate (dispatch/handshake handled by SDK)
        },
        "resources/read" => "read",
        _ => null, // initialize, tools/list, prompts/*, resources/list, notifications, …
    };
```

In `McpPermissionFilterTests.cs`, rename the `[InlineData]` rows to the bare names (L15–23: `("search","search")`, `("list","search")`, `("recall","read")`, `("remember","store")`, `("compact","store")`, `("link","store")`, `("reconcile","store")`, `("append","append")`, `("forget","forget")`; L31: `[InlineData("restore")]`).

In `McpEndpointTests.cs`, change the two `tools/call` payload names `"name":"memory_remember"` → `"name":"remember"` (L89, L111).

- [ ] **Step 4: Run to verify green**

Run: `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj` → all green (the completeness test, the renamed policy rows, and the two end-to-end `tools/call` auth tests). Confirm `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- src/Scrinia.Mcp/MemoryTools.cs` shows only `[Description]`-body occurrences (Task 2), and `... -- src/Scrinia.Server tests/Scrinia.Server.Tests` shows nothing.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/MemoryTools.cs src/Scrinia.Server/Auth/McpPermissionFilter.cs tests/Scrinia.Server.Tests/McpPermissionFilterTests.cs tests/Scrinia.Server.Tests/McpEndpointTests.cs
git commit -F- <<'EOF'
refactor(mcp): rename memory_* tools to bare names + guard the permission map

The 10 [McpServerTool(Name)] strings drop the redundant memory_ prefix (agents
see scrinia_remember, not scrinia_memory_remember); C# method names unchanged.
The McpPermissionFilter map (a hand-maintained parallel copy) renames in lockstep,
and a new completeness test enumerates the registered tools and asserts each has a
permission mapping or is intentionally ungated (guide/restore) — so a future
missed key can't silently drop an auth gate.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 2: Rename agent-facing strings in the MCP tool bodies

Rename every runtime string that names a tool the agent should call — `[Description]` example snippets, error/recovery hints, `followUp`s, and `WithInstruction`s — across the MCP tool source. After the rename the running system never directs an agent to a dead tool name.

**Files:**
- Modify (rename `memory_<verb>` tokens in string literals + comments):
  - `src/Scrinia.Mcp/MemoryTools.cs` — `[Description]` examples (L80, 133, 158, 199, 204, 234, 250, 267, 300) + error-hint recovery args (L100, 102, 105, 107, 147, 149, 150, 175, 177, 211, 213, 216, 218, 223, 241, 243, 258, 260, 276, 278, 281, 283, 341, 343, 347, 349) + code comments (L52, 313, 319, 331)
  - `src/Scrinia.Mcp/MemoryTools.Core.cs` — L21, 61, 62, 73, 74, 404, 406, 418, 419, 648, 1443, 1497 (+ comment L873)
  - `src/Scrinia.Mcp/MemoryTools.Bundling.cs` — L61
  - `src/Scrinia.Mcp/MemoryTools.Skills.cs` — L372, 383, 416
  - `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs` — L81, 87, 93, 109, 123, 140, 159, 441, 442, 559, 644, 647 (+ comment L528)
  - `src/Scrinia.Mcp/McpResponse.cs` — L189, 190
  - `src/Scrinia.Mcp/ReservedPrefixGuard.cs` — L64
  - `src/Scrinia.Mcp/ScriniaMcpResources.cs` — L26 (+ XML doc comment L17)
- Test: `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` — update the coupled assertions at L451 (`list` summary body), L1985 (validator recovery hint), L2000 (not-found hint). Leave L1836 (guide) untouched — that's Task 4.

**Interfaces:** none new — pure string edits.

- [ ] **Step 1: Update the coupled tests first (they encode the expected new strings)**

Read the three assertions and update the expected substrings to the bare names. Mirror the exact assertion style already there; e.g. an assertion `Contain("memory_search")` becomes `Contain("search")`. Because these are substring assertions, confirm the new substring is still specific enough not to match incidentally (e.g. prefer asserting a fuller example fragment like `list({ mode: 'full' })` over the bare word `list` where the surrounding test already quotes a call example).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~ScriniaMcpToolsTests` and confirm exactly those three assertions now fail (the source still emits `memory_*`).

- [ ] **Step 3: Rename the source strings**

Go file by file through the list above, renaming each `memory_<verb>` token (in `[Description]` strings, `ResponseBuilder.Error(...)` hint/followUp args, `WithInstruction`/`WithFollowUp` strings, and comments) to its bare form per the mapping table. Do not alter surrounding punctuation or the `{ ... }` example bodies. Do NOT touch C# identifiers.

- [ ] **Step 4: Run to verify green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` → all green. Verify `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- src/Scrinia.Mcp/*.cs` returns **nothing** (all in-code tool-name refs renamed; the `prompts/` and `skills/` markdown are Task 4).

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/*.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -F- <<'EOF'
refactor(mcp): rename tool-name refs in agent-facing tool-body strings

Error/recovery hints, followUps, and [Description] example snippets across the
MCP tool source now name the bare tools (remember/recall/search/…) so the running
system never directs an agent to a removed tool name. String-only; no logic or
method-name changes. Coupled test assertions updated.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 3: Rename `ScriniaPrimer` (→ generated `AGENTS.md`) and `Hint.cs` hook payloads

These feed content the agent receives outside the tool-response path: `ScriniaPrimer` populates the generated `AGENTS.md` and the MCP `instructions` field; `Hint.cs` emits hook payloads injected into the agent's context. Both must name the bare tools.

**Files:**
- Modify: `src/Scrinia.Mcp/ScriniaPrimer.cs` — L22, 24, 40, 41
- Modify: `src/Scrinia/Commands/Hint.cs` — L144, 147, 166, 168, 243
- Test: `tests/Scrinia.Tests/Hooks/AgentsMdWriterTests.cs` — L45 (`Contain("memory_search")` → `Contain("search")`); `tests/Scrinia.Tests/HintCommandTests.cs` — L105, 152, 237, 250 (expected hint text `memory_search({ query: 'oauth-flow' })` → `search({ query: 'oauth-flow' })`)

**Interfaces:** none new.

- [ ] **Step 1: Update the coupled tests first**

In `AgentsMdWriterTests.cs` L45 and `HintCommandTests.cs` L105/152/237/250, change the expected substrings to the bare names, mirroring the existing assertion style (keep the surrounding `({ query: '...' })` fragment so the assertion stays specific).

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AgentsMdWriterTests|FullyQualifiedName~HintCommandTests"` → the edited assertions fail (source still emits `memory_*`).

- [ ] **Step 3: Rename the source strings**

`ScriniaPrimer.cs` L22/24/40/41 and `Hint.cs` L144/147/166/168/243: rename `memory_<verb>` tokens to bare per the mapping.

- [ ] **Step 4: Run to verify green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~AgentsMdWriterTests|FullyQualifiedName~HintCommandTests"` → green. Verify `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- src/Scrinia.Mcp/ScriniaPrimer.cs src/Scrinia/Commands/Hint.cs` returns nothing.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/ScriniaPrimer.cs src/Scrinia/Commands/Hint.cs tests/Scrinia.Tests/Hooks/AgentsMdWriterTests.cs tests/Scrinia.Tests/HintCommandTests.cs
git commit -F- <<'EOF'
refactor(mcp): rename tool refs in ScriniaPrimer + Hint hook payloads

ScriniaPrimer (→ generated AGENTS.md + MCP instructions field) and the Hint hook
payloads now name the bare tools, so context injected outside the tool-response
path matches the renamed surface. Coupled tests updated.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

### Task 4: Rename tool-name tokens in runtime-loaded prompt/skill markdown

Mechanically rename the `memory_<verb>` tokens in the markdown the agent LOADS at runtime — the guide (`guide()`), the emitted skill file (`SkillWriter`), the scaffolds, and the embedded skills. This is a token rename, **not** the authored guide rewrite (Plan 7 rewrites `guide.md`'s prose and structure — here we only correct the names so runtime-loaded content isn't misinstructing).

**Files:**
- Modify (rename `memory_<verb>` tokens → bare, per the mapping):
  - `src/Scrinia.Mcp/prompts/guide.md` (32)
  - `src/Scrinia.Mcp/prompts/scrinia-memory-skill.md` (9)
  - `src/Scrinia.Mcp/prompts/scaffolds/*.md` (researcher, reviewer, domain-expert — 12)
  - `src/Scrinia.Mcp/skills/*.md` (auditor, chaos-engineer, debugger, evolutionary, merge-safety, onboarder, qa, self-reflector — 32)
- Test: `tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs` L22 (`Contain("memory_remember")` → `Contain("remember")`); `tests/Scrinia.Tests/Hooks/SkillWriterTests.cs` L59 (`memory_search` + `memory_remember` → `search` + `remember`); `tests/Scrinia.Tests/ScriniaMcpToolsTests.cs` L1836 (guide output `Contain("memory_remember")` → `Contain("remember")`)

**Interfaces:** none — content only.

- [ ] **Step 1: Update the coupled tests first**

Update the three expected substrings (L22, L59, L1836) to the bare names, mirroring the existing assertion style.

- [ ] **Step 2: Run to verify they fail**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter "FullyQualifiedName~ScriniaMcpPromptsResourcesTests|FullyQualifiedName~SkillWriterTests|FullyQualifiedName~ScriniaMcpToolsTests.Guide"` (adapt the last filter to the actual guide test name) → the edited assertions fail (markdown still says `memory_*`).

- [ ] **Step 3: Rename the markdown tokens**

Across the listed markdown files, replace each `memory_<verb>` token (for the 10 verbs) with its bare form. These are embedded resources compiled into `Scrinia.Mcp`, so after editing, a rebuild re-embeds them. Do not otherwise reword the prose (the authored rewrite is Plan 7). Confirm you touched NO file under `docs/`, `README.md`, `CHANGELOG.md`, `benchmarks/`, or `.superpowers/`.

- [ ] **Step 4: Run to verify green**

Run: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` → all green. Verify `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)' -- src/Scrinia.Mcp/prompts src/Scrinia.Mcp/skills` returns nothing.

- [ ] **Step 5: Commit**

```bash
git add src/Scrinia.Mcp/prompts src/Scrinia.Mcp/skills tests/Scrinia.Tests/ScriniaMcpPromptsResourcesTests.cs tests/Scrinia.Tests/Hooks/SkillWriterTests.cs tests/Scrinia.Tests/ScriniaMcpToolsTests.cs
git commit -F- <<'EOF'
refactor(mcp): rename tool tokens in runtime-loaded prompt/skill markdown

Mechanical token rename (memory_* -> bare) in the markdown the agent loads at
runtime: guide.md, scrinia-memory-skill.md, prompts/scaffolds, and the embedded
skills. Not the authored guide rewrite (Plan 7) — only the names, so runtime
content stops naming removed tools. Coupled prompt/skill/guide test assertions
updated. Human docs (docs/, README, CHANGELOG) intentionally deferred.

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
Claude-Session: https://claude.ai/code/session_019kfCkvGdDsbK2DiWKJZp2g
EOF
```

---

## Verification

1. `dotnet build` clean across `Scrinia.Core`/`Scrinia.Mcp`/`Scrinia.Server` (TreatWarningsAsErrors on).
2. Full suites green: `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj` and `dotnet test tests/Scrinia.Server.Tests/Scrinia.Server.Tests.csproj`.
3. **Rename completeness:** `git grep -nE 'memory_(remember|recall|search|list|append|forget|compact|link|restore|reconcile)'` returns matches ONLY under the deferred/excluded paths — `docs/**`, `README.md`, `CHANGELOG.md`, `benchmarks/**`, `tests/Scrinia.Retrieval.Benchmarks.Tests/**`, `src/Scrinia.Mcp/plugins/opencode/**`, and `.superpowers/**`. No match anywhere in `src/Scrinia.Mcp/*.cs`, `src/Scrinia.Server/**`, `src/Scrinia/Commands/**`, `src/Scrinia.Mcp/prompts/**`, `src/Scrinia.Mcp/skills/**`, or their tests.
4. **Auth parity:** the completeness test passes, and `RequiredPermission("tools/call", "<each renamed tool>")` returns the same permission the `memory_*` key returned before.

## Self-review (done during authoring)

- **Spec coverage:** implements Unit 4 (rename the `[McpServerTool(Name)]` strings; update `McpPermissionFilter` and product tests). The spec's "update the AgentsMdWriter/SkillWriter hook writers" resolves — per the inventory — to updating their *sources* (`ScriniaPrimer.cs`, `scrinia-memory-skill.md`), which Tasks 3–4 do; the writer `.cs` files hold no tool-name strings. The completeness test the spec's risk note asked for is Task 1.
- **Scope discipline:** matches the approved "functional + runtime-agent-loaded" scope — human docs and the authored guide rewrite are explicitly deferred to Plan 7; bench and plugins are untouched. The one intentional overlap with Plan 7 (mechanically fixing `guide.md` tokens now, fully rewriting it later) is a deliberate choice to keep dogfooded `main` coherent at every merge.
- **Silent-failure guard:** the permission map renames in the same commit as the `Name` strings (Task 1), and the new completeness test converts a future missed key from a silent auth-gate drop into a red test.
- **Blast-radius bound:** C# method names are unchanged, so the CLI (`ScriniaCommands*.cs` calls `MemoryRemember` directly) and the bench (own tool-name strings) need no change; the token `memory_<verb>` never appears in a C# identifier, so a token rename can't corrupt code.
- **Task independence:** each task renames its sources AND their coupled tests together, so no task leaves the build red; tasks touch mostly disjoint files (Task 1 = Name strings + Server auth; Task 2 = Mcp tool bodies; Task 3 = Primer/Hint; Task 4 = markdown).
