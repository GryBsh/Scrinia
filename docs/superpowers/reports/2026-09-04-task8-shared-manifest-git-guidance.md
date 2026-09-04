# Task 8 Report — Authoritative Mutation Manifests and Git Guidance

## Implemented

- `SharedManifest.Create(IEnumerable<string>)` (Core) is the single deterministic manifest authority. It makes each entry repository-root-relative (`.`scrinia/`-prefixed), `/`-separated, sorted by UTF-8 ordinal bytes, and deduplicated, retaining only exact authoritative files. Directories, wildcards, and every path the §4.1 durable classifier marks as derived are dropped, so no control, derived, or directory pathspec can enter staging.
- The runtime commit (`SharedBaseRuntime.CommitMutation`) now normalizes `plan.AuthoritativeFiles` through `SharedManifest.Create` in both the success and the unknown-write-failure paths, so `MutationResult.Manifest` is always the canonical form.
- `SharedGitGuidance.Render` (Mcp) + `SharedServer.GuidanceForMutation` produce the operation-based guidance: exactly one `git -C "<root>" add -A -- '<path>' ...` command with every manifest path individually shell-quoted, followed by the fixed `commit -m "docs(memory): update shared Scrinia memory"` and `push`. It returns `null` for an empty manifest, so a no-op emits no staging command. Root is double-quoted; each path is POSIX single-quoted with `'"'"'` embedded-quote escaping. Scrinia never claims Git state.

## Tests Added

- `tests/Scrinia.Tests/Shared/SharedManifestTests.cs` — `Manifest_contains_only_exact_authoritative_files_for_each_mutation`, `Never_contains_directory_wildcard_derived_or_pretracked_derived_paths`, dedup/UTF-8 sort, backslash normalization, already-root-relative idempotence, empty-input no-op, and a runtime no-op commit with an empty manifest.
- `tests/Scrinia.Tests/Shared/SharedGitGuidanceTests.cs` — `Git_guidance_is_operation_based_and_shell_quotes_each_manifest_path`, empty-manifest no-command, individually quoted arguments with no store-directory pathspec, no Git-state verb, and an embedded-quote POSIX round-trip.

## Verification

- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~SharedManifestTests|SharedGitGuidanceTests|Commit_normalizes... --no-restore`: 13 passed.
- `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --filter FullyQualifiedName~Shared --no-restore`: 162 passed.
- Full suite `dotnet test tests/Scrinia.Tests/Scrinia.Tests.csproj --no-restore`: 2511 passed, 20 skipped (benchmark), 0 failed.
- `dotnet build Scrinia.sln --no-restore`: build succeeded, 0 errors (only pre-existing CA1707 test-naming warnings).
- `git diff --check`: clean.

## Follow-up / Concerns

- Per-operation file selection (remember/append body+sidecar, forget deleted files, link both endpoints, compact versions-archive handling, skill/agent body+sidecar) is the handler contract for later tasks; Task 8 supplies only the shared `SharedManifest.Create` normalization each handler consumes.
- The `Utf8ByteArrayComparer` uses an explicit byte loop rather than `Span.SequenceCompare` to avoid a framework-version dependency; behavior is identical UTF-8 ordinal ordering.
