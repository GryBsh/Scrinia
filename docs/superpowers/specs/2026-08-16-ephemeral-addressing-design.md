# Ephemeral addressing — design

**Date:** 2026-08-16
**Branch 1 of 7** from the 2026-08-16 whole-repository design review (`docs/reviews/2026-08-16-design-review.html`).
**Closes:** review findings C1 and H2.

---

## Problem

Two defects meet at one command, and they are not the same defect.

**H2 — three doors disagree about what `/temp/x` means.** The entire `/temp/` → `~` routing rule is two verbatim string literals in one file, `MemoryTools.Core.cs:140-141` (Remember) and `:1039-1040` (Append). It exists nowhere else. Meanwhile `IsEphemeral` — the de-facto routing predicate, with ~20 call sites — tests only `name[0] == '~'`. Runtime probe, one workspace, one string:

```
remember /temp/probe   success   Remembered: ~probe [ephemeral]
recall   /temp/probe   error     "Memory '/temp/probe' not found."
forget   /temp/probe   error     "No artifact found with name '/temp/probe'."
link     /temp/probe   error     actionNeeded: remember({path:'/temp/probe'}) ...
compact  /temp/probe   error     NOT_FOUND
```

`link`'s recovery hint advises the call that created the unreachable memory — the advice is a loop. The published `forget` tool description gives `forget({ path: '/temp/scratch' })` as its example; that call returns NOT_FOUND. Over raw HTTP the same string writes *permanent* bytes to `.scrinia/memories/temp/`. So `/temp/x` is three different memories depending on the door.

The tests do not disconfirm this — they encode it. Every test writes at `/temp/x` and reads back at `~x` (`RemoteStoreToolTests.cs:496-497`, `:512-517`, `:612`; `ScriniaMcpToolsTests.cs:1687-1693`). A comment at `ScriniaMcpToolsTests.cs:1673` documents this exact bug class being diagnosed and fixed *once, locally* — which is how the second literal came to exist.

**C1 — the CLI reports success while discarding the content.** `scri store /temp/x` builds `new ScriniaMcpTools()`, calls `Remember`, which routes to `RememberEphemeral` (`FileMemoryStore.cs:303`) and lands in `_ephemeralStore` — a plain in-memory `ConcurrentDictionary` (`FileMemoryStore.cs:26`). The CLI prints `Remembered: ~x [ephemeral]` and exits. The map dies with the process. Exit code 0.

(The address is a positional `[Argument]` — `scri store <name> [file]`, per `docs/cli-reference.md:73`. Earlier drafts of this finding wrote it as `--name`, which would fail CLI parsing rather than reproduce the defect.)

**These are independent.** Fixing the aliasing does not fix C1: `scri store "~x"` still writes to an in-memory map in a process that is about to exit, whatever the spelling. C1 is caused by an ephemeral write occurring in a short-lived process, not by how the address was written.

### Prior constraint

`docs/superpowers/plans/2026-08-07-store-boundaries.md:119` records a ruling: *"`remember({ path: '/temp/scratch' })` **worked** against a remote store and must keep working."* Deleting the `/temp/` affordance is therefore off the table; it is a supported address form.

---

## Decisions

| # | Decision | Rationale |
|---|---|---|
| D-1 | The CLI **refuses** any ephemeral address, on every command, exit 1 | Ephemeral is defined as "dies on process exit". A CLI process exits immediately, so an ephemeral operation there is definitionally meaningless. Saying so beats pretending. |
| D-2 | The raw HTTP wire **refuses** `/temp/` with 400 | The wire sits below the surface layer; it does not accept surface sugar. Also stops `/temp/` becoming a permanent growing on-disk scope. |
| D-3 | Responses echo the **canonical** `~x`, not the caller's spelling | Teaches the caller the real address — precisely what the current bug prevents. Matches existing behavior and existing test expectations. |
| D-4 | `IsEphemeral` / `StripEphemeralPrefix` stay **`~`-only** | See "Rejected alternatives". |
| D-5 | Existing persistent `memories/temp/` data is **not** migrated automatically | See "Migration". |

