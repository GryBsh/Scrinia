# Customizing Hook-Injected Memory Hints Per Agentic CLI: A Technical Field Guide for Scrinia

## TL;DR
- **Your instinct is correct: the optimal hint format and injection mechanism differ per CLI, and the difference is mostly about the *injection channel*, not the wrapper syntax.** Codex picks up your XML hints reliably because Codex injects `UserPromptSubmit`/`SessionStart` `additionalContext` as a **`developer`-role message** (the strongest instruction channel for GPT‑5‑class models) on *every* prompt; the other four either inject as plain user/system‑reminder text, only fire once at session start, require a JavaScript/TypeScript plugin, or can't inject model‑visible context at all from a hook.
- **All five now have hook systems, but they are wildly unequal for context injection.** Claude Code (`hookSpecificOutput.additionalContext`), Codex (`additionalContext` → developer message), and Gemini CLI (`BeforeAgent`/`UserPromptSubmit` → `additionalContext`) support reliable per‑turn text injection. OpenCode has **no shell‑hook system at all** — injection requires a TypeScript plugin (`experimental.chat.system.transform` / `chat.message`). GitHub Copilot CLI's hooks are **observability/policy‑only**: `sessionStart`/`userPromptSubmitted` output is *ignored*, so you cannot inject hints via hooks at all (use AGENTS.md instead).
- **Format the hint to the underlying model, not the CLI:** XML‑tagged, imperative blocks for Codex (GPT‑5.x) and Copilot‑on‑GPT; XML tags also work well for Claude Code — Anthropic states "Claude was trained specifically to recognize XML tags as a prompt organizing mechanism," and Claude sees your text inside a `system-reminder`; markdown‑with‑light‑XML for Gemini. For OpenCode and Copilot, lean on the persistent `AGENTS.md` file because their hook injection is weak or absent.

## Key Findings

### The single most important variable: *which channel* the hint lands in
Across these tools, hook‑injected text can arrive in the model's context as one of four things, in rough order of "stickiness":
1. **A `developer`-role message** (Codex) — highest authority short of system, re‑asserted every turn.
2. **A `system-reminder` block** (Claude Code `additionalContext`) — high salience, Claude is trained to weight these.
3. **Appended to the user prompt / a user message** (Gemini `BeforeAgent`, Codex plain‑stdout fallback) — medium.
4. **A system‑prompt segment or message part injected by a plugin** (OpenCode) — medium, but requires code.
5. **Nothing** (Copilot CLI `sessionStart`/`userPromptSubmitted` output is discarded) — you must fall back to the instruction file.

This is why Codex "just works": its `additionalContext` becomes a developer message, which for GPT‑5‑family models is the canonical place for steering instructions, and it fires on every `UserPromptSubmit`. The others are weaker channels or one‑shot.

### Per-turn vs. once-per-session
"Knowing memories exist, when to create, when to retrieve" is a *per‑turn* decision, so you want a hook that fires **before every user prompt is processed**, not just at session start. Session‑start‑only injection decays as the conversation grows (the model forgets). The events that fire per‑turn and inject context:
- Codex: `UserPromptSubmit` (developer message) ✅
- Claude Code: `UserPromptSubmit` (`additionalContext` → system-reminder) ✅
- Gemini CLI: `BeforeAgent` / `UserPromptSubmit` (`additionalContext` appended for that turn) ✅
- OpenCode: `experimental.chat.system.transform` (every turn, system segment) or `experimental.chat.messages.transform` (every turn, hidden from UI) ✅ but plugin‑only
- Copilot CLI: none usable ❌

---

## Details (per CLI)

### 1. OpenAI Codex CLI

**Model:** GPT‑5.x family (e.g., gpt‑5‑codex; config shows defaults like `gpt-5.4`). This is the key to why your XML works.

**Hook system:** Formal, called **"hooks" / "lifecycle hooks."** Stable and default‑enabled as of recent versions (`[features].hooks`; `codex_hooks` is a deprecated alias). Explicitly modeled on Claude Code's design ("Claude-style lifecycle hooks").

**Config format & location:** Either a **`hooks.json`** file or an **inline `[hooks]` TOML table** in `config.toml`, discovered next to active config layers (`~/.codex/`, project `.codex/` when trusted). Same event schema in both forms.

