# Merge Driver Residuals — Design

**Status:** design, awaiting review
**Sequence:** residuals spec 2 of 4. Inventory: `2026-08-08-residuals-triage.md`
(T1.3, T2.3, T2.4, T4.3, T5.6, T5.7, T5.8, T6.2).

## Goal

Close the eight items the `merge-driver-correctness` branch parked because its process allowed
only one fix wave.

Six are contained in `Scrinia.Merge`, its tests, and the multi-user docs. Two — the conflict-scan
coverage gap (§7) and the upgrade-note correction (§8) — reach into `MemoryTools.Lifecycle.cs`,
because conflict *scanning* and *resolution* live on the MCP surface even though conflict
*writing* lives in the driver. Nothing here touches the store or the server.

## Why this is one spec

Every item is in the merge driver's blast radius, and three of them (§1, §2, §3) are on the
same failure path: what the driver does when its configuration or invocation is not what it
expected. Reviewing them together is cheaper than three separate passes over the same file.

## Established facts

| # | Fact | Evidence |
|---|---|---|
| F1 | `IsWithin` returns true when base and candidate are equal | `src/Scrinia.Merge/Nmp2ConflictHandler.cs:118-119` |
| F2 | The containment check is `Path.IsPathRooted(config.ConflictDir) \|\| !IsWithin(dirFull, conflictDir)` — so a `ConflictDir` of `"."` resolves to `dirFull`, passes, and the driver proceeds | `src/Scrinia.Merge/Nmp2ConflictHandler.cs:30-33` |
| F3 | The legacy four-argument branch occupies `Program.cs:13-35`; the top-level `try` opens at `:37` | `src/Scrinia.Merge/Program.cs` |
| F4 | That branch reports both driver keys unconditionally, and `git config --get` on a missing key exits 1 with empty stderr — so the message cannot distinguish "missing" from "failed" | `src/Scrinia.Merge/Program.cs:29-33` |
| F5 | `InsertConflictedFlag` is the only writer of `"conflicted"`; there is no reader anywhere in `src/` | writer `src/Scrinia.Merge/Nmp2ConflictHandler.cs:126`; repo-wide search finds no reader |
| F6 | `LocateDriver` selects by `OrderByDescending(File.GetLastWriteTimeUtc)` | `tests/Scrinia.Merge.Tests/GitMergeHarness.cs:44-47` |
| F7 | `ScanForConflicts` calls `TryReadConflictCopy` for both sides | `src/Scrinia.Mcp/MemoryTools.Lifecycle.cs:250-251` |
| F8 | The upgrade doc still tells the reader the conflict copies "stay out of the" commit, with no untrack guidance | `docs/multi-user-setup.md:52` |
| F9 | `scri setup` always writes `"conflict"` as the ConflictDir | `ScriniaCommands.Setup.cs:352` (per the merge-driver residual; **re-verify, line may have moved**) |

F9 is the one fact carried from memory rather than re-checked for this spec, and it is
load-bearing for §1's severity. Verify it before writing the plan.

## Design

### 1. Strict containment for `ConflictDir`

Require strict containment: `IsWithin` must return false when base equals candidate, at least
for this call site.

**Why this is the sharpest item in the list.** With `ConflictDir` of `"."` or `""`, the
conflict directory resolves to the artifact directory itself, and the driver then writes a
`.gitignore` containing `*` into it. That silently un-tracks every untracked memory at and
below that directory, and clobbers any `.gitignore` the user had there. Before the
`.gitignore` write was added, the same misconfiguration merely left a stray
`current/`+`incoming/` pair — so the previous fix wave made this failure mode worse.

It takes a hand-edited `merge.config` to reach (F9), which is why it was parked rather than
fixed. That makes it low-likelihood, not low-consequence.

Keep `IsWithin`'s equality case if some other caller depends on it; the fix belongs at the
`ConflictDir` check, not necessarily in the helper. Establish which during planning — the
helper currently has one call site, but that is a fact to verify, not assume.

### 2. The legacy branch moves inside the try

Move the `try` above `Program.cs:13` so the four-argument path is covered.

