# `facts.json` verification churn — remediation design

**Date:** 2026-08-22  
**Status:** approved  
**Scope:** verification workflow and tracked derived artifacts only

## Context and root cause

`facts.json` is a derived cache, not a source-of-truth memory artifact. Its contents are
reconstructed from the stored memories and their attributes. The cache deliberately carries a
derivation version, and derivation version **3** intentionally rejects an older cache so that the
next read rebuilds it under the current derivation rules.

That rebuild is expected behavior. It can nevertheless appear as repository churn when a
verification command uses the repository workspace as its active Scrinia store: reading facts can
rewrite the tracked `facts.json`, even though no source or test behavior changed. Treating that
rewrite as an implementation change obscures the actual verification result and risks committing
a machine-generated cache update.

The approved remediation is procedural. Keep the tracked cache artifact at its reviewed baseline,
and keep final verification away from the repository workspace so cache generation cannot dirty
the checkout.

## Scope and non-goals

### In scope

- Recognizing `facts.json` as a rebuildable, derived cache.
- Preserving the tracked cache artifact after expected derivation-version churn.
- Running final verification against an isolated workspace or store.
- Recording the distinction between expected cache churn and a real regression.

### Non-goals

- No production-code changes.
- No isolated-test changes.
- No change to the derivation version or cache rebuild policy.
- No attempt to make `facts.json` a source-of-truth artifact.
- No replacement of the tracked cache with a newly generated local copy.
- No test execution as part of this documentation task.

## Approved procedure

1. **Identify the cache before verification.** Treat any change to a tracked
   `.scrinia/**/facts.json` as potentially expected derivation churn. Do not stage it, fold it into
   an unrelated change, or use it as evidence of a production-code regression without first
   checking the active workspace.

2. **Use an isolated verification store.** Configure final verification to read and write an
   isolated temporary workspace or store outside the repository checkout. The repository
   workspace must not be the destination for facts generation during final verification.

3. **Complete verification without repository-workspace facts generation.** Keep all generated
   facts caches, indexes, and other derived store output in the isolated location. Verification
   may inspect repository files, but it must not initialize or use the repository’s Scrinia store
   as its writable store.

4. **Restore expected churn if it already occurred.** If an earlier verification pass rewrote the
   tracked cache, restore the tracked `facts.json` to the pre-verification reviewed version. Restore
   only that derived artifact; preserve source, documentation, test, and unrelated working-tree
   changes. Do not replace it with the newly rebuilt cache.

5. **Perform the final change review.** Confirm that the remaining change set contains the intended
   documentation artifact and no cache-only diff. A cache diff caused solely by the version-3
   rebuild is resolved by restoration, not by changing production code or tests.

## Acceptance criteria

- `facts.json` is explicitly documented and handled as a derived cache rather than source data.
- The design states that derivation version 3 intentionally causes an older cache to rebuild.
- Final verification is specified to avoid facts generation in the repository workspace.
- Any tracked cache artifact changed by expected verification churn is restored to its reviewed
  baseline after verification.
- Restoration preserves unrelated working-tree changes and does not substitute a generated cache.
- The remediation requires no production-code changes and no isolated-test changes.
- The final documented change set contains no unexplained cache-only modification.

## Self-review

- **Placeholders:** none remain; the procedure names the artifact, the version behavior, the
  repository-workspace constraint, and the restoration action directly.
- **Contradictions:** none found. The document distinguishes an allowed rebuild from the required
  post-verification restoration and does not ask for code or test changes.
- **Ambiguity:** “isolated” is defined as outside the repository checkout and writable only for the
  verification run; “restore” is limited to the tracked derived artifact and explicitly preserves
  unrelated changes.
