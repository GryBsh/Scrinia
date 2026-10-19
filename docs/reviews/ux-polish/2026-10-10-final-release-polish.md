# UX Polish Review — Final release copy

**Date:** 2026-10-10
**Scope:** Uncommitted public-copy changes against `b9289fe4340e411b6376f6a6d30cef3f005bac94` in `docs/getting-started.md`, `docs/architecture/cli.md`, `docs/installation.md`, and release-body construction in `scripts/release/Publish-ValidatedRelease.ps1`.
**Reviewer:** ux-polish-reviewer agent

The corrected copy is clear, consistent, and supported by the packaging and publication code.

## Must fix — 0

None.

## Should fix — 0

None.

## Consider — 0

None. No before/after rewrites are needed.

## Consistency notes

- Keep `scri`, `libSystem.Native.so` (Linux), and `libSystem.Native.dylib` (macOS) as literal filenames. The installation tree makes the platform mapping explicit.
- “Self-contained archive” and “managed NuGet tool” consistently distinguish the two delivery formats. The build-resolved companion is retained beside the host in plain and embeddings output; the NuGet tool uses the installed runtime.
- “Filesystem classification” accurately describes the Unix `SystemNative_LStat` dependency. The host companion and plugin native libraries remain distinct in the installation guidance.
- The release body uses a fenced `text` block, preserving one visible row per identity field. The literal field names remain stable, and `Read-Release` still compares the entire body with case-sensitive exact equality.

Verification was limited to relevant source and configuration. No builds, tests, or live publication were performed.