Inline TOML example:
```toml
[[hooks.UserPromptSubmit]]
[[hooks.UserPromptSubmit.hooks]]
type = "command"
command = '/usr/bin/python3 "$CODEX_HOME/hooks/inject_memory_hint.py"'
timeout = 30
```
JSON form (`hooks.json`, structurally identical to Claude Code):
```json
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "python3 ~/.codex/hooks/hint.py" } ] }
    ]
  }
}
```

**Events:** `SessionStart`, `UserPromptSubmit`, `PreToolUse`, `PermissionRequest`, `PostToolUse`, `PreCompact`, `PostCompact`, `SubagentStart`, `SubagentStop`, `Stop`. Only `command` handlers run today (prompt/agent handlers are parsed but skipped).

**Hook input (stdin JSON):** Common fields include `session_id`, `turn_id`, `transcript_path`, `cwd`, `hook_event_name`, `model`, `permission_mode`; `UserPromptSubmit` additionally gets `prompt`.

**Context injection (the important part):** On `SessionStart`, `UserPromptSubmit`, and `PostToolUse`, return:
```json
{ "hookSpecificOutput": { "hookEventName": "UserPromptSubmit", "additionalContext": "<scrinia_memory_hint>...</scrinia_memory_hint>" } }
```
Plain stdout text (exit 0) is also accepted and treated as `additionalContext`. **Critically, Codex converts `additionalContext` into a `DeveloperInstructions` item — i.e., a `developer`-role message** in the wire format (confirmed in source: `additional_context_messages` maps each string to `DeveloperInstructions::new(...).into()`). That is the strongest non‑system steering channel for GPT‑5 and is re‑injected each turn — exactly why your XML hints land reliably.

