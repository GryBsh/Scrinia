# UX Polish Review — Shared MCP operations

**Date:** 2026-10-10
**Scope:** Reader-facing messages in `src/Scrinia.Mcp/Shared/SharedOperations.cs` and `SharedValidation.cs`; added sections in `docs/multi-user-setup.md` and `docs/troubleshooting.md`, compared with `ded4cfc874158feb9f4433ce4d1c21dde7ffe76a`.
**Reviewer:** ux-polish-reviewer agent

The copy is calm and mostly accurate; two recovery instructions need correction, with two smaller clarity improvements.

## Must fix

1. **Manual repair omits the exact file-format requirements and ambiguous-marker case.** Location: `docs/troubleshooting.md:332–336`.

   The displayed rules match `SharedClassifier.Rules`, but copying them into a Windows editor can still leave the base blocked. `ManagedIgnoreBlock.Validate` rejects carriage returns and a leading BOM; `TryGetProjectRegion` requires exactly one marker pair and the exact block, including its final newline. “If there is no managed region, add this block” also risks adding a second region when existing markers are malformed. State the required format and distinguish absent markers from broken ones.

   **Before:** “In the registered base's `.scrinia/.gitignore`, replace only the region between the two Scrinia managed markers with the block below. Preserve all user content outside that region. If there is no managed region, add this block.”

   **After:** “In the registered base's `.scrinia/.gitignore`, replace the existing managed block, including both marker lines, with the block below. Preserve user rules outside it. If neither marker is present, append the block. If markers are duplicated or malformed, repair them so exactly one complete block remains. Save the entire file as UTF-8 without a BOM, with LF line endings and a newline after the closing marker.”

2. **Unknown write guidance needs refreshed inspection before a retry.** Locations: `src/Scrinia.Mcp/Shared/SharedOperations.cs:289`; `docs/multi-user-setup.md:60–62`.

   The error instruction gives the same advice for a refused stale write and an unknown outcome. An unknown outcome can follow partial durable writes or a completed callback whose refreshed state cannot be validated (`SharedBaseRuntime.CommitMutationUnderLease`). Refresh alone does not establish whether repeating the operation is appropriate. The documentation also puts inspection before refresh, although MCP reads use the held snapshot. Give unknown outcomes their own instruction, and cover every mutation that can return this certainty.

   **Before — MCP instruction for `mutation: unknown`:** “Refresh this base before another mutation.”

   **After:** “The operation may have changed files. Call shared_refresh for this base, then inspect the affected entries before deciding whether to retry.”

   **Before — documentation:** “If a link or delete reports `mutation: unknown`, inspect the affected entries and refresh before retrying. A link updates its two endpoints in sequence; an uncertain outcome may include a completed first endpoint.”

   **After:** “If a shared mutation reports `mutation: unknown`, call `shared_refresh` for the base, then inspect the affected entries before deciding whether to retry. A link may have updated only one endpoint.”

   Keep the existing refresh instruction for `SHARED_BASE_STALE` refusals. If refresh fails, the outcome remains unresolved; this rewrite does not promise automatic recovery.

## Should fix

1. **Name the file and policy in the tracking-drift diagnostic.** Location: `src/Scrinia.Mcp/Shared/SharedOperations.cs:298`.

   “Managed tracking block” makes a repairable error harder to locate. `OpenValidatedBase` checks the registered base's `.scrinia/.gitignore` against `TrackingPolicy.ProjectGit`, so the message can identify both directly.

   **Before:** “Restore the shared base managed tracking block, then refresh it.”

   **After:** “Restore the project-git managed block in this shared base's .scrinia/.gitignore, then call shared_refresh.”

2. **Use the exact shared tool names in the visibility limitation.** Location: `docs/multi-user-setup.md:55–57`.

   “Shared recall/list” and “search/facts” are shorthand beside actual `shared_link` and `shared_refresh` names. The product also exposes workspace tools with these verbs, so explicit names make this limitation easier to apply. The limitation itself is verified: file-entity snapshots expose empty attributes and keywords, and shared search/facts candidates include ordinary memory entries only.

   **Before:** “These sidecar links survive restart, but shared recall/list still return empty attributes and keywords for file entities, and search/facts do not index those links.”

   **After:** “These sidecar links survive restart. For skills and agent profiles, `shared_recall` and `shared_list` return empty attributes and keywords. `shared_search` and `shared_facts` do not index these links.”

## Consider

None.

## Consistency notes

- Keep sentence-case diagnostics, full stops, and direct action verbs; `SharedValidation.cs` already follows this style.
- Use “shared base,” “memory,” “skill,” and “agent profile” consistently. Prefer exact `shared_*` tool names when giving instructions.
- Keep `mutation: unknown` distinct from a refused operation; refreshed inspection must precede a retry decision.
- Preserve the exact managed block and manual repair requirement. The documented bracket patterns match the classifier's generated patterns.
- The sidecar-only `ref` visibility and unsaved link reason match `SharedMemoryOperations` and `SharedOperations`. The added documentation's print-only Git claim is consistent with the implementation; no automatic Git action is implied.

Verification was read-only source inspection. No product copy was edited, and no builds or tests were run.
