# Surface Defects — Design

**Status:** implemented on branch `surface-defects`.
**Sequence:** residuals spec 3 of 4. Inventory: `2026-08-08-residuals-triage.md`
(T2.1, T2.2, T3.3, T3.4, T4.1, T4.2, T4.5).

## Goal

Fix the places where Scrinia's outward surfaces — one HTTP endpoint, one CLI command pair, and
three small guards — are wrong in ways a caller hits directly. Two of these are dead on
arrival: an endpoint that has likely never worked, and a command pair that reports success on
failure.

## Why these belong together

They share a shape rather than a subsystem: **each is a boundary where the code declares one
contract and honours another.** A parameter declared as query but bound as body. A guard that
tests for a string the producer never emits. A validation that bounds one dimension of an input
and not the other. They are small, independent, and cheap to review as a set; splitting them
would cost more in ceremony than the fixes cost to make.

## Established facts

**This table describes state at `02d1a29`** — the commit this branch was cut from, before any
of the fixes below landed. Its line numbers are not updated for what the branch changed; in
particular, the dead guards F4 describes (`ScriniaCommands.Bundle.cs:57`, `:99`) were removed
by the branch, and F1, F7, F9, and F10 are all now fixed. See `CHANGELOG.md` and the branch's
commits for current state.

| # | Fact | Evidence |
|---|---|---|
| F1 | `POST /api/v1/stores/{store}/import` declares `string[]? topics = null` with no `[FromQuery]` | `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:370` |
| F2 | ASP.NET Core Minimal API infers a complex-typed parameter as JSON-body-bound, so a non-`application/json` Content-Type is rejected with 415 before the handler runs | framework behaviour; reproduced over both TestServer and a real Kestrel socket during store-boundaries Task 3a |
| F3 | The sibling export endpoint takes an `ExportRequest` DTO and is correct | `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:337` |
| F4 | `ScriniaCommands.Bundle.cs` still tests `result.StartsWith("Error:", …)` at `:57` and `:99`, each preceded by a comment at `:51`/`:95` stating the guard is dead | `src/Scrinia/Commands/ScriniaCommands.Bundle.cs` |
| F5 | `ToYaml` always opens with `status: `, so `"Error:"` never matches — the guards are dead by construction | established on the `mcp-response-shape` branch, which fixed the same defect for `show`/`forget` |
| F6 | `Status == "error"` appears at six production sites: four `== "error"` and two `!= "error"` | `MemoryTools.Core.cs:1377`, `ScriniaCommands.cs:66`, `ScriniaCommands.Memory.cs:421`, `:467` (positive); `MemoryTools.cs:154`, `:259` (negative) |
| F7 | `attributeFilter` is bounded at 64 keys; per-key value-array length is unbounded | `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:300-301` |
| F8 | `InstallDefault(params IMemoryEventSink[])` has no argument validation | `src/Scrinia.Core/IMemoryEventSink.cs:39-40` |
| F9 | `BuildSearchGet` checks `scopes is not null` while its two siblings check `!IsNullOrWhiteSpace` | `src/Scrinia/HttpMemoryStore.cs:411` vs `:413`, `:415` |
| F10 | Two unused using directives | `src/Scrinia.Server/Endpoints/MemoryEndpoints.cs:1`, `:7` |

**F6 corrects the source memory**, which recorded eight sites across two projects. Four is the
count at `c3773bf`. A plan that goes looking for eight will either invent them or conclude the
grep is broken.

## Design

### 1. `POST /api/v1/stores/{store}/import` accepts the requests it was built for

Per F1–F3: every genuine multipart bundle upload gets 415 before the handler runs. This
endpoint has likely never worked for a real caller. It was found by a *guard test* on the
store-boundaries branch — the test correctly declined to fake a 200-returning local-import case
over a path that is blocked upstream of the guard, which is how the bug surfaced at all.

Two fixes, both correct:
- Add `[FromQuery]` to `topics`.
- Move it into a request DTO, matching the export sibling (F3).

**Take `[FromQuery]`.** The parameter is a filter on a multipart upload, not part of a JSON
body — there is no body to put it in. Matching the sibling's *shape* would mean matching a DTO
that exists because export has no file payload; the two endpoints differ for a real reason.

The test must go over a transport that actually performs content negotiation. A handler-level
unit test cannot observe a 415 issued by the binding layer.

### 2. Bundle commands report failure

Apply the fix pattern `mcp-response-shape` already used for `show`/`forget`: read the typed
status instead of substring-matching rendered output.

F4 is worth dwelling on. Someone identified these guards as dead, wrote `// NOTE: dead guard`
above each one, and left them in place. The comment is accurate and the code is still wrong —
`scri bundle export` and `scri bundle import` still exit 0 on genuine failure, which means any
script or CI step gating on their exit code is not gated at all. Documenting a defect is not
fixing it, and a comment that explains why the code is broken can make it *less* likely someone
fixes it, because the oddity now looks deliberate.

### 3. A shared predicate for error status

Add `public bool IsError => Status == "error";` to `McpResponse` and route F6's six sites
through it.

The `mcp-response-shape` residual is explicit that this was deliberately held out of that
branch and "belongs on its own branch/commit with its own review" — this is that commit. It is
the natural companion to §2, which adds a fifth and sixth reader of the same condition.

### 4. Bound the other dimension of `attributeFilter`

Per F7, a filter with three keys each carrying a hundred thousand values passes validation;
Kestrel's body limit is the only backstop. Bound per-key value-array length, in the same place
and the same style as the existing key-count check.

### 5. `InstallDefault` validates its arguments

Per F8: an empty array silently installs a no-op composite sink — every memory event is
dropped, with no error. A null element becomes a `NullReferenceException` on the first sink
call; `CompositeEventSink`'s own per-sink catch handler then dereferences the same null via
`sink.GetType()` to name it in the log line, so a *second* NRE is thrown from inside the catch,
escapes the loop, and aborts every sink after it. That is a loud, misattributed crash, not a
silent no-op.

This is not a regression. The six inline call sites it replaced had identical exposure. But
centralising them into one method turns six independent chances to notice into one trap for
whichever future seventh caller gets it wrong — which is the ordinary cost of a good
refactor, payable by adding the guard the individual sites never had.

Reject both empty and null-containing input outright. A sink installation that silently
discards every event is never what the caller meant.

### 6. Two consistency fixes

- F9: make `BuildSearchGet` treat `scopes` like its siblings. An empty-string `scopes` currently
  emits `&scopes=` where the other two filters would omit it. No known caller passes an empty
  string, so this is a consistency fix inside one method, not a bug report.
- F10: delete the two unused usings. Invisible to the build because `EnforceCodeStyleInBuild` is
  unset on that project, so nothing flags them. Verify with the compiler, not by eye — the file
  uses `RequireRateLimiting`, which resolves through implicit usings rather than through the
  `System.Threading.RateLimiting` import, and that is exactly the kind of thing a quick read
  gets wrong.

## Out of scope

- Any other exit-code mirror defect. `show` and `forget` are already fixed; this spec covers the
  bundle pair. If planning finds a third, record it — do not widen.
- Rate-limiting or auth changes to `/import` beyond the binding fix.

## Testing

- `POST /api/v1/stores/{store}/import` with a real multipart payload over a transport that
  performs content negotiation → the handler runs. Assert on the import outcome, not merely on
  "not 415".
- `scri bundle export` and `scri bundle import` against a genuine failure → non-zero exit.
  Assert the exit code; asserting the message would re-create the substring coupling being
  removed.
- `InstallDefault` with an empty array and with a null element → both rejected.
- `attributeFilter` with one key carrying an oversized value array → 400.

The `IsError` change (§3) is a refactor with no behaviour change; the existing suite is its
test, provided the six sites are genuinely all of them (F6).

## Risks

**§2 changes exit codes.** That is the point, and it is the same change `mcp-response-shape`
made for `show`/`forget`, so the precedent and the CHANGELOG convention both exist. Anything
currently depending on `scri bundle import` exiting 0 on failure is depending on a defect — but
it should still get a CHANGELOG entry, because a CI pipeline that starts failing is entitled to
an explanation.

Everything else here is additive or internal.
