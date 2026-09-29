# 0.6.0 versioning, changelog, and living-documentation corrections

**Date:** 2026-08-27  
**Status:** Approved design  
**Release relationship:** Required release documentation and metadata stream

## Goal

Prepare the repository for version **exactly `0.6.0`**, correct current living documentation, and
describe the release without rewriting historical records or claiming behavior the implementation
does not provide.

## Scope

- Set the single solution-wide version source to `0.6.0`.
- Add the 0.6.0 changelog entry with the approved improvements and release-readiness corrections.
- Correct living documentation for the current local CLI/stdio MCP product and plugin behavior.
- Make plugin documentation match the current implementation: standalone executable, local MCP stdio,
  nested embeddings layout, model directory, and Vulkan replacement behavior.
- Correct the identified troubleshooting/bundle terminology drift without broad editorial rewrites.

## Non-scope

- Do not rewrite historical changelog entries, dated `docs/superpowers/` artifacts, or prior reports.
- Do not change source, tests, publish scripts, manifest, plugin protocol, public APIs, or storage.
- Do not add HTTP, REST, remote CLI, web UI, `--remote`, `--api-key`, or `--store` guidance.

## Architecture and data flow

`Directory.Build.props` remains the sole version authority. Its `Version` value is `0.6.0`; project
files do not redeclare it. The version flows to assembly informational metadata, CLI version output,
and MCP server information through existing build behavior.

The changelog records 0.6.0 as the current release narrative, including entity-fact ownership and
adjacency improvements, address-surface conformance coverage, publish-manifest/archive verification,
and documentation corrections. It distinguishes preserved behavior from intentional changes and
does not copy unresolved historical findings into the new entry.

Living docs are corrected only at current-reference locations: `README.md`, `docs/getting-started.md`,
`docs/cli-reference.md`, `docs/plugin-authoring.md`, `docs/troubleshooting.md`, and the relevant
architecture pages. Plugin examples use the actual executable and discovery layout. The optional
embeddings archive is documented under `plugins/scri-plugin-embeddings/`, and Vulkan native
replacement is described as the local publish-script step with explicit CPU fallback where applicable.

## Error and risk handling

- A version literal other than `0.6.0` in canonical metadata or current release-facing copy is a
  release correction failure; historical version literals remain valid in historical entries.
- A documentation statement is removed or rewritten when it describes a removed surface, stale path,
  or unsupported runtime claim; it is not replaced with a speculative feature.
- Plugin documentation is checked against `McpPluginHost`, plugin CLI argument handling, discovery,
  model paths, and publish-script output before acceptance.
- Any ambiguity between current and historical documentation is resolved by keeping dated history
  unchanged and correcting only living reference docs.

## Tests and acceptance criteria

- `Directory.Build.props` contains exactly `0.6.0` as the canonical version and no project-level
  version override contradicts it.
- A version/help probe reports `0.6.0` from the built CLI and MCP server metadata is consistent with
  the same version.
- The 0.6.0 changelog entry names the shipped scope, preserved APIs/storage behavior, and known
  platform limitations without claiming unrun checks.
- Living docs contain no removed remote/HTTP/REST surface or stale plugin path.
- Plugin docs match executable-first publication, local stdio MCP, nested plugin discovery, model
  directory, and Vulkan replacement/fallback behavior.
- Historical changelog and dated planning/spec/report files are byte-for-byte untouched.
- Links, commands, headings, and code examples in every changed living doc resolve to current paths
  and supported commands.

## Affected files

- `Directory.Build.props` — version metadata.
- `CHANGELOG.md` — new 0.6.0 entry only; history remains unchanged.
- `README.md`, `docs/getting-started.md`, `docs/cli-reference.md`, `docs/plugin-authoring.md`,
  `docs/troubleshooting.md` — living corrections.
- `docs/architecture/overview.md`, `docs/architecture/cli.md`, `docs/architecture/core.md`,
  `docs/architecture/embeddings.md` — only where current implementation claims are stale.
- `src/Scrinia/Program.cs`, `src/Scrinia.Mcp/MemoryTools*.cs`, and plugin projects — verification
  references only; no implementation change.

## Dependencies

- IMP-01 through IMP-04 define the improvement language used by the release narrative.
- IMP-03 defines publish/archive terminology and the authoritative manifest.
- The current implementation and local scripts remain authoritative over all plugin claims.

## Explicit no-placeholder content

The release version is exactly `0.6.0`; historical records remain unchanged; living-doc targets and
plugin layout are explicitly named. No future feature or unresolved editorial decision is implied.
