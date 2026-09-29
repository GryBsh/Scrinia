# UX Polish Review — CLI banner, `scri setup` summary, `memory_search` description

**Date:** 2026-06-23
**Scope:** `src/Scrinia/Program.cs` (CLI banner), `src/Scrinia/Commands/ScriniaCommands.Setup.cs` (setup summary), `src/Scrinia.Mcp/MemoryTools.cs` (`memory_search` tool description) — the three user/agent-visible string changes on branch `chore/llm-removal-stale-strings`.
**Reviewer:** ux-polish-reviewer agent

**State:** All three rewrites are clean, accurate, and on-voice. The LLM-at-rest references are fully purged, every factual claim is verified against the code, and the new copy is consistent with the README and primer. No must-fix or should-fix issues. Two minor consistency observations to consider.

## Must fix
None.

## Should fix
None.

## Consider

**1. CLI banner — `src/Scrinia/Program.cs:10`** (already good)
`Scrinia {version} — Persistent, portable memory for AI agents.`
This matches the README's lead sentence verbatim ("Persistent, portable memory for AI agents.") — exactly the consistency you want between the landing copy and the first thing a CLI user sees. The em-dash is house style (README uses them throughout). Ship as-is.

**2. `scri setup` summary — `ScriniaCommands.Setup.cs:23`** (already good)
`Download embedding models for built-in providers and installed plugins.`
Verified against the command body: it downloads the built-in Model2Vec model (line 84–101) and the Vulkan plugin GGUF (line 104–135), and the LLM-model download is gone. The summary is now accurate. Tight, imperative, matches the verb-first convention of the other summaries (`Get or set...`, `Rebuild...`).
- One optional thought: the command also does meaningful non-download work — Ollama auto-detect, hook install/uninstall, multi-user git config. The summary only advertises the download path. That's defensible (downloading is the primary flow and the flags are documented per-parameter), so I'd leave it unless you find users surprised that `setup` also installs hooks. Flagging, not prescribing.

**3. `memory_search` description — `MemoryTools.cs:152-153`** (already good)
`Find memories by keyword or phrase. Returns ranked matches with description, tags, and retrieval anchors per hit so you can decide what to recall without a follow-up call.`
All three claims verified against `FormatRichSearchResults` / `AppendResultBlock` (`MemoryTools.Core.cs:814-934`): each hit emits `tags:`, `description:`, and `anchors:` (sourced from `RetrievalAnchors`). "retrieval anchors" is the canonical term (README, write-side tool descriptions). "without a follow-up call" is true — the rich format exists precisely to avoid a per-candidate `memory_recall`. The example is well-formed. No change needed.

## Consistency notes
- **Terminology purge is complete and correct.** "LLM models" → dropped from setup; "LLM-extracted facts" → "retrieval anchors." Both align with the product's stated architecture: no LLM consultation on the server at read/write time (README line 7). No surviving LLM-at-rest claims in these surfaces.
- **Canonical positioning line is now used consistently** across README intro and CLI banner. Keep these two in lockstep on any future edit.
- **"retrieval anchors"** is the agreed term for agent-supplied query-vocabulary phrases (lowercase in prose, `RetrievalAnchors` as the code field, `anchors:` in output). Used consistently here.
- Verb-first command summaries and em-dash usage match existing house style.
