# UX Polish Review — GitHub release title and body

**Date:** 2026-10-10
**Scope:** Newly public release name and body in `scripts/release/Publish-ValidatedRelease.ps1:204,220`; surrounding publication checks read only to verify claims. Developer diagnostics, tests, and other copy excluded.
**Reviewer:** ux-polish-reviewer agent

The title is clear and the validation claim is supported; the body needs one small formatting improvement for readability. **0 Must fix, 1 Should fix, 0 Consider.**

## Must fix

None.

## Should fix

### Preserve metadata rows in the rendered release body

**Location:** `scripts/release/Publish-ValidatedRelease.ps1:204`.

The body uses single newlines without Markdown structure. These do not reliably preserve distinct rows in rendered Markdown, leaving three long identity values in a dense paragraph. Keep the existing field names and values, but format them as a code block.

**Before** (expanded template; placeholders represent script variables):

```text
Scrinia $Version validated release
commit: $Revision
validationRunId: $ValidationRunId
manifestSha256: $manifestHash
```

**After** (proposed Markdown body):

````markdown
Scrinia $Version validated release

```text
commit: $Revision
validationRunId: $ValidationRunId
manifestSha256: $manifestHash
```
````

**Contract constraint:** `Read-Release` compares the complete body to `$metadata` using case-sensitive equality at line 101. This suggestion changes that identity string. Apply only through a coordinated template change before the first draft is created, with the expected body updated wherever the retry contract is asserted. If a draft already exists, retain its exact body until an explicit compatibility decision is made. Do not manually polish a live release body. No publication was performed as part of this review, and no source copy was changed.

## Consider

None. The release name `Scrinia $Version` is concise; keep it.

## Factual claim checks

- **“Validated release”: supported by the inspected control flow.** Local artifact validation occurs at line 163. Lines 176–199 require the selected successful run and successful build, three native, and aggregate jobs. Lines 201–203 bind the tag to the revision; line 207 verifies the selected run artifact. Publication requires seven verified remote assets and a verified NuGet package at lines 245–247, and each publication write rechecks the current validation attempt. This is a source review, not evidence that a real candidate has passed or been published.
- **Identity fields: supported.** The revision and run ID are checked against the selected run; `manifestSha256` is calculated from the local manifest at line 169. The proposed formatting preserves their names and values and adds no feature or compatibility claims.

## Consistency notes

- Retain the concise product-and-version release title and calm, factual wording.
- Treat the metadata labels as stable contract fields; avoid editorial renaming or case changes.
- Use a blank line before a fenced metadata block to separate the sentence from the identity values.
- Preserve exact body bytes across creation and retries once a candidate has a draft.