`GitConfigRepair.TryGit`'s catch filter covers only `Win32Exception` and
`InvalidOperationException`, so an `IOException` from reading git's stdout still escapes as a
raw .NET stack trace into git's merge output — which is exactly what the top-level handler was
added to prevent, on the one path that now makes two git invocations instead of one.

### 3. Stop phrasing success as failure

A workspace that registered only `nmp2` currently gets told that `meta` "could not be read".
Per F4 the appended detail cannot disambiguate a missing key from a failed read, so the fix is
in the *phrasing*, not in better detection: report only on the key that was actually invoked,
or state both outcomes without the error framing.

### 4. Decide the write-only `"conflicted"` flag

F5 says it has a writer and no reader. It self-heals — `WriteSidecarCore` reserialises
`ArtifactEntry` and drops the unknown property — so it cannot corrupt anything. What it does do
is produce a spurious committed diff on every conflicted merge that nothing can act on.

**Recommendation: delete the write and its assertions.** A flag nobody reads is not a feature
waiting for a reader; it is a diff generator. If a reader is wanted later, the write is four
lines to restore, and by then whoever adds it will know what the reader needs.

This is a judgement call, not a defect fix. Flagging it as the item most likely to be
overturned on review.

### 5. Round-trip test for `merge.config`

The contract is declared twice — the CLI's writer and `Scrinia.Merge`'s `MergeConfig` reader —
in two assemblies, with two independent camelCase policies and nothing pinning them together.
That exact divergence *was* the camelCase bug found and fixed during the original branch, and
that branch's final reviewer called this the highest-value follow-up in the list.

Write a test that goes through the **real writer** and the **real `MergeConfig.Load`**. A test
that constructs the JSON by hand pins nothing — it would have passed under the original bug.

The two types live in different assemblies, so establish during planning where the test can
see both.

### 6. `LocateDriver` picks by configuration, not mtime

F6: a stale Release build can be exercised against a Debug test run, and the failure would look
like a driver bug rather than a harness bug. Select by the build configuration the test
assembly was built in.

### 7. `ScanForConflicts` conflict-copy coverage

F7's two calls pass `"current"` and `"incoming"`. Nothing tests them, so a swapped pair ships
undetected and produces a conflict report with the two sides transposed — which reads as
plausible output, not as an error.

The test must distinguish the sides by content, not merely assert both are non-empty.

### 8. Upgrade doc tells the truth

Per F8 the doc's claim is true for new workspaces and false for exactly the pre-release ones
the adjacent upgrade section addresses. A workspace that re-runs `scri setup --multi-user` is
told it is fixed, and still resolves from stale committed bytes, because `reconcile()` prefers
the conflict copy unconditionally with no freshness check.

Add "delete and untrack any committed `conflict/` directories" to the upgrade note, and state
the freshness caveat. Zero risk: a merge driver correctly must not `git rm` unasked, but the
doc can say so.

## Out of scope

- Giving `reconcile()` a freshness check on conflict copies. Real, but it is a behaviour change
  to the resolution path, not a residual — it needs its own design.
- `reconcile()` leaving the sidecar stale after resolving (`originalBytes`, `chunkCount`,
  `contentPreview`, `keywords` keep pre-merge values, no reindex). Recorded in the triage as
  **unverified**; verify it before deciding whether it belongs here or in its own spec.

## Testing

- A `merge.config` with `ConflictDir` of `"."` and of `""` → rejected, and **no `.gitignore`
  written**. Assert the absence, not just the rejection; the `.gitignore` write is the harm.
- The four-argument invocation with git's stdout made to fail → a diagnostic message, not a
  stack trace.
- A workspace with only `nmp2` registered → no error framing on the success path.
- `merge.config` round trip through the real writer and reader (§5).
- Conflict scan with distinguishable current/incoming content (§7).

## Risks

Low throughout — `Scrinia.Merge` is a standalone AOT/trimmed executable with no `Scrinia.Core`
dependency, and this spec deliberately keeps it that way. The containment fix in §1 is the only
one that changes behaviour for an existing (if misconfigured) workspace, and it changes it from
"silently destroys tracking" to "refuses with a message".
