---
name: product-voice-terminology
description: Scrinia's established product voice, canonical terms, and copy conventions for user-facing and agent-facing strings
metadata:
  type: project
---

Scrinia is a local-first MCP server: persistent, portable memory for AI agents. Tagline framing: "agents bring the intelligence; scrinia provides the substrate."

**Canonical positioning line:** "Persistent, portable memory for AI agents." (used in README intro and CLI banner). Replaced the older "Cognitive toolkit for LLMs." — the team deliberately purged LLM-at-rest framing (no LLM consultation on the server at read/write time).

**Canonical terms:**
- "AI agents" / "agents" — the audience and actors. Not "LLMs" in product-facing positioning (LLM still fine in technical/architecture prose where accurate).
- "retrieval anchors" (lowercase in prose) / `RetrievalAnchors` (the code field) — query-vocabulary phrases an agent supplies at write time. Surfaced in search results as `anchors:`. This is the canonical replacement for the removed "LLM-extracted facts" language.
- "memories", "embedding models", "embedding provider", "workspace".

**Voice characteristics:**
- Confident, technical, terse. Comfortable with em-dashes (README uses them heavily — house style, not an LLM tell here).
- Lowercase "scrinia" in running prose is acceptable (README does it); "Scrinia" capitalized in the CLI banner and headings.
- Bold inline emphasis for key claims is established (README **bolds** the substrate line and the no-LLM claim).

**De-LLMing note specific to this repo:** the active cleanup theme is removing references to a *removed* LLM-at-rest feature. When reviewing copy, flag any surviving claim that the server itself extracts/summarizes/consults an LLM at read or write time — that capability does not exist. The agent is the only source of LLM enrichment.

**Key surfaces:**
- CLI banner: `src/Scrinia/Program.cs` (printed on `scri` / `scri --help`).
- Command summaries: XML `<summary>` doc comments on command methods in `src/Scrinia/Commands/ScriniaCommands.*.cs` (ConsoleAppFramework surfaces these in `--help`).
- MCP tool descriptions (agent-facing): `[Description(...)]` attributes in `src/Scrinia.Mcp/MemoryTools.cs` and `MemoryTools.Core.cs`.
- Agent primer / managed AGENTS.md+CLAUDE.md block: `src/Scrinia.Mcp/ScriniaPrimer.cs`.
- README + benchmarks: `README.md`.