---

## Architecture

**`/temp/` is a reserved-path alias on the agent-facing surface, exactly like `/skill/`. It is resolved at the surface boundary and never travels below it. The store, the wire, and every layer beneath speak one spelling: `~`.**

This is not a convenience choice. `MemoryTools.cs` is already the reserved-path routing layer — `/skill/` is intercepted there (`:155`, `:206`) before anything else runs. `/temp/` is the same kind of thing and belongs in the same place.

The principle produces all three decisions rather than requiring them as special cases:

- **MCP tools** are a surface: accept `/temp/x`, canonicalize, pass `~x` down.
- **The CLI** is a surface: accept the alias, then refuse the operation (D-1) — because the refusal is about process lifetime, not spelling.
- **The HTTP wire** is below the surface: refuse `/temp/` (D-2). Not an exception — the layer boundary doing its job.

### One home, two readers

`src/Scrinia.Core/MemoryNaming.cs`, beside `StripEphemeralPrefix`:

```csharp
/// True for the "/temp/..." surface alias. The alias is a SURFACE concern: it is
/// canonicalized at the MCP facade and the CLI, and never reaches the store or the wire.
public static bool IsEphemeralAlias(string name) =>
    name.StartsWith("/temp/", StringComparison.OrdinalIgnoreCase);

/// The one place "/temp/x" becomes "~x". Everything below the surface sees only "~".
public static string CanonicalizeEphemeralAlias(string name) =>
    IsEphemeralAlias(name) ? "~" + name["/temp/".Length..] : name;
```

`OrdinalIgnoreCase` preserves the existing literals' behavior exactly.

---

## Components and call sites

### Surface 1 — the MCP facade (`src/Scrinia.Mcp/MemoryTools.cs`)

Canonicalize each incoming address after the existing null/whitespace guard and before delegation. Six methods, seven parameters:

| Method | Line | Address parameter(s) |
|---|---|---|
| `MemoryRemember` | 109 | `path` |
| `MemoryRecall` | 197 | `path` |
| `MemoryAppend` | 266 | `path` |
| `MemoryForget` | 335 | `path` |
| `MemoryCompact` | 354 | `path` |
| `MemoryLink` | 374 | `path` **and** `destination` |

`link` carrying two addresses is the easy one to miss, and is why `/temp/` currently fails on both ends of a link.

`/temp/` and `/skill/` are disjoint prefixes, so ordering relative to the skill-route check is functionally free. Canonicalize **first**, so all subsequent logic in the method sees the canonical form.

Then **delete** both literals — `MemoryTools.Core.cs:140-141` and `:1039-1040`. They become dead code.

**Not address parameters, deliberately untouched:** `search`'s `query`/`pathPrefix`, `list`'s `scopes`/`excludeTopics`, `facts`' `entity`/`entities`. See "Out of scope".

### Surface 2 — the CLI (`src/Scrinia/Commands/ScriniaCommands.Memory.cs`)

The CLI bypasses the facade entirely, calling implementation methods directly at six sites: `:382` (Store), `:423` (Show), `:471` (Forget), `:531` (Append), `:553` (Compact), `:576` (Link). This is a genuine second surface, not an oversight that can be routed away.

D-1 applies to all six, not only writes: a fresh CLI process starts with an empty ephemeral map, so `scri show --name ~x` is as futile as `scri store`. One shared guard, called at the top of each command after argument parsing:

```csharp
/// Canonicalizes the "/temp/" alias, then refuses any ephemeral address. Every ephemeral
/// operation from the CLI is meaningless: the process starts with an empty ephemeral map
/// and exits before anything can read it back. Refusing loudly beats reporting success
/// over a write that is discarded (review finding C1).
private static bool RefuseEphemeral(string name, bool json, out int exitCode)
```

Message:

> `Error: '~x' names an ephemeral memory. Ephemeral memories live only in the process that creates them, and the CLI exits immediately. Use a persistent path.`

Exit 1. Honors `--json` through the existing `WriteJsonError`, matching the surrounding commands' error idiom.

