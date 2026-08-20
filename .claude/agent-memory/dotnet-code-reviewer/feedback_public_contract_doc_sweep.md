---
name: public-contract-doc-sweep
description: On any public-interface change, sweep docs/architecture/*.md and docs/plugin-authoring.md by hand — they quote interface source verbatim and no test guards them.
metadata:
  type: feedback
---

When reviewing a branch that changes a **public interface signature** in `Scrinia.Core`, grep
`docs/architecture/*.md` and `docs/plugin-authoring.md` for the interface name before signing off.

**Why:** `docs/architecture/core.md` publishes whole interface bodies as verbatim C# code blocks
(`IMemoryEventSink` is one). Nothing verifies them — a grep of `tests/` for `core.md` returns zero,
so a signature change leaves the doc silently wrong and every test stays green. Caught exactly this
on `refactor/plugin-contract-narrowing`: the sink contract narrowed to `IPluginStore` in code while
`core.md:632-637` still showed `IMemoryStore store` in all three methods.

**How to apply:** the SDD per-task briefs never list these doc files, so no per-task reviewer can
see the gap — it only surfaces at the whole-branch seat. Treat it as a standing checklist item
there, alongside CHANGELOG. See [[review-method]] for the falsification discipline that pairs
with it.
