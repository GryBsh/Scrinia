# UX Polish Review — Shared memory and global tool architecture

**Date:** 2026-09-01
**Scope:** `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md`; `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md`
**Reviewer:** ux-polish-reviewer agent

State: Needs polish — 1 Must fix, 5 Should fix, 2 Consider.

## Must fix (1)

1. **The “normalized” historical excerpt still contradicts the active mutation contract**  
   `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md:600-617`

   **Before:**  
   > MutationResult: base,path/destination conditional,mutation applied|none|unknown,generation nullable/in success required.

   **After:**  
   > MutationResult: base, conditional path/destination fields, mutation `applied|none`, a required nonnull generation on success, and a manifest of authoritative paths. Mutation errors have no result DTO; only the top-level envelope may report mutation `unknown`.

   This aligns the excerpt with the active contract while preserving its historical purpose.

## Should fix (5)

1. **Use prospective language for unimplemented requirements**  
   `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md:21,741-742,872,1213-1215`

   **Before:**  
   > The implementation delivers:  
   > Tests prove detectable pre-write drift...  
   > The release adds only these command forms:

   **After:**  
   > The implementation must deliver:  
   > Acceptance tests must cover detectable pre-write drift...  
   > The release must add only these command forms:

   The current wording sometimes presents proposed behavior and tests as already implemented or verified.

2. **Qualify the self-review’s “passed” claims while external review is pending**  
   `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md:1550-1593`

   **Before:**  
   > **Placeholders:** passed. No unresolved marker, branch, deferred decision, or empty-object payload remains.

   **After:**  
   > **Placeholders — author check complete; external verification pending:** No known unresolved marker, branch, deferred decision, or empty-object payload remains.

   Apply the same framing throughout §17. This avoids self-certification conflicting with the document’s pending-review status.

3. **Align titles and revision dates across the paired documents**  
   `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md:1-5`  
   `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md:1-5`

   **Before:**  
   > # Shared Memory Global Tool Specification — Review Resolution  
   > **Date:** 2026-08-31

   **After:**  
   > # Shared Memory and Global Tool Architecture — Review Resolution  
   > **Created:** 2026-08-31  
   > **Last revised:** 2026-09-01

   The review includes substantive rounds dated September 1, and its title should mirror the specification it governs.

4. **Replace the awkward pseudo-enum term “non-available”**  
   `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md:667,673,1443-1444,1526`

   **Before:**  
   > partial whenever at least one summary is non-available

   **After:**  
   > `partial` whenever at least one summary is not `available`

   Where useful, the more explicit “is `unavailable`, `stale`, or `conflicted`” is even clearer.

5. **Avoid claiming that key construction conclusively prevents collisions**  
   `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md:220`

   **Before:**  
   > These keys prevent model, plugin-version, RID, workspace, and shared-base collisions.

   **After:**  
   > Keying assets by these identities keeps model, plugin-version, RID, workspace, and shared-base data in distinct namespaces.

   This states the designed mechanism without making an absolute claim stronger than the documented verification.

## Consider (2)

1. **Simplify the dense scope line**  
   `docs/superpowers/specs/2026-08-31-shared-memory-global-tool-design.md:5`

   **Before:**  
   > **Scope:** Global-tool packaging, local/shared memory separation, tracking migration, and merge-subsystem retirement

   **After:**  
   > **Scope:** Packaging the global tool; separating local and shared memory; migrating tracking policy; and retiring the merge subsystem

2. **Replace vague dialogue references with dated references**  
   `docs/superpowers/reviews/2026-08-31-shared-memory-global-tool-spec-review.md:32-35`

   **Before:**  
   > The closing dialogue retained the design's existing global-tool...

   **After:**  
   > The 2026-08-31 design dialogue retained the proposed global-tool...

   “Closing dialogue” has no stable referent for readers encountering this record independently.

## Consistency notes

- Approval provenance is handled carefully and consistently; the documents do not invent reviewer approval.
- Technical identifiers, command names, enum values, and error codes are generally formatted consistently.
- The repeated uses of “exact” are mostly justified where byte-level, schema, ordering, or argv behavior is normative.
- Keep `Ephemeral` capitalized only when referring to the parser classification; use lowercase “ephemeral” for ordinary prose.
- Keep status and enum values in code formatting: `Proposed`, `available`, `partial`, `applied`, and `none`.

## Disposition

All 8 recommendations accepted by the user and applied on 2026-09-01; external architecture re-review pending.
