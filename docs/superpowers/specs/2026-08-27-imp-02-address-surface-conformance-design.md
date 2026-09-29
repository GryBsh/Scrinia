# IMP-02 — Address-surface conformance tests design

**Date:** 2026-08-27  
**Status:** Approved design  
**Release relationship:** 0.6.0 test-only improvement

## Goal

Add mechanical coverage that inventories every supported address-taking surface and pins its current
canonicalization, refusal, and ephemeral policy without replacing the authoritative behavior.

## Scope

- Cover all public MCP memory tools that accept or resolve addresses.
- Cover CLI address-taking commands and their direct internal-handler route.
- Cover MCP resource address resolution.
- Record intentional policy differences as explicit test data rather than forcing one helper into
  every surface.

## Non-scope

- No canonicalization algorithm change.
- No new address format, public API, storage layout, path-length policy, or remote/HTTP surface.
- No broad facade rewrite and no replacement of the existing address behavior.

## Architecture and data flow

The tests form a declarative inventory over the current owners:

- MCP boundaries in `src/Scrinia.Mcp/MemoryTools.cs` and `ScriniaMcpResources.cs`.
- CLI boundary handling in `src/Scrinia/Commands/ScriniaCommands.cs` and its command partials.
- Store resolution through the existing `IMemoryStore`/`FileMemoryStore` naming contract.

Each inventory row identifies the operation, input shape, expected canonical name or refusal,
whether `~name` is allowed, and the common internal route. The test invokes the public boundary and,
where the CLI bypasses the facade, invokes the direct CLI handler as a separate row. It then asserts
the observable result and the absence of unintended filesystem mutation.

## Error and risk handling

- A newly added address-taking surface must fail the inventory test until its intentional policy is
  recorded.
- An expected policy exception must be named with its owner and reason; a generic exclusion is not
  accepted.
- Tests must distinguish invalid input, missing memory, and valid ephemeral addressing rather than
  collapsing them into one failure class.
- Existing long-name and sanitized-name behavior remains authoritative; the tests do not infer a new
  limit or apply the 182-character write guard from the earlier public-surface design unless the
  implementation has already adopted it.

## Tests and acceptance criteria

- The inventory enumerates all current MCP tools, CLI address-taking commands, and MCP resources
  identified at `MemoryTools.cs:103-115,207,293,421,462,490,524-525`,
  `ScriniaCommands.cs:34-59`, and `ScriniaMcpResources.cs:26-41`.
- Every row asserts canonicalization or refusal, ephemeral handling, and expected route ownership.
- MCP and CLI tests agree where they intentionally share a handler and explicitly differ where policy
  is local to a boundary.
- Valid persistent addresses resolve to the same canonical store address across MCP and CLI.
- Valid `~name` addresses retain their existing ephemeral behavior.
- Invalid or refused addresses produce their existing public error envelope/exit behavior.
- A test fails when a production address-taking surface is added without an inventory entry.
- No test claims a helper is universal merely because it is shared by current implementations.

## Affected files

- `src/Scrinia.Mcp/MemoryTools.cs` — inventory reference only; no behavior change.
- `src/Scrinia.Mcp/ScriniaMcpResources.cs` — inventory reference only; no behavior change.
- `src/Scrinia/Commands/ScriniaCommands.cs` and command partials — inventory reference only.
- `tests/Scrinia.Tests/` — focused address-surface conformance tests, extending existing surface
  conformance coverage where present.

## Dependencies

- Existing address parsing, canonicalization, refusal, and ephemeral rules are authoritative.
- IMP-03 is independent of this address-conformance stream. IMP-01 and IMP-04 do not depend on
  address-conformance work; the umbrella milestone sequence still requires IMP-04 before IMP-01.

## Explicit no-placeholder content

This design names the complete current surface inventory and requires explicit treatment of every
intentional exception. It does not leave a future address-taking class or policy undefined.