Note the guard canonicalizes first, so the message reports `~x` for an input of `/temp/x` — consistent with D-3.

### Below the surface — the server (`src/Scrinia.Server/Endpoints/MemoryEndpoints.cs`)

One clause in `NameRefusal:990`, ahead of the existing ephemeral skip:

```csharp
private static IResult? NameRefusal(IMemoryStore store, string name)
{
    if (MemoryNaming.IsEphemeralAlias(name))
        return Results.BadRequest(new ErrorResponse(
            "'/temp/' names a client-side ephemeral memory and cannot be stored on the server. "
            + "Use '~name' via an MCP client, or a persistent path."));
    if (store.IsEphemeral(name)) return null;   // unchanged: ~name routes exactly as today
    return TryResolveName(store, name, out _, out _, out IResult? refusal) ? null : refusal;
}
```

Commit `8bf748a` already made name refusal uniform across every route, so this lands on all of them at once with no per-route work.

**Scope of D-2, stated precisely to avoid a misread:** `NameRefusal` guards the REST routes in `MemoryEndpoints`. The server's hosted **MCP endpoint** (`/mcp`) does not go through it — it goes through the facade, which is a surface and therefore *accepts* the alias per the architecture. So on one server process, `POST /api/v1/stores/{s}/memories {"name":"/temp/x"}` is a 400 while `remember({path:'/temp/x'})` over `/mcp` succeeds and stores an ephemeral in the server process. That asymmetry is the design working as intended, not a gap: the two are different layers, not two doors onto the same one.

### Deliberately untouched

`IsEphemeral`, `StripEphemeralPrefix`, all ~20 paired store-layer call sites, `HttpMemoryStore`'s client-side ephemeral handling, and the `Resolve` mirrors in `MemoryTools.Core.cs:236` / `MemoryOrchestrator.cs:39`. The store's contract does not change, so nothing below the surface needs re-verification.

---

## Rejected alternatives

**B — make `IsEphemeral` and `StripEphemeralPrefix` alias-aware.** This was the fix proposed in the design review, and it is wrong. It would make all ~20 paired sites correct with zero edits, which is genuinely attractive. But:

1. It pushes a surface alias down into the store, so the raw `/temp/x` string flows into display names, response text, and `PluginPipeline.BuildRoutingQualifiedName:88` — which would route embedding vectors on `/temp/x`.
2. It makes the **server** ephemeral-aware for `/temp/`, directly contradicting D-2. Recovering the 400 would require special-casing the server out of its own predicate — two rules again.

**C — both, as belt and braces.** Two homes for one rule is the exact anti-pattern the review exists to remove.

**D — delete the `/temp/` affordance entirely.** Blocked by the prior ruling in "Prior constraint".

---

## Error handling

| Input | Surface | Result |
|---|---|---|
| `/temp/x`, `~x` | MCP tool | Canonicalized to `~x`, operates on ephemeral storage, response echoes `~x` |
| `/temp/x`, `~x` | CLI (any of 6 commands) | Exit 1, refusal message naming `~x`, `--json` honored |
| `/temp/x` | HTTP wire (any route) | 400 with `ErrorResponse` |
| `~x` | HTTP wire | Unchanged from today — routes as ephemeral |
| `/temp` (no trailing slash) | any | **Not** an alias; an ordinary persistent path |
| `/tempest/x` | any | **Not** an alias; an ordinary persistent path |
| `/TEMP/x` | MCP tool, CLI | Alias (case-insensitive), same as `/temp/x` |

---

## Testing

The regression test the current code lacks is a **round trip**, not a write-then-read-at-the-other-spelling. Every existing test writes at `/temp/x` and reads at `~x`, which is exactly why the bug survived 2,015 tests.

1. **Round-trip per tool, parameterized.** `remember({path:'/temp/x'})` then each of `recall`, `append`, `compact`, `link`, `forget` at `/temp/x` — all succeed. This single parameterized test is the one that would have caught H2.
2. **Canonical echo (D-3).** `remember({path:'/temp/x'})` response text contains `~x` and not `/temp/x`.
3. **`link` both ends.** `link({path:'/temp/a', destination:'/temp/b'})` succeeds — pins the two-parameter site.
4. **CLI refusal (D-1).** Each of the six commands, with both `~x` and `/temp/x`: exit 1, message present, and the `--json` variant emits well-formed JSON through `WriteJsonError`.
5. **Server 400 (D-2).** `POST /memories {"name":"/temp/x"}` returns 400 with the message; assert on at least one additional route to pin that the refusal is uniform.
6. **Negative cases.** `/temp` and `/tempest/x` are persistent and unaffected; `/TEMP/x` canonicalizes.
7. **Store layer unchanged.** The existing ephemeral suites (which use `~` directly) must stay green with no edits — that is the evidence D-4 held.

Existing tests that write `/temp/x` and read `~x` stay valid and are **not** rewritten: both spellings now address the same memory, so those assertions remain true. They become weaker than the new round-trip test, not wrong.

---

## Documentation

Five user-facing promises and two incorrect code comments:

| File | Line | Change |
|---|---|---|
| `src/Scrinia.Mcp/prompts/guide.md` | 135 | Keep `/temp/` as ephemeral; note it is an MCP-surface alias for `~name` |
| `docs/cli-reference.md` | 735 | **Breaking:** `/temp/` is not available from the CLI. Remove it from the reserved-path table and document the refusal. |
| `docs/getting-started.md` | 189 | Same as above, scoped to whichever surface the surrounding text describes |
| `src/Scrinia.Mcp/MemoryTools.cs` | 110, 334 | No edit. Both examples (`'/temp/scratch'` in the `remember` and `forget` descriptions) return NOT_FOUND today and succeed after this change. Confirm by test 1 rather than by re-reading the prose. |
| `src/Scrinia.Core/PathRouter.cs` | 93-94 | Wrong today ("at the remember and forget entry points"). Rewrite to point at `MemoryNaming.CanonicalizeEphemeralAlias` and the facade. |
| `src/Scrinia.Core/FileMemoryStore.cs` | 20 | Documents `/temp/` as the *store's* convention. Correct it — the store no longer sees the alias. |

CHANGELOG needs a breaking-change entry covering the CLI refusal and the wire 400.

---

## Migration

**No automated migration (D-5).** Deployed instances may hold real bytes under `.scrinia/memories/temp/`, written by raw HTTP callers before the 400. After this change:

- Those bytes are **not deleted or moved**. They remain on disk and remain readable as the ordinary topic scope `local:temp` — via `list({scopes:'local:temp'})` and `search`.
- They become unreachable by the path `/temp/x` through an MCP client, since that now canonicalizes to `~x`.

Auto-moving would be a destructive guess about data the documentation always said would not persist. Documenting the situation and leaving the bytes in place is reversible; moving them is not.

**This is the one decision in this spec made on the author's assumption rather than an explicit ruling.** It is called out here so it can be reversed cheaply. If a migration is wanted, the natural home is the existing `scri migrate` command, and it should warn rather than move.

This workspace has no `memories/temp/` directory, so there is nothing to migrate locally.

---

## Out of scope

- **`search({pathPrefix:'/temp/'})`** stays a filter over indexed persistent memories. `pathPrefix` is not an address, and ephemerals are not in that index; canonicalizing it would imply a search capability that does not exist. `/temp/` as a *filter* continues to mean the `temp` topic. Named in the docs rather than papered over.
- **The CLI bypassing the MCP facade** (six direct implementation calls) is the same root shape as review finding M1 and is branch 6's business. This spec adds a guard at the CLI's own entry points rather than re-routing it.
- **All other review findings.** Branches 2–7.

---

## Success criteria

1. `remember` then `recall`/`append`/`compact`/`link`/`forget`, all at `/temp/x`, all succeed.
2. `scri store /temp/x` exits 1 with a message, and stores nothing — no success report over a discarded write.
3. `POST /memories {"name":"/temp/x"}` returns 400 on every route.
4. The `/temp/` string literal appears in `src/` only inside `MemoryNaming`.
5. Existing ephemeral test suites pass unmodified.
