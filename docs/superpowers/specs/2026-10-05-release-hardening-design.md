# Release Hardening Design

**Date:** 2026-10-05  
**Status:** Approved for planning  
**Scope:** Release contracts, test isolation, and analyzer hygiene for the 0.6 release line.

## Intent and success criteria

Make the release candidate reproducible and truthful about what it ships. A clean checkout must
pass the active CI test lanes; documentation must describe the embeddings executable as a
RID-specific asset bundled in the host release layout; and a forced Release compilation must be
warning-free after intentionally test-shaped diagnostics are scoped out.

This work does not change the public `IMemoryStore` boundary. That refactor is explicitly deferred
to a future breaking-change window.

## Release contract repairs

### CI topology

Remove the retired `Scrinia.Merge.Tests` invocation from `ci.yml`. Keep the current Core,
embeddings, and retrieval test lanes, which correspond to projects in `Scrinia.sln`.

Add or update a regression assertion that validates active workflow test targets against the
current project topology, so a deleted project cannot remain a required CI lane.

### Bundled embeddings delivery

The supported distribution is the executable and native payload nested in the host release archive
at `plugins/scri-plugin-embeddings/<version>/<rid>/`. It is not a standalone NuGet global tool.

Remove installation commands and prose that promise `dotnet tool install ...
scri-plugin-embeddings`. Update current product documentation, package-readme text, and tests to
describe the bundled asset and its discovery path. Historical planning/specification artifacts are
not rewritten.

The plugin CLI project remains publishable as the release-build input. It does not gain global-tool
packaging metadata, and the release pipeline does not publish a plugin NuGet tool package.

### Process-wide test environment

All embeddings tests that read or mutate `SCRINIA_HOME` join one named, non-parallel xUnit
collection. The existing `ProcessEnvironmentLock` remains the cross-process safeguard. The
concurrent cold-start test continues to make concurrent calls inside the plugin; only unrelated
tests are prevented from tearing down the process-wide home directory mid-test.

## Analyzer policy

Production projects retain the existing warnings-as-errors policy.

Test and benchmark source gets a directory-scoped analyzer configuration that suppresses only
intentional test conventions:

- `CA1707` for scenario-style test names with underscores;
- `xUnit1030` and `xUnit1031` where the established test harness intentionally uses those patterns.

All other diagnostics remain enabled and are fixed, including nullability, disposal, cancellation,
platform, ignored-result, and low-cost performance diagnostics. A suppression outside the three
named rules requires a local explanatory justification and is used only after confirming the
diagnostic is not meaningful for the fixture.

## Verification

Run a forced, non-incremental Release build; the active CI test projects; package creation for the
host; and both host/plugin publish paths. Re-run the final gate from a fresh clean checkout.

Do not publish packages, reset the worktree, remove user-owned files, or treat existing local
`.scrinia` data and retired empty directories as release artifacts during this work.

## Acceptance criteria

1. CI names and runs only extant test projects.
2. No current user-facing documentation says the embeddings plugin is installed as a standalone
   .NET tool.
3. A solution test run does not race `SCRINIA_HOME` setup/teardown between embeddings test classes.
4. A forced Release build produces no warnings: production remains strict, test conventions are
   narrowly scoped, and actionable diagnostics are resolved.
5. Host package and release publish layouts continue to succeed, including the bundled plugin
   directory.
