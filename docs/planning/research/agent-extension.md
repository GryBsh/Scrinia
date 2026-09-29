# Beyond Hooks: The Full Non-Hook Extensibility Surface of Five Agentic Coding CLIs — and How Scrinia Should Use It

## TL;DR
- **Skills (SKILL.md) and instruction files (AGENTS.md/CLAUDE.md) are now your highest-leverage, most portable injection surfaces.** SKILL.md became an open standard on December 18, 2025 (first launched as a Claude feature Oct 16, 2025) and is supported natively by Claude Code, Codex CLI, Gemini CLI, OpenCode, and GitHub Copilot — a single Scrinia "memory skill" plus an AGENTS.md block reaches all five tools with model-driven (autonomous) invocation.
- **MCP prompts and MCP resources are only fully usable on Claude Code and Gemini CLI** (prompts surface as `/mcp__server__prompt` slash commands; resources via `@` mentions). **Codex CLI and GitHub Copilot CLI support MCP tools only** — both have open feature requests (Codex #5059/#8342, Copilot #1518) for prompts/resources — so on those two tools Scrinia must lean on skills + AGENTS.md, not MCP prompts/resources.
- Concretely: ship (1) a SKILL.md "scrinia-memory" skill with a keyword-rich, "pushy" description; (2) an AGENTS.md/CLAUDE.md snippet (or `@import` rule) telling the agent memories exist; (3) on Claude/Gemini, expose an MCP *prompt* (slash-command guide) and MCP *resources* (memory docs). Keep your existing MCP `guide` tool as the universal fallback that works everywhere.

## Key Findings

### The two cross-tool standards that cut your work dramatically
1. **SKILL.md (Agent Skills)** — originated by Anthropic and published as an open standard (agentskills.io) on December 18, 2025 ("Update: We've published Agent Skills as an open standard for cross-platform portability"), supported by Claude Code, Codex CLI, Gemini CLI, OpenCode, and GitHub Copilot. A skill is a directory with a `SKILL.md` file; YAML frontmatter requires `name` and `description`; the body is Markdown. The **description field is the autonomous-invocation trigger** — per Anthropic, "At startup, the agent pre-loads the name and description of every installed skill into its system prompt. This metadata is the first level of progressive disclosure," and the model decides when to load the full body. This is the single best vehicle for autonomous, model-driven guidance because it travels across all five tools nearly unchanged.
2. **AGENTS.md** — a cross-tool instruction-file convention honored by Codex CLI, OpenCode, and Copilot CLI (and bridged by Claude via CLAUDE.md). Always-on context injected at session start.

### MCP-native extensibility is uneven across the five
- **Claude Code**: MCP prompts → `/mcp__server__prompt` slash commands; MCP resources → `@server:protocol://path` mentions; supports elicitation, roots, sampling, `list_changed` notifications. (Caveat: MCP prompts are user-invocable only — not visible to the model for autonomous suggestion, per open issue #11054.)
- **Gemini CLI**: MCP prompts → slash commands (`/promptname`, args via `--arg=` or positional); MCP resources read-only and de-emphasized ("primarily focuses on tool execution").
- **Codex CLI**: **MCP tools only.** No prompts, no resources. Reads the MCP server `instructions` field as server-wide guidance (first ~512 chars matter most).
- **Copilot CLI**: **MCP tools only.** Prompts/resources exist in VS Code Copilot Chat but NOT the CLI.
- **OpenCode**: MCP tools, resources, and prompts supported via config; permissions wildcard-match MCP tool names.

---

## Details (per-CLI catalogue of every non-hook mechanism)

### 1. Claude Code (Anthropic)

**Agent Skills** — `.claude/skills/<name>/SKILL.md` (project) or `~/.claude/skills/` (personal), or bundled in plugins. Frontmatter: `name`, `description` (required), optional `allowed-tools`, `disable-model-invocation` (true = user-only `/name`), `model`, `context`, `agent`. The folder name (or `name`) becomes a `/slash-command`. Skills are **model-invocable** (autonomous) unless disabled. As of Claude Code v2.1.101 (April 11, 2026) **custom slash commands were merged into skills** — `.claude/commands/*.md` still works but `.claude/skills/<name>/SKILL.md` is the recommended format; if both share a name, the skill wins. Description budget: ~1% of context window; raise with `skillListingBudgetFraction` or `SLASH_COMMAND_TOOL_CHAR_BUDGET`; run `/doctor` to check overflow.

**Slash commands (legacy `.claude/commands/`)** — Markdown + YAML frontmatter: `allowed-tools`, `argument-hint`, `description`, `model`. Argument substitution: `$ARGUMENTS`, `$1`–`$9`; `` !`bash` `` embeds shell output; `@file` references. User-invoked.

**Subagents** — `.claude/agents/*.md` with frontmatter `name`, `description`, `tools`, `model`. Built-ins: Explore, Plan, general-purpose. Body is the subagent's system prompt. Delegated autonomously or via `/agents`.

**Memory / context files** — `CLAUDE.md` (project root), `~/.claude/CLAUDE.md` (user), nested directory CLAUDE.md (hierarchical), `CLAUDE.local.md` (gitignored personal). `@path/to/file` **import syntax**, recursive up to 5 levels. `.claude/rules/` directory: topic files auto-discovered recursively; path-scoped rules load only when matching files are touched. Managed-policy CLAUDE.md can't be excluded. `/memory` command to view/edit loaded files; auto-memory (v2.1.59+) at `~/.claude/memory`. Project-root CLAUDE.md survives `/compact` (re-read from disk).

**MCP prompts/resources** — prompts as `/mcp__server__prompt` (args space-separated); resources as `@server:protocol://resource/path`, fetched as attachments. Tool Search defers tool defs (`ENABLE_TOOL_SEARCH`).

**Output styles** — `.claude/output-styles/*.md` (user-level `~/.claude/output-styles`), YAML frontmatter. Directly **modify the system prompt** (turn off SWE-specific instructions), unlike CLAUDE.md (user message) or `--append-system-prompt` (appends). Built-ins: Explanatory, Learning. `/output-style`, `/output-style:new`. Saved to `.claude/settings.local.json`. (Note: output styles are increasingly migrated into plugins/skills.)

**Plugins** — `.claude-plugin/plugin.json` manifest; bundles `commands/`, `agents/`, `skills/`, `hooks/`, `output-styles/`, `.mcp.json`, LSP. Marketplace catalog `marketplace.json`; install via `/plugin`. Skills-directory plugins auto-load as `<name>@skills-dir`. To ship context, put it in a skill (plugin-root CLAUDE.md is NOT loaded).

**Config-level injection** — `--append-system-prompt`, `--system-prompt` (SDK), `--settings`, model config.

### 2. Codex CLI (OpenAI)

**Agent Skills** — native, surfaced Dec 12, 2025 (six days before Anthropic's open-standard announcement; per Simon Willison, "skills support quietly showing up in both their Codex CLI tool and now also in ChatGPT itself"). Locations: `~/.codex/skills/` (personal), `.codex/skills/` (project), and cross-tool `~/.agents/skills/`. Entry file must be exactly `SKILL.md` (case-sensitive; issue #20637). Progressive disclosure: skills list capped at ~2% context / 8,000 chars. Optional `agents/openai.yaml` for UI metadata + `policy.allow_implicit_invocation` (default true; false = explicit `$skill` only). Disable via `[[skills.config]]` in config.toml. Invoke with `$skillname` (explicit) or autonomously by description match (implicit). **Skills are now OpenAI's recommended replacement for custom prompts.**

**Custom prompts (deprecated)** — `~/.codex/prompts/*.md`; filename → `/name` (or `/prompts:name`). Args `$1`–`$9`, `$ARGUMENTS`, `$$`. Explicit-invocation only, local (not repo-shared). OpenAI now says "use skills" instead.

**Subagents** — config-driven via `[agents]` table in config.toml; Codex only spawns subagents when explicitly asked. `/review` runs a dedicated reviewer agent.

**Instruction files (AGENTS.md hierarchy)** — `AGENTS.override.md` → `AGENTS.md` → `project_doc_fallback_filenames`, walked root→CWD, concatenated (later/closer overrides), size cap `project_doc_max_bytes` (32 KiB default). Pulled from `~/.codex` plus each dir. Each file becomes its own user-role message: `# AGENTS.md instructions for <dir>`. `/init` creates one.

**Config-level instruction injection** — `experimental_instructions_file` (replaces base system prompt, with validation); `developer_instructions` (developer-role messages); `model_instructions_file`. `personality = friendly|pragmatic|none`. Profiles (`~/.codex/profile-name.config.toml`).

**MCP** — `[mcp_servers.<name>]` in `~/.codex/config.toml` or project `.codex/config.toml` (trusted only); `codex mcp add`. **Tools only** — no MCP prompts (open issues #5059, #8342), no MCP resources (issue #8565). Reads server `instructions` field as guidance. `granular` approval policy includes `mcp_elicitations`.

**Plugins** — `codex plugin` / plugin marketplaces (Git or local); plugins can bundle MCP servers and skills.

### 3. Gemini CLI (Google)

**Agent Skills** — `SKILL.md` standard; locations `.gemini/skills/` or `.agents/skills/` (workspace, `.agents` alias wins), `~/.gemini/skills/` or `~/.agents/skills/` (user), extension skills, built-in. Model calls `activate_skill` tool on description match → **consent prompt** on first activation, then body + folder added to context and allowed paths. `/skills list|link|disable|enable|reload`. Frontmatter requires `name` + `description` (silently skipped if missing/malformed). Workspace skills require `/trust`.

**Custom commands (TOML)** — `~/.gemini/commands/*.toml` (user) or `.gemini/commands/*.toml` (project). Keys: `prompt` (required), `description`. Subdirs → namespaced `/parent:child`. Args: `{{args}}` (context-aware, shell-escaped in `!{}`), positional `$1`; `!{cmd}` shell injection; `@{file}` file/dir/multimodal injection. `/commands reload|list`.

**Subagents** — `.gemini/agents/*.md` (project) or `~/.gemini/agents/*.md` (user), YAML frontmatter, body = system prompt; tool wildcards `*`, `mcp_*`, `mcp_<server>_*`. `/agents list|enable|disable|configure`. Exposed to main agent as a tool; force with `@agentname`. Built-ins: codebase_investigator, CLI help agent, generalist.

**Context files (GEMINI.md)** — hierarchical; `contextFileName` configurable per extension; `/memory` command; `/init` generates one. `includeDirectories` for multi-dir workspace.

**MCP** — `mcpServers` in settings.json; **MCP prompts → slash commands** (`/promptname`, `--arg=`/positional; `/mcp list`); **MCP resources read-only and de-emphasized** ("Gemini CLI primarily focuses on tool execution"). `/mcp refresh`.

**Extensions** — `gemini-extension.json` manifest in `.gemini/extensions/<name>/`; bundles `mcpServers`, `commands/` (TOML), `GEMINI.md` context, `excludeTools`. `gemini extensions install/list/update`. Commands lowest precedence (renamed `/ext.cmd` on conflict).

### 4. OpenCode

**Agent Skills** — native; `.opencode/skills/` (project), `~/.config/opencode/skills/`. Follows Anthropic spec; only `name`+`description` frontmatter recognized (unknown fields ignored). Listed in the `skill` tool description; model-invocable. (Plurals `skills/` standard; singular accepted for back-compat.)

**Custom commands** — Markdown in `command/` (or `commands/`) dirs, or JSON in opencode.json `command` field. Frontmatter + `template`/content body. Args `$ARGUMENTS`, `$1`; `!cmd` bash output; `@file` references. User-invoked `/name`.

**Agents (primary vs subagent)** — Markdown + YAML frontmatter in `agent/` dirs, or JSON in config. Fields: `description`, `mode` (`primary`|`subagent`|`all`), `model`, `prompt` (system-prompt file path), `tools`/`permission`, `temperature`/`top_p`, `color`. Primary agents cycled with Tab; subagents invoked via `@mention` or Task tool. `opencode agent create`. Task-tool permission `deny` removes a subagent from delegation.

**Custom tools (non-MCP)** — `.opencode/tool/*.ts` (project) or `~/.config/opencode/tool/`; TypeScript/JS using `tool()` helper + Zod schema; filename = tool name; can shell out to any language. Can override built-ins.

**Rules / instruction files** — `AGENTS.md` (`/init`); `instructions` array in opencode.json (supports globs, remote URLs w/ 5s timeout); first matching of AGENTS.md/CLAUDE.md wins. File refs in AGENTS.md require explicit "use your Read tool" instruction.

**Modes** — `modes/` dir; primary/plan/build modes; permissions per agent/mode.

**Plugins (TypeScript)** — `plugin/` dir; beyond hooks, can register custom tools and commands. Config precedence: remote (.well-known) < global < OPENCODE_CONFIG < project < managed.

**MCP** — `opencode mcp add`; tools, resources, prompts supported; permission wildcards match MCP tool names (`mymcp_*`).

### 5. GitHub Copilot CLI

**Custom agents** — `.agent.md` files; locations `~/.copilot/agents/` (user), `.github/agents/` (repo), org/enterprise `.github-private`. Frontmatter: `name`, `description` (required), optional `tools`, `target` (`vscode`|`github-copilot`), MCP config; body = prompt (max 30,000 chars). Invoke via `/agent`, natural-language description match (autonomous, runs as subagent), or `--agent name`/`--agent=name --prompt`. Built-ins (e.g., rubber-duck) auto-consulted.

**Agent Skills** — supported (open standard, same SKILL.md works across Copilot CLI, VS Code, coding agent, Claude Code). Progressive disclosure: name+description pre-loaded, body on match, referenced files level 3.

**Custom instructions** — `.github/copilot-instructions.md` (repo-wide, always-on); path-specific `.github/instructions/*.instructions.md` with `applyTo:` glob frontmatter (+ optional `excludeAgent`); `AGENTS.md` (root/CWD/`COPILOT_CUSTOM_INSTRUCTIONS_DIRS`). **All instruction files now combine** (no priority fallback); matching `.instructions.md` files stack/union. Available immediately on save.

**Slash commands** — built-in only (`/agent`, `/plan`, `/review`, `/pr`, `/delegate`, `/fleet`, `/mcp`, `/chronicle`, etc.); **users cannot create custom slash commands** in the CLI (use custom agents instead).

**Copilot Memory** — persistent repo understanding ("memories" of conventions Copilot deduces).

**MCP** — `~/.copilot/mcp-config.json`; GitHub MCP server pre-configured. **Tools only** — no MCP prompts, no MCP resources in the CLI (open issue #1518); the `/mcp.server.prompt` format and resource context-add exist only in VS Code, not the CLI. No `@mcp` explicit-invocation syntax (issue #2690).

---

## Comparative matrix

| Mechanism | Codex CLI | Claude Code | Gemini CLI | OpenCode | Copilot CLI |
|---|---|---|---|---|---|
| **SKILL.md skills (autonomous)** | ✅ `~/.codex/skills`, `~/.agents/skills` | ✅ `.claude/skills` | ✅ `.gemini/skills`, `.agents/skills` | ✅ `.opencode/skills` | ✅ skills (open standard) |
| **Custom slash commands** | ✅ `~/.codex/prompts` (deprecated→skills) | ✅ `.claude/commands` (merged→skills) | ✅ `.gemini/commands` (TOML) | ✅ `command/` | ❌ built-ins only |
| **Subagents/custom agents** | ✅ `[agents]` config | ✅ `.claude/agents` | ✅ `.gemini/agents` | ✅ `agent/` md/JSON | ✅ `.agent.md` |
| **AGENTS.md / instruction files** | ✅ AGENTS.md hierarchy | ✅ CLAUDE.md + @import + rules | ✅ GEMINI.md + includeDirs | ✅ AGENTS.md + instructions[] | ✅ AGENTS.md + copilot-instructions + applyTo |
| **MCP tools** | ✅ | ✅ | ✅ | ✅ | ✅ |
| **MCP prompts (as slash cmds)** | ❌ (#5059/#8342) | ✅ `/mcp__srv__prompt` | ✅ `/promptname` | ✅ | ❌ (#1518) |
| **MCP resources (@-mention)** | ❌ (#8565) | ✅ `@srv:proto://path` | ⚠️ read-only, de-emphasized | ✅ | ❌ (#1518) |
| **MCP server `instructions` field** | ✅ used as guidance | ✅ | ✅ | ✅ | partial |
| **Output styles / system-prompt override** | ✅ `experimental_instructions_file`, `personality` | ✅ output-styles + `--append-system-prompt` | persona via agents | ✅ modes + agent prompt | persona via agents |
| **Plugins/extensions** | ✅ plugin marketplaces | ✅ plugins + marketplace | ✅ extensions | ✅ TS plugins | (skills/agents/MCP) |
| **Custom tools (non-MCP)** | via skills/scripts | via skills/scripts | via extensions | ✅ `.opencode/tool` TS | via skills |

---

## Recommendations for Scrinia (an MCP memory server)

**Stage 1 — Universal baseline (works on all five tools):**
1. **Ship a `scrinia-memory` SKILL.md** as the primary autonomous guidance vehicle. Distribute it to `~/.agents/skills/scrinia-memory/SKILL.md` (Codex + Gemini alias), `~/.claude/skills/`, `.opencode/skills/`, and Copilot's skills dir. Write a **"pushy," keyword-rich description in third person** that names the triggers explicitly — e.g., *"Manages long-term agent memory via the Scrinia MCP tools. Use this whenever the user references past work, prior decisions, established conventions, 'remember', 'last time', or starts a new task that could benefit from stored context — and create a memory after completing meaningful work."* This is the description that drives autonomous loading on every tool.
2. **Provide an AGENTS.md snippet** (and a CLAUDE.md variant) users paste/import, stating that Scrinia memory tools exist, when to retrieve (task start) and when to create (after meaningful changes/decisions). This is always-on and covers Codex/Copilot where MCP prompts/resources are absent. For Claude, deliver it as a `.claude/rules/` file or `@import`.
3. **Keep your existing MCP `guide` tool** as the lowest-common-denominator fallback — it works identically across all five since all support MCP tools.

**Stage 2 — Per-tool MCP enrichment (Claude Code + Gemini CLI only):**
4. **Expose an MCP prompt** ("scrinia-guide" / "scrinia-recall") so it surfaces as `/mcp__scrinia__recall` (Claude) and `/promptname` (Gemini) for explicit user invocation. On Claude, remember the prompt is user-invocable only (issue #11054), so don't rely on it for autonomous behavior — pair it with the skill.
5. **Expose MCP resources** for memory documents so the agent (Claude) can `@scrinia://memories/...` them into context; on Gemini treat resources as best-effort (read-only, de-emphasized).
6. **Set a strong MCP server `instructions` field** (first ~512 chars self-contained). Codex reads this as server-wide guidance even though it ignores prompts/resources — a free win on Codex.

**Stage 3 — Deeper packaging (optional):**
7. Bundle the skill + commands + MCP config as a **Claude Code plugin** and a **Gemini CLI extension** for one-command install; ship a Copilot **custom agent** (`scrinia-memory.agent.md`) and an OpenCode **plugin/skill** for parity.

**Benchmarks that would change this plan:** If Codex closes #5059/#8342 or Copilot closes #1518 (MCP prompts/resources land in those CLIs), promote Stage 2 to universal and reduce reliance on per-tool AGENTS.md duplication. If Claude resolves #11054 (MCP prompts visible to the model), you can drop the parallel skill on Claude. Track adoption of the `.agents/skills/` path as the converging cross-tool skills location.

## Caveats
- **Fast-moving surface.** Claude Code merged commands into skills (v2.1.101, April 2026); Codex skills surfaced Dec 12, 2025 and custom prompts are now deprecated; **Gemini CLI's consumer access is sunsetting** — announced May 19, 2026 at Google I/O, "On June 18, 2026, Gemini CLI and Gemini Code Assist IDE extensions will stop serving requests for Google AI Pro and Ultra, as well as those using it free of charge" (enterprise Gemini Code Assist Standard/Enterprise and paid-API-key access are unchanged). Its successor **Antigravity CLI** (invoked as `agy`, written in Go) is explicitly NOT 1:1 — Google states it "keeps the most critical features of Gemini CLI: Agent Skills, Hooks, Subagents, and Extensions (now as Antigravity plugins)." Skills and AGENTS.md will carry forward; verify before committing engineering to Gemini-specific extensions.
- **"NOT supported" findings rest on currently-open GitHub issues** plus absence from docs (Codex #5059/#8342/#8565; Copilot #1518/#2690); none had a maintainer "implemented" resolution at retrieval.
- **VS Code ≠ CLI for Copilot.** The widely-documented `/mcp.server.prompt` slash format and MCP-resource context-adding are VS Code Copilot Chat features, not Copilot CLI.
- **MCP prompts are user-invoked, not autonomous** on Claude (issue #11054) and generally; skills/instruction files are the autonomous path.
- **Skill description quality dominates reliability.** Across all tools the description is the only thing pre-loaded; vague descriptions fail to trigger. Test with should-trigger/should-not-trigger eval queries.
- Codex skill entry file must be **exactly `SKILL.md`** (case-sensitive on macOS; issue #20637).