# Stream 6 — Web Frontend: Secrets & Correctness Fixes (Design)

**Date:** 2026-07-30
**Source:** 2026-07-29 whole-codebase review (`REQUEST_CHANGES`), remediation Stream 6.
**Scope:** `web/` React + TypeScript + Vite SPA only. No server/C# changes.

## Goal

Fix the seven confirmed web-frontend findings from the review, and establish
the first automated test harness in `web/` so the subtle logic fixes carry
regression protection.

## Confirmed findings (verified against current source)

**Important**

1. **Login persists unverifiable keys** — `web/src/pages/LoginPage.tsx:18-34`.
   `setToken()` writes the key to `localStorage` *before* validating. Only a
   literal `401` clears it; a `500`/`503` falls through the `=== 401` check and
   a network error is swallowed, both then `navigate('/')`. A bad or
   unverifiable key gets stored and drops the user into a broken session.
   (The endpoint-choice half — validating against the non-admin
   `/api/v1/stores/default/memories` route rather than the `ManageKeys`-gated
   `/api/v1/keys` — was already fixed in a prior pass and is retained.)

2. **Health fallback masks failures** — `web/src/api/client.ts:91-102`.
   `getHealth()` catches *every* error from the authenticated `/health/details`
   (including 500) and silently returns the unauthenticated `/health/ready`
   status; the fallback `resp.json()` has no `.ok` guard. A genuine server
   failure is reported as healthy.

3. **Settings numbers serialize as `null`** — `web/src/pages/SettingsPage.tsx:157-162`.
   Empty/invalid inputs → `parseFloat`/`parseInt` → `NaN` → `JSON.stringify`
   emits `null`, which is persisted to the embeddings settings.

**Minor**

4. **SSE final buffer dropped** — `web/src/api/client.ts:254-270`. The read loop
   breaks on `done` without processing the trailing `buffer` or a final decoder
   flush; a last event without a trailing `\n` is lost.

5. **`response.body!` non-null assertion** — `web/src/api/client.ts:250`. Throws
   an opaque `TypeError` if the body is null.

6. **Empty search cannot clear** — `web/src/components/SearchBar.tsx:15`. The
   `if (value.trim())` guard prevents calling `onSearch('')`. The consumer
   (`MemoryBrowserPage`) uses `enabled: !!searchQuery`, so an empty string
   *would* cleanly clear an active search — the guard is the only thing
   blocking it.

7. **Missing programmatic labels (a11y)** — 11 controls:
   `LoginPage.tsx:50`, `SearchBar.tsx:22`, `AgentChatPage.tsx:142/153/206` and
   the icon-only send button `AgentChatPage.tsx:214` have no accessible name;
   `KeyManagementPage.tsx:195/205/214/224` and `SettingsPage.tsx:174/186` have
   `<label>` elements that are not associated with their inputs (no
   `htmlFor`/`id`).

## Server context (relevant to fix #2)

- `/health/details` is gated by the `Health` authorization policy, which
  requires the claim `permission = health`
  (`src/Scrinia.Server/Program.cs:205-206`,
  `src/Scrinia.Server/Endpoints/HealthEndpoints.cs:24-32`). A valid
  authenticated key **without** the `health` permission receives **403**.
- `/health/details` returns **200** when all checks pass and **503** (with the
  same detailed body) when degraded.
- `/health/ready` is unauthenticated, status-only (no `checks` array), returns
  200/503, and is rate-limited.
- `DashboardPage` derives its store list and health tiles from
  `health.checks`, which only the detailed response carries. Therefore the
  **403 → `/health/ready` fallback must be preserved** so non-`health` keys
  keep working exactly as today; only genuine failures (500/network) must stop
  being masked.

## Architecture

**Pattern:** extract the decision/parsing logic into small **exported pure
functions** in `web/src/api/client.ts` (co-located with the code they serve),
TDD those with Vitest, and keep the JSX changes thin — verified by
`tsc -b` + `eslint` + `vite build`.

**Test harness (first in `web/`):** minimal footprint — **`vitest` only**.

- The extracted units touch `fetch` and `localStorage`; those are stubbed with
  `vi.stubGlobal`, so the Node test environment is sufficient — **no jsdom, no
  testing-library**.
- `package.json` scripts: `"test": "vitest run"`, `"test:watch": "vitest"`.
- `vite.config.ts` gains a `test` block: `environment: 'node'`,
  `include: ['src/**/*.test.ts']`. Import `defineConfig` from `vitest/config`
  (superset of the Vite config type) so the `test` key type-checks.
- Tests co-located as `web/src/api/client.test.ts`.
- `vitest` is pinned to a version compatible with Vite 8 at plan time.

## The seven fixes

### 1. Login fail-closed

New pure function in `client.ts`:

```ts
export type LoginOutcome = 'valid' | 'invalid' | 'unverifiable';

export async function validateApiKey(key: string): Promise<LoginOutcome> {
  try {
    const resp = await fetch(`${API_BASE}/stores/default/memories?limit=1`, {
      headers: { Authorization: `Bearer ${key}` },
    });
    if (resp.status === 401) return 'invalid';
    if (resp.ok || resp.status === 403) return 'valid';
    return 'unverifiable';
  } catch {
    return 'unverifiable';
  }
}
```

`LoginPage.handleSubmit` calls it and persists **only** on `valid`:

- `valid` → `setToken(key)`, `navigate('/', { replace: true })`
- `invalid` → `setError('Invalid API key.')`, return (nothing persisted)
- `unverifiable` → `setError("Couldn't verify your key — the server is
  unreachable or returned an error. Try again.")`, return (nothing persisted)

`403` counts as `valid` for login: a valid non-admin key may lack scopes but
can still sign in. Fail-closed policy: when the key cannot be confirmed valid,
the key is neither stored nor used to navigate.

**Tests:** `validateApiKey` returns `invalid` on 401, `valid` on 200 and 403,
`unverifiable` on 500 and on a thrown fetch (network error) — via
`vi.stubGlobal('fetch', …)`.

### 2. Health no-mask

New pure classifiers in `client.ts`:

```ts
export type HealthDetailsAction = 'use-body' | 'redirect' | 'fallback' | 'error';

export function classifyDetailsStatus(status: number): HealthDetailsAction {
  if (status === 401) return 'redirect';
  if (status === 403) return 'fallback';
  if (status === 200 || status === 503) return 'use-body';
  return 'error';
}

export function classifyReadyStatus(status: number): 'use-body' | 'error' {
  return status === 200 || status === 503 ? 'use-body' : 'error';
}
```

`getHealth` does a raw `fetch('/health/details')` (so status is inspectable)
and follows the classifier:

- `redirect` (401) → `clearToken()`, `window.location.href = '/login'`, throw.
- `use-body` (200/503) → `return resp.json()` (503 carries the degraded detail
  body).
- `fallback` (403) → fall through to the unauthenticated `/health/ready`.
- `error` (500/other) → `throw new Error('Health check failed: HTTP ' + status)`
  — no longer masked.

The `/health/ready` fallback path applies `classifyReadyStatus`: `use-body`
(200/503) → `return resp.json()`; `error` → throw. Unauthenticated callers
(no token) skip straight to `/health/ready` as today.

**Tests:** `classifyDetailsStatus` for 401/403/200/503/500; `classifyReadyStatus`
for 200/503/500.

### 3. Settings NaN→null

New pure function in `client.ts`:

```ts
export interface SettingsValidation {
  values: EmbeddingsSettingsUpdate | null;
  errors: { semanticWeight?: string; maxBatchSize?: string };
}

export function validateEmbeddingsSettings(
  semanticWeightStr: string,
  maxBatchSizeStr: string,
): SettingsValidation;
```

Rules (ranges taken from the existing field labels):

- `semanticWeight`: `parseFloat`; must be finite and in `[0, 200]`.
- `maxBatchSize`: must be an integer string in `[1, 64]` (reject non-integer
  and out-of-range).

On any violation, `values` is `null` and the offending field(s) get an error
message. `SettingsForm.handleSave` calls the validator, and:

- if `values` is non-null → `onSave(values)`;
- else → set field-level error state and do **not** call `onSave` (no `null`
  ever reaches the API).

Per-field errors render inline beneath each input.

> **Pre-existing context (out of scope):** the server registers only `GET
> /status`, `GET /settings`, and `POST /reindex` under `/plugins/embeddings`
> (`src/Scrinia.Server/Program.cs:476-512`) — there is **no `PUT /settings`
> handler**. `updateEmbeddingsSettings` therefore hits the SPA fallback and
> fails with the "endpoint may not exist" guard regardless of input. This is a
> pre-existing gap, not in the review's Stream 6 findings, and is not addressed
> here (web-only, no server changes). Fix #3 remains worthwhile: it stops
> `null` from being sent and gives the user inline feedback before the request,
> and is correct for when the endpoint is added. The ranges (`0–200`, `1–64`)
> come from the existing inputs' `min`/`max` attributes, the only authority for
> these UI-only guardrails.

**Tests:** valid inputs → populated `values`, no errors; empty string →
error, `values` null; out-of-range (`-1`, `250`, `0`, `65`) → error; non-integer
batch size (`3.5`) → error.

### 4. SSE final flush

Single buffering implementation in `client.ts`, used by `streamChat`:

```ts
export function drainSse(
  prev: string,
  chunk: string,
  final: boolean,
): { data: string[]; buffer: string } {
  const buffer = prev + chunk;
  const parts = buffer.split('\n');
  const remainder = final ? '' : (parts.pop() ?? '');
  const data: string[] = [];
  for (const line of parts) {
    if (line.startsWith('data: ')) data.push(line.slice('data: '.length));
  }
  return { data, buffer: remainder };
}
```

`streamChat` read loop:

```ts
let buffer = '';
while (true) {
  const { done, value } = await reader.read();
  if (done) break;
  const r = drainSse(buffer, decoder.decode(value, { stream: true }), false);
  buffer = r.buffer;
  for (const d of r.data) { try { onEvent(JSON.parse(d)); } catch { /* skip */ } }
}
const tail = drainSse(buffer, decoder.decode(), true); // final decoder flush + drain
for (const d of tail.data) { try { onEvent(JSON.parse(d)); } catch { /* skip */ } }
```

**Tests:** a `data: ` line split across two chunks is emitted once; a final
`data: ` line with no trailing newline is emitted (not dropped); non-`data:`
lines are ignored.

### 5. `response.body` guard

In `streamChat`, replace `const reader = response.body!.getReader()` with:

```ts
if (!response.body) throw new Error('Streaming not supported: empty response body.');
const reader = response.body.getReader();
```

**Verification:** `tsc -b` (the `!` assertion is removed) + `vite build`.

### 6. Empty search clears

`SearchBar.handleSubmit` drops the guard:

```ts
const handleSubmit = (e: React.FormEvent) => {
  e.preventDefault();
  onSearch(value.trim());
};
```

An empty submit calls `onSearch('')`; the consumer's `enabled: !!searchQuery`
disables the search query and hides the results block — clearing the active
search.

**Verification:** `vite build` + consumer contract (`MemoryBrowserPage`
`enabled: !!searchQuery`).

### 7. A11y labels

- Associate existing labels via `htmlFor`/`id`:
  `KeyManagementPage.tsx` (User ID, Label, Stores, Permissions — 4 pairs),
  `SettingsPage.tsx` (Semantic Weight, Max Batch Size — 2 pairs).
- Add `aria-label` where no label element exists:
  `LoginPage.tsx:50` input → `aria-label="API key"`;
  `SearchBar.tsx:22` input → `aria-label={placeholder}`;
  `AgentChatPage.tsx` store select → `aria-label="Store"`, provider select →
  `aria-label="Provider"`, message input → `aria-label="Message"`, icon-only
  send button → `aria-label="Send message"`.

**Verification:** `vite build` + `eslint` + a grep check that every
`<input>/<select>` has an `aria-label` or associated `<label>`.

## Verification strategy

- `npm run test` — units for fixes 1–4.
- `npm run build` — `tsc -b` (type check, includes the removed `!` assertion in
  fix 5) then `vite build`.
- `npm run lint` — `eslint .`.

## Out of scope / non-goals

- No `jsx-a11y` eslint plugin, no component/DOM tests, no jsdom/testing-library.
- No fail-open login path (fail-closed was chosen).
- No server or C# changes.
- The four reserved user working-tree files
  (`.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`,
  `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`,
  `src/Scrinia.Mcp/prompts/guide.md`) are never staged or modified.

## Global constraints

- Work in `web/` only; do not touch server/C# code or the four reserved user
  files.
- Test harness footprint is minimal: `vitest` only, Node environment.
- Extracted logic is exported and unit-tested; JSX/a11y wiring is verified by
  build + lint.
- Fail-closed login: an unconfirmed key is never persisted or used to navigate.
- Preserve the `403 → /health/ready` health fallback for non-`health` keys.

## Risks

- The one user-visible behavioral change is that login now refuses to proceed
  during a validation-endpoint outage (500/network) instead of proceeding
  optimistically. This is intended per the fail-closed decision.
- Adding a `test` block to `vite.config.ts` requires importing `defineConfig`
  from `vitest/config`; confirm `vite build` still succeeds after the change
  (it is a type-compatible superset).
