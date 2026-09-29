# IMP-03 — Publish workflow and manifest parity design

**Date:** 2026-08-27  
**Status:** Approved design  
**Release relationship:** 0.6.0 release-readiness improvement

## Goal

Make `publish.projects.tsv` the sole source of truth for publishable projects, prove parity across
both scripts and the release workflow, and verify archive contents across supported publish targets
without changing public APIs or storage behavior.

## Scope

- Preserve the six-column manifest and current rows as the canonical project inventory.
- Make the workflow consume the manifest or mechanically validate its project selection against it.
- Keep PowerShell and Bash parsing, conditions, profiles, output kinds, and RID scopes equivalent.
- Verify host, Linux, and macOS archive layouts through the supported release process, subject to
  NativeAOT host restrictions.
- Verify the nested embeddings plugin layout and Vulkan native replacement behavior exactly as local
  scripts define them.

## Non-scope

- No project additions or removals.
- No change to package identifiers, plugin protocol, runtime support, storage, or public APIs.
- No claim of native execution on a platform that was not executed.
- No replacement of `publish.projects.tsv` with a second generated list.

## Architecture and data flow

The manifest remains:

| id | project | condition | profile | output | rids |
|---|---|---|---|---|---|
| `scri` | `src/Scrinia/Scrinia.csproj` | `always` | `trimmed` | `rid` | `all` |
| `scri-merge` | `src/Scrinia.Merge/Scrinia.Merge.csproj` | `always` | `plain` | `rid` | `host` |
| `scri-plugin-embeddings` | `src/Scrinia.Plugin.Embeddings.Cli/Scrinia.Plugin.Embeddings.Cli.csproj` | `with-embeddings` | `plain` | `plugin` | `all` |

Both `publish.ps1` and `publish.sh` read the same header and rows. The workflow invokes the scripts
or validates that every publish job selects exactly the manifest project set. For each RID, the
normal CLI output is rooted at the RID directory; the optional plugin is nested at
`{rid}/plugins/scri-plugin-embeddings/` with its executable and model directory expected by local
plugin discovery. The Vulkan replacement step must use the local scripts' backend package/version,
RID-specific native directory, and CPU-backend overwrite semantics.

`scri-merge` remains host-restricted because it is NativeAOT. Archive verification checks presence,
names, nesting, executable placement, and expected omissions; it does not turn an unavailable
foreign native execution into a pass.

## Error and risk handling

- Header, column count, enum values, duplicate IDs, missing projects, and workflow drift fail before
  artifacts are accepted.
- A workflow project not represented by the manifest, or a manifest project not represented by the
  workflow/scripts, is a parity failure.
- Missing Vulkan natives produce the existing explicit warning and are recorded as CPU fallback, not
  as successful Vulkan packaging.
- Missing or misplaced nested plugin files fail archive verification.
- Foreign-RID `scri-merge` publication is reported as host-restricted, not falsely marked failed or
  natively verified.

## Tests and acceptance criteria

- `PublishManifestTests` parses the manifest and verifies all three rows, the exact header, and the
  corresponding script/workflow project selection.
- PowerShell and Bash accept/reject the same malformed headers, row widths, enum casing, and empty
  manifest cases.
- Every supported RID produces the expected `scri` archive shape; host publish produces
  `scri-merge`; `-WithEmbeddings`/`--with-embeddings` adds the nested plugin only when requested.
- Embeddings archives place the plugin executable under the nested plugin directory and preserve the
  model-directory/discovery contract.
- Windows/Linux Vulkan native replacement is verified from the exact local script paths; macOS is
  verified as the documented CPU fallback where no Vulkan package exists.
- At least one host publish and one non-host archive inspection are captured with command, RID,
  output path, and limitation. No unsupported native runtime claim is made.
- The workflow fails before release artifacts are accepted when manifest parity is broken.

## Affected files

- `publish.projects.tsv` — canonical inventory; no row change is implied.
- `publish.ps1` and `publish.sh` — parity/consumption changes only.
- `.github/workflows/release.yml` — consume or validate the manifest.
- `tests/Scrinia.Tests/` (including `PublishManifestTests.cs`) — parity and archive-shape tests.
- `src/Scrinia.Plugin.Embeddings/` and `src/Scrinia.Plugin.Embeddings.Cli/` — inspected for the
  existing plugin contract; no implementation change is implied by this design.

## Dependencies

- `publish.projects.tsv` remains authoritative.
- Local plugin discovery and Vulkan replacement scripts are authoritative for layout.
- Versioning is handled by the 0.6.0 release documentation/versioning spec, not this stream.

## Explicit no-placeholder content

The manifest rows, output paths, plugin nesting, host restriction, and Vulkan fallback outcomes are
fully specified. Cross-platform verification is bounded by explicit execution evidence and never by
an unsupported runtime assertion.