**Caveat / in flux:** `PreToolUse.additionalContext` is **parsed but NOT supported** (silently dropped); use `SessionStart`/`UserPromptSubmit`/`PostToolUse` for injection. There were recent reports/issues (openai/codex #16486, #16933) about `additionalContext` rendering as a *visible* developer message in the TUI transcript and about multiline rendering — behavior here is evolving, so pin a Codex version in your test matrix.

**Instruction file:** **`AGENTS.md`** (Codex originated the standard; supports nested per‑directory files). Reserved `instructions_file`/`model_instructions_file` exist but AGENTS.md is canonical.

---

### 2. Claude Code (Anthropic)

**Model:** Claude (Sonnet/Opus family). Anthropic documents that Claude responds especially well to **XML tags**: *"we recommend that you use specifically XML tags as separators for Claude, as Claude was trained specifically to recognize XML tags as a prompt organizing mechanism"* — and *"XML tags help Claude parse complex prompts unambiguously… Wrapping each type of content in its own tag (e.g. `<instructions>`, `<context>`, `<input>`) reduces misinterpretation."*

**Hook system:** The most mature. Formal "hooks," configured as **JSON in `settings.json`** at `~/.claude/settings.json` (user), `.claude/settings.json` (project, committable), `.claude/settings.local.json` (local), managed policy, or plugin `hooks/hooks.json`.

**Config schema (three‑level nesting): event → matcher group → handler array.** Handler types: `command`, `http`, `prompt`, `agent` (plus `mcp_tool`).
```json
{
  "hooks": {
    "UserPromptSubmit": [
      { "hooks": [ { "type": "command", "command": "$CLAUDE_PROJECT_DIR/.claude/hooks/scrinia-hint.sh", "timeout": 10 } ] }
    ]
  }
}
```

**Events:** The lifecycle is large and growing. The official `code.claude.com` reference groups events by cadence (per‑session, per‑turn, per‑tool‑call) and lists, among others: `SessionStart`, `Setup`, `UserPromptSubmit`, `UserPromptExpansion`, `PreToolUse`, `PermissionRequest`, `PermissionDenied`, `PostToolUse`, `PostToolUseFailure`, `PostToolBatch`, `Notification`, `MessageDisplay`, `SubagentStart`, `SubagentStop`, `TaskCreated`, `TaskCompleted`, `Stop`, `StopFailure`, `TeammateIdle`, `InstructionsLoaded`, `ConfigChange`, `CwdChanged`, `FileChanged`, `WorktreeCreate/Remove`, `PreCompact`, `PostCompact`, `Elicitation`, `ElicitationResult`, `SessionEnd`. *Note source divergence on the total count:* third‑party guides cite "21 lifecycle events" (SmartScope, March 2026) while other references list as many as 29 — the count is unsettled across sources and versions, so pin a Claude Code version rather than relying on a fixed number.

**Context injection:** Per the official reference, *"the exceptions are UserPromptSubmit, UserPromptExpansion, and SessionStart, where stdout is added as context that Claude can see and act on."* The structured form:
```json
{ "hookSpecificOutput": { "hookEventName": "UserPromptSubmit", "additionalContext": "<scrinia>...</scrinia>" } }
```
**The injected text is wrapped by Claude Code in a `<system-reminder>` block and read as plain text.** `PostToolUse` can also append `additionalContext` to a tool result. MCP tools are matchable as `mcp__<server>__<tool>` (e.g., a `PreToolUse` matcher `mcp__scrinia__.*`).

**Known bugs/flux to flag:**
- `UserPromptSubmit` with `hookSpecificOutput` JSON throws a spurious "hook error" on the *first* message of a session (issue #17550); plain‑text stdout is the workaround.
- `additionalContext` **accumulates** in history rather than being ephemeral (issue #40216) — every turn's injection persists as a separate `<system-reminder>`, so injecting large per‑turn hints bloats context. Keep hints short, or inject via `SessionStart` once + a terse per‑turn nudge.
- `SessionStart` injection has historically failed to inject for brand‑new sessions in some builds (issue #10373).

**Instruction file:** **`CLAUDE.md`** (auto‑loaded global → project → subdir; `CLAUDE.local.md` for personal). Slash‑command and subagent `.md` files use **YAML frontmatter** (`name`, `description`, `tools`, `model`, `allowed-tools`). AGENTS.md is not natively read (you can symlink/reference it).

---

### 3. Gemini CLI (Google)

**Model:** Gemini (2.5/3 Pro family).

**Hook system:** Formal "hooks," launched in **v0.26.0** (relatively new — flag as fast‑moving). Per the official v0.26.0 release notes, *"Hooks are launched"* and *"Now officially enabled by default, hooks provide a way to fully control and customize the agentic loop."* It deliberately mirrors Claude Code's JSON‑over‑stdin contract, exit‑code semantics, and matcher syntax. Configured as **JSON in `settings.json`** (`.gemini/settings.json` project, `~/.gemini/settings.json` user, plus system and extension layers).

```json
{
  "hooks": {
    "BeforeAgent": [
      { "hooks": [ { "name": "scrinia-hint", "type": "command", "command": "$GEMINI_PROJECT_DIR/.gemini/hooks/hint.sh", "timeout": 5000 } ] }
    ]
  }
}
```

**Events:** `SessionStart`, `SessionEnd`, `BeforeAgent`, `AfterAgent`, `BeforeModel`, `AfterModel`, `BeforeToolSelection`, `BeforeTool`, `AfterTool`, `Notification`, `PreCompress`. Matchers are regex for tool events, exact strings for lifecycle events. (Only `command` type is broadly supported; plugin hooks exist in the v1 design.)

**Context injection:** `BeforeAgent` "fires after a user submits a prompt, but before the agent begins planning" and is the injection point. Return:
```json
{ "hookSpecificOutput": { "hookEventName": "BeforeAgent", "additionalContext": "## Scrinia Memory\n..." } }
```
`additionalContext` is "text that is appended to the prompt for this turn only." (`BeforeTool`/`AfterTool` also support `additionalContext` appended to tool results, plus `tailToolCallRequest` for programmatic tool routing.) Input arrives on stdin with `session_id`, `cwd`, `hook_event_name`, `timestamp`, and event‑specific data (`prompt`, `tool_input`, etc.). The "Golden Rule": only valid JSON to stdout; stderr for logs; exit 0 parses stdout, exit 2 is a system block.

**Notable:** Gemini's hook docs include a worked **memory system** example (`BeforeAgent: Inject memories into context`, `SessionEnd: Consolidate memories`) — almost exactly your use case. There is **no automatic tag wrapping**; you supply any markdown/XML yourself.

**Instruction file:** **`GEMINI.md`**, with unique behavior — Gemini reads GEMINI.md files **both up and down** the directory tree and concatenates them (inspect with `/memory show`). AGENTS.md is not confirmed as natively supported.

---

### 4. OpenCode

**Model:** Configurable / multi‑model (Anthropic, OpenAI, Google, Grok, etc.) — so format to whichever provider the user runs.

**Hook system:** **No shell‑based hooks.** OpenCode has a **TypeScript/JavaScript plugin system** instead. Plugins are `.ts`/`.js` files in `.opencode/plugin/` (project) or `~/.config/opencode/plugin/` (global), auto‑loaded at startup; npm packages are registered via the singular **`"plugin"`** array in `opencode.json`. A plugin exports a `Plugin` function returning a hooks object.

> **Source‑quality flag:** The official docs (opencode.ai/docs/plugins) only document `experimental.session.compacting`. The chat hooks below are confirmed from the `@opencode-ai/plugin` source (quoted in GitHub issues #21240, #27401) and community guides; the `experimental.*` hooks are explicitly unstable. Type definitions for `Message`/`Part` are not published, so use defensive typing.

**Injection mechanisms (model‑visible):**
- **`experimental.chat.system.transform`** — `(input: { sessionID?, model }, output: { system: string[] })`. Push a string onto `output.system` to add a **system‑prompt segment the model sees every turn**. This is the closest equivalent to a persistent hint. *Limitation:* `input` does **not** include the user's current message text (feature request #27401 was closed as not‑planned), so you can't condition on the prompt here.
- **`experimental.chat.messages.transform`** — `(input: {}, output: { messages: {info, parts}[] })`. Mutate the full per‑turn message array; **injected text is invisible in the UI but seen by the model**. Best for a hidden per‑turn reminder.
- **`chat.message`** — `(input: {}, output: { message, parts })`. Push a text part onto `parts` to attach a hint to the user message (visible in history).

Copy‑pasteable system‑hint plugin:
```ts
// .opencode/plugin/scrinia-hint.ts
import type { Plugin } from "@opencode-ai/plugin"
export const ScriniaHint: Plugin = async () => ({
  "experimental.chat.system.transform": async (_input, output) => {
    output.system.push(`<scrinia_memory>
You have access to Scrinia memory tools via MCP. Before answering, consider retrieving relevant memories. After substantive work, create a memory.
</scrinia_memory>`)
  },
})
```

**No automatic tag wrapping** — injected text is plain; any XML/markdown delimiters are your convention.

**Instruction file:** **`AGENTS.md`** (canonical; `/init` generates it). Falls back to `CLAUDE.md` for Claude Code migrants. Supports an `instructions` array in `opencode.json` for extra files/globs/URLs. AGENTS.md is injected into context as a custom‑instructions system layer, making it the **most reliable always‑on channel for OpenCode** — prefer it over plugin hooks for the "memories exist" baseline, and use a plugin only for dynamic per‑turn nudges.

---

### 5. GitHub Copilot CLI

**Model:** **Claude Sonnet 4.5 by default.** The official `github/copilot-cli` README states verbatim: *"By default, copilot utilizes Claude Sonnet 4.5. Run the /model slash command to choose from other available models, including Claude Sonnet 4 and GPT‑5."* (Sonnet 4.5 was added in Copilot CLI v0.0.329.) The `/model` picker shows `1. Claude Sonnet 4.5 (default) 2. Claude Sonnet 4 3. GPT-5`, and you can switch via `COPILOT_MODEL=gpt-5` (and newer GPT‑5.x‑Codex variants via `--model`). So your hint format should target Claude by default but be robust to GPT.

**Hook system:** Formal "hooks," shared with Copilot coding agent. Configured as **JSON** in `.github/hooks/*.json` (repo, must be on default branch for cloud agent) or `~/.copilot/hooks/*.json` (personal, CLI only). Schema requires `"version": 1` and a `hooks` object; handlers use OS‑keyed `bash`/`powershell` commands.

```json
{
  "version": 1,
  "hooks": {
    "sessionStart": [ { "type": "command", "bash": "./scripts/banner.sh", "timeoutSec": 10 } ],
    "preToolUse":   [ { "type": "command", "bash": "./scripts/policy.sh", "timeoutSec": 15 } ]
  }
}
```

**Events (camelCase):** `sessionStart`, `sessionEnd`, `userPromptSubmitted`, `preToolUse`, `postToolUse`, `permissionRequest` (CLI only), `subagentStart`, `subagentStop`, `errorOccurred`, `notification`. (VS Code's adapter maps these to PascalCase.)

**Context injection — the critical limitation:** For **`sessionStart` and `userPromptSubmitted`, the hook's output is *ignored*** ("Any output from this hook is ignored… suitable for informational messages"). The .NET SDK documents an `additionalContext` field on `onSessionStart`/`onUserPromptSubmitted`, but there are confirmed bugs where it is **silently dropped** (SDK issue #775; the pre‑v1.0.11 `onSessionStart` fire‑and‑forget bug). Where injection *does* work:
- `postToolUse` → `additionalContext` (appended to tool result, model‑visible).
- `notification` hook → `additionalContext` "injected as a prepended user message."
- `subagentStart` → `additionalContext` into the subagent prompt.
- Since CLI v1.0.44, `userPromptSubmitted` can write `{"response": "..."}` to short‑circuit and answer the user directly (bypasses the LLM — *not* a context injection).

`preToolUse` is the powerful gate (fail‑closed; can `allow`/`deny` and rewrite args via `modifiedArgs`/`updatedInput`).

**Bottom line for Copilot CLI:** You **cannot reliably push a per‑turn memory hint through hooks.** Put your "memories exist / when to use them" guidance in the **instruction file**, and optionally use a `postToolUse` or `notification` hook for nudges after specific tool calls.

**Instruction file:** **`AGENTS.md`** is natively read (Copilot walks the directory tree, nearest file wins), alongside Copilot‑specific **`.github/copilot-instructions.md`**. Custom CLI agents use Markdown with YAML frontmatter.

---

### Reference system: GSD ("Get Shit Done")
GSD is a meta‑prompting / spec‑driven development system (npm `get-shit-done-cc`, plus a redux/SDK lineage and a Claude‑Code‑native plugin) that supports Claude Code, OpenCode, Gemini CLI, Codex, Copilot, Cursor, Windsurf and more. Why it feels more reliable than ad‑hoc XML:
- **It translates per‑runtime at install time.** Its installer "detects and adapts to your runtime… translating tool names, hook events, agent formats, and path conventions automatically" — i.e., it does exactly what you're proposing (per‑CLI customization) rather than emitting one generic format.
- **It leans on persistent file‑state, not just ephemeral hook injection.** State lives in structured Markdown with **YAML frontmatter** and **XML‑formatted PLAN.md task blocks** under `.planning/`/`.gsd/`, so guidance is "always‑on" in context rather than dependent on a single injection that decays.
- **It uses hooks for the deterministic bookends** — `SessionStart` context‑loading, `PreCompact` writing a `HANDOFF.json` and `PostToolUse` checkpoints so state survives compaction, and auto‑resume on the next `SessionStart`. The reliability comes from *redundancy*: file state + session‑start load + compaction‑survival hooks, not from one clever prompt.

The lesson for Scrinia: reliability is a function of **(1) the right channel, (2) per‑turn re‑assertion, and (3) a persistent file backstop** — not of finding one magic XML wrapper.

---

## Recommendations

**Stage 1 — Match the channel to each CLI (do this first; biggest reliability win):**
- **Codex:** Keep doing what works. Inject via `UserPromptSubmit` `hookSpecificOutput.additionalContext` (→ developer message). Keep the XML wrapper (GPT‑5 is documented to follow structured XML specs like `<..._spec>`). Add a one‑time richer block on `SessionStart`. Avoid `PreToolUse` for injection (dropped).
- **Claude Code:** Inject via `UserPromptSubmit` `additionalContext` (→ `<system-reminder>`). Use **XML tags** — Anthropic documents Claude's strong XML adherence. Because injections **accumulate**, keep the per‑turn hint to 1–3 terse lines and put the full "guide" in `SessionStart` or `CLAUDE.md`. Use plain‑text stdout if you hit the first‑message JSON bug.
- **Gemini CLI:** Inject via `BeforeAgent` `additionalContext`. Use **markdown headers with light XML** for the memory block. Mirror Google's own memory‑hook example structure.
- **OpenCode:** Ship a tiny plugin using `experimental.chat.system.transform` for the always‑on hint, plus `AGENTS.md` as the durable baseline. Treat the plugin API as unstable and pin the OpenCode version.
- **Copilot CLI:** **Do not rely on hooks for hints.** Put the memory guidance in `AGENTS.md` (+ `.github/copilot-instructions.md`). Use `postToolUse`/`notification` `additionalContext` only for post‑action nudges.

**Stage 2 — Format to the model, not the tool:**
- GPT‑5.x (Codex; Copilot‑on‑GPT): XML‑tagged, imperative, named sections (`<scrinia_memory_policy>`), short. GPT‑5 follows instructions literally — be explicit about *when* to call `create`/`retrieve`.
- Claude (Claude Code; Copilot default): XML tags, imperative; Claude weights `system-reminder` content heavily.
- Gemini: markdown headings + bullet imperatives, light XML acceptable.
- OpenCode/multi‑model: detect the provider (`chat.params`/`system.transform` `input.model`) and branch the wrapper, GSD‑style.

**Stage 3 — Add the persistent backstop (the GSD lesson):**
Always also write the memory contract into each tool's instruction file (`AGENTS.md` for Codex/OpenCode/Copilot, `CLAUDE.md`, `GEMINI.md`). The hook hint becomes the *per‑turn reminder*; the file is the *always‑on baseline*. Your MCP "guide" tool is the third layer (on‑demand depth). Redundancy across all three is what makes GSD‑class systems reliable.

**Stage 4 — Instrument and tune:** Log MCP `create`/`retrieve` invocation rates per CLI. **Benchmarks that should change your approach:**
- If Claude Code context bloats or latency rises → you're hitting `additionalContext` accumulation; move bulk to `SessionStart`/`CLAUDE.md`, shorten per‑turn hint.
- If a CLI's invocation rate stays low despite injection → the channel is weak (Copilot) or the hint is too long/buried; shorten, move up the authority ladder (developer > system‑reminder > user‑append), or rely on the instruction file + MCP tool description.
- If Codex starts showing the hint as a visible transcript message or breaks on multiline → a version regression; pin/downgrade and watch the linked issues.

## Caveats
- **These hook systems are changing fast (mid‑2026).** Codex hooks only recently went stable; Gemini hooks launched in v0.26.0; Copilot hooks and OpenCode's plugin chat‑hooks are partially undocumented. Treat every event name and field as version‑pinned and add a smoke test per CLI per upgrade.
- **Several documented behaviors have open bugs:** Claude Code `additionalContext` accumulation (#40216) and first‑message JSON error (#17550); Codex `PreToolUse.additionalContext` dropped and TUI rendering quirks (#16486, #16933); Copilot `onSessionStart`/`additionalContext` silently ignored in some versions (#775). Don't assume the docs reflect runtime — verify with a logging hook.
- **OpenCode chat hooks are unofficial/experimental** and lack published types; the official docs only cover `experimental.session.compacting`. The directory is scanned as both `.opencode/plugin/` and `.opencode/plugins/`.
- **Copilot CLI's default model is Claude Sonnet 4.5**, not an OpenAI model — counterintuitive given the "Copilot = OpenAI" prior. Format accordingly and re‑check if the user switches models.
- **No CLI automatically wraps your injected text in XML/markdown** (except Claude Code's `system-reminder` envelope). All tag conventions are yours to supply and keep consistent.
- **The Claude Code event count is unsettled across sources** (21 vs. 29 cited); rely on the cadence groupings in the official reference and pin a version rather than a number.
- AGENTS.md is now a Linux Foundation / Agentic AI Foundation standard read by Codex, Copilot, OpenCode, Cursor, and others; Claude Code (CLAUDE.md) and Gemini CLI (GEMINI.md) remain proprietary but can reference/symlink it.