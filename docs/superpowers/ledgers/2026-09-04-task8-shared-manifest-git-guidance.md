# Task 8 Ledger

| Decision | Evidence |
| --- | --- |
| One deterministic manifest factory owned by Core | `SharedManifest.Create` is the sole authority; the runtime commit normalizes `plan.AuthoritativeFiles` through it, so ordering, separators, duplicates, and derived paths never leak into staging. |
| Manifest is repository-root-relative and classifier-filtered | `Create` strips any `.scrinia/` prefix, classifies the store-relative form against `SharedClassifier.IsExcludedFromFingerprint`, and re-roots surviving authoritative files; the `Never_contains_...derived` test proves `embeddings/`, `versions/`, `*.tmp`, `*.lock`, and `*` wildcards are all dropped. |
| UTF-8 ordinal sort, not string ordinal | `Utf8ByteArrayComparer` compares raw UTF-8 bytes via a manual lexicographic loop, so a non-ASCII path sorts by byte value as §7.2 requires rather than by UTF-16 code unit. |
| Guidance is operation-based and print-only | `SharedServer.GuidanceForMutation` returns `null` for an empty manifest (no-op emits no staging command) and a fixed three-line `add -A` / `commit` / `push` block otherwise; `SharedGitGuidanceTests` asserts each path is individually quoted and no state verb appears. |
| Shell quoting matches §7.2 shape | Root is double-quoted and every manifest path is POSIX single-quoted with `'"'"'` embedded-quote escaping, verified by the round-trip embedded-quote test. |
| Per-operation file selection stays with later handlers | remember/append, forget, link, compact (versions archive included only if the classifier later marks it authoritative), and skill/agent each pass their exact changed authoritative files to `Create`; Task 8 supplies the normalization, not the handlers. |
| No Git invocation or state claim | guidance prints exact commands only; the runtime never launches `git` and never asserts tracked/committed/clean. |
