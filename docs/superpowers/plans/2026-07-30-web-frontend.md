# Web Frontend Remediation (Stream 6) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Fix the seven confirmed `web/` findings from the 2026-07-29 review and stand up the first automated test harness in `web/`.

**Architecture:** Extract each fix's decision/parsing logic into small exported pure functions in `web/src/api/client.ts`, TDD them with a minimal Vitest harness (Node environment, `fetch`/`localStorage` stubbed via `vi.stubGlobal`), and keep the JSX/a11y changes thin — verified by `tsc -b` + `eslint` + `vite build`.

**Tech Stack:** React 19, TypeScript 6, Vite 8, Vitest (new, dev-only), TanStack Query, ESLint 10 flat config.

## Global Constraints

- Work in `web/` only. Do **not** touch server/C# code.
- Never stage or modify the four reserved user working-tree files: `.gitignore`, `src/Scrinia.Mcp/plugins/opencode/package.json`, `src/Scrinia.Mcp/plugins/opencode/scrinia.ts`, `src/Scrinia.Mcp/prompts/guide.md`.
- Test-harness footprint is minimal: `vitest` only, Node environment. No jsdom, no testing-library, no `jsx-a11y` plugin.
- Test files import test APIs explicitly from `vitest` (`import { describe, it, expect, vi, afterEach } from 'vitest'`) — do **not** rely on globals (the ESLint config exposes only `globals.browser`).
- Extracted logic is exported and unit-tested; JSX/a11y wiring is verified by build + lint only.
- Fail-closed login: an unconfirmed key is never persisted or used to navigate.
- Preserve the `403 → /health/ready` health fallback for non-`health`-permission keys; only genuine failures (500/network) must stop being masked.
- All shell commands run from the `web/` directory unless stated otherwise.
- Commit after each task with the exact message given.

---

### Task 1: Vitest harness + smoke test

Stands up the test runner so every later task can TDD. Deliverable: `npm run test` runs and passes.

**Files:**
- Modify: `web/package.json` (add `vitest` devDependency + `test`/`test:watch` scripts)
- Modify: `web/vite.config.ts` (import `defineConfig` from `vitest/config`, add a `test` block)
- Create: `web/src/harness.test.ts` (temporary smoke test, deleted in Step 6)

**Interfaces:**
- Produces: a working `npm run test` (alias for `vitest run`) that discovers `src/**/*.test.ts` in a Node environment.

- [ ] **Step 1: Install Vitest**

Run (from `web/`):
```bash
npm install -D vitest
```
If npm errors on a peer-dependency conflict with Vite 8, retry with:
```bash
npm install -D vitest --legacy-peer-deps
```
This adds `vitest` to `devDependencies` in `package.json` and updates `package-lock.json`.

- [ ] **Step 2: Add test scripts to `package.json`**

In `web/package.json`, add these two entries to the `"scripts"` object (alongside `dev`, `build`, `lint`, `preview`):
```json
    "test": "vitest run",
    "test:watch": "vitest",
```

- [ ] **Step 3: Add the `test` block to `vite.config.ts`**

Change the first import line from:
```ts
import { defineConfig } from 'vite'
```
to:
```ts
import { defineConfig } from 'vitest/config'
```
Then add a `test` property to the config object (after the `server` block):
```ts
  test: {
    environment: 'node',
    include: ['src/**/*.test.ts'],
  },
```

- [ ] **Step 4: Write the smoke test**

Create `web/src/harness.test.ts`:
```ts
import { describe, it, expect } from 'vitest'

describe('vitest harness', () => {
  it('runs', () => {
    expect(1 + 1).toBe(2)
  })
})
```

- [ ] **Step 5: Run the smoke test — verify it passes**

Run: `npm run test`
Expected: 1 passed. If it fails to discover the file or the `test` key is a type error, revisit Steps 2–3.

- [ ] **Step 6: Delete the smoke test and confirm build/lint still pass**

```bash
rm src/harness.test.ts
npx tsc -b
npm run lint
```
Expected: `tsc` and `eslint` both clean. (The `test` block in `vite.config.ts` type-checks because `vitest/config`'s `defineConfig` is a superset of Vite's.)

- [ ] **Step 7: Commit**

```bash
git add web/package.json web/package-lock.json web/vite.config.ts
git commit -m "test(web): add minimal Vitest harness (Node env)"
```

---

### Task 2: Login fail-closed (`validateApiKey`)

Fix #1. The login must persist and navigate **only** when the key is confirmed valid; a 401 is invalid, a 500/network error is unverifiable and blocks.

**Files:**
- Modify: `web/src/api/client.ts` (add `LoginOutcome` type + `validateApiKey`)
- Modify: `web/src/pages/LoginPage.tsx:11-35` (rewrite `handleSubmit`, update import)
- Test: `web/src/api/client.test.ts` (create)

**Interfaces:**
- Consumes: `API_BASE` (`'/api/v1'`, already in `client.ts`), `setToken` (already exported).
- Produces:
  - `export type LoginOutcome = 'valid' | 'invalid' | 'unverifiable'`
  - `export async function validateApiKey(key: string): Promise<LoginOutcome>`

- [ ] **Step 1: Write the failing tests**

Create `web/src/api/client.test.ts`:
```ts
import { describe, it, expect, vi, afterEach } from 'vitest'
import { validateApiKey } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('validateApiKey', () => {
  it('returns "invalid" on 401', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 401 })))
    expect(await validateApiKey('k')).toBe('invalid')
  })

  it('returns "valid" on 200', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 200 })))
    expect(await validateApiKey('k')).toBe('valid')
  })

  it('returns "valid" on 403 (valid non-admin key)', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 403 })))
    expect(await validateApiKey('k')).toBe('valid')
  })

  it('returns "unverifiable" on 500', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 500 })))
    expect(await validateApiKey('k')).toBe('unverifiable')
  })

  it('returns "unverifiable" on network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('network') }))
    expect(await validateApiKey('k')).toBe('unverifiable')
  })
})
```

- [ ] **Step 2: Run the tests — verify they fail**

Run: `npx vitest run src/api/client.test.ts`
Expected: FAIL — `validateApiKey` is not exported (import/type error).

- [ ] **Step 3: Implement `validateApiKey`**

In `web/src/api/client.ts`, add after the `isAuthenticated` function (around line 87):
```ts
// ── Login ─────────────────────────────────────────────────────────────────────

export type LoginOutcome = 'valid' | 'invalid' | 'unverifiable';

// Validate a candidate key against a route any authenticated key can call
// (not the ManageKeys-gated /keys endpoint). 401 = invalid; 2xx or 403 = valid
// (a non-admin key may lack scopes but can still sign in); anything else, or a
// network failure, is unverifiable — the caller must fail closed.
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

- [ ] **Step 4: Run the tests — verify they pass**

Run: `npx vitest run src/api/client.test.ts`
Expected: 5 passed.

- [ ] **Step 5: Wire `LoginPage` to fail closed**

In `web/src/pages/LoginPage.tsx`, change the import on line 3 from:
```ts
import { setToken } from '../api/client'
```
to:
```ts
import { setToken, validateApiKey } from '../api/client'
```
Then replace the entire `handleSubmit` function (lines 11–35) with:
```tsx
  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!key.trim()) return

    const outcome = await validateApiKey(key.trim())
    if (outcome === 'invalid') {
      setError('Invalid API key.')
      return
    }
    if (outcome === 'unverifiable') {
      setError("Couldn't verify your key — the server is unreachable or returned an error. Try again.")
      return
    }

    // valid — persist only now, then enter the app
    setToken(key.trim())
    navigate('/', { replace: true })
  }
```

- [ ] **Step 6: Typecheck + lint + build**

```bash
npx tsc -b
npm run lint
npm run build
```
Expected: all clean. (`setToken` is still used; no unused-import warnings.)

- [ ] **Step 7: Commit**

```bash
git add web/src/api/client.ts web/src/api/client.test.ts web/src/pages/LoginPage.tsx
git commit -m "fix(web): login fails closed — persist key only when validated"
```

---

### Task 3: Health no-mask (`classifyDetailsStatus` / `classifyReadyStatus`)

Fix #2. `getHealth` must surface genuine failures (500/network) instead of masking them with the unauthenticated status, while still falling back to `/health/ready` for keys that legitimately lack the `health` permission (403).

**Files:**
- Modify: `web/src/api/client.ts` (add two classifiers; rewrite `getHealth`, lines 91–102)
- Test: `web/src/api/client.test.ts` (append)

**Interfaces:**
- Consumes: `getToken`, `clearToken` (already in `client.ts`).
- Produces:
  - `export type HealthDetailsAction = 'use-body' | 'redirect' | 'fallback' | 'error'`
  - `export function classifyDetailsStatus(status: number): HealthDetailsAction`
  - `export function classifyReadyStatus(status: number): 'use-body' | 'error'`

- [ ] **Step 1: Write the failing tests**

Append to `web/src/api/client.test.ts`:
```ts
import { classifyDetailsStatus, classifyReadyStatus } from './client'

describe('classifyDetailsStatus', () => {
  it('401 → redirect', () => expect(classifyDetailsStatus(401)).toBe('redirect'))
  it('403 → fallback', () => expect(classifyDetailsStatus(403)).toBe('fallback'))
  it('200 → use-body', () => expect(classifyDetailsStatus(200)).toBe('use-body'))
  it('503 → use-body (degraded body)', () => expect(classifyDetailsStatus(503)).toBe('use-body'))
  it('500 → error (not masked)', () => expect(classifyDetailsStatus(500)).toBe('error'))
})

describe('classifyReadyStatus', () => {
  it('200 → use-body', () => expect(classifyReadyStatus(200)).toBe('use-body'))
  it('503 → use-body', () => expect(classifyReadyStatus(503)).toBe('use-body'))
  it('502 → error', () => expect(classifyReadyStatus(502)).toBe('error'))
})
```

- [ ] **Step 2: Run the tests — verify they fail**

Run: `npx vitest run src/api/client.test.ts`
Expected: FAIL — classifiers not exported.

- [ ] **Step 3: Implement the classifiers**

In `web/src/api/client.ts`, add directly above the `getHealth` function (currently line 91):
```ts
export type HealthDetailsAction = 'use-body' | 'redirect' | 'fallback' | 'error';

// 401 → the key is gone, redirect to login; 403 → the key lacks the `health`
// permission, fall back to the unauthenticated status; 200/503 → an authorized
// detailed body (503 carries the degraded detail); anything else → a genuine
// failure that must surface rather than be masked by the fallback.
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

- [ ] **Step 4: Run the tests — verify they pass**

Run: `npx vitest run src/api/client.test.ts`
Expected: all passed (Task 2 tests + these 8).

- [ ] **Step 5: Rewrite `getHealth` to use the classifiers**

Replace the current `getHealth` function (lines 91–102) with:
```ts
export async function getHealth(): Promise<HealthResponse> {
  const token = getToken();
  if (token) {
    const resp = await fetch('/health/details', {
      headers: { Authorization: `Bearer ${token}` },
    });
    switch (classifyDetailsStatus(resp.status)) {
      case 'redirect':
        clearToken();
        window.location.href = '/login';
        throw new Error('Unauthorized');
      case 'use-body':
        return resp.json();
      case 'error':
        throw new Error(`Health check failed: HTTP ${resp.status}`);
      case 'fallback':
        break; // 403 — key lacks `health` permission; fall through to status-only
    }
  }
  const resp = await fetch('/health/ready');
  if (classifyReadyStatus(resp.status) === 'error') {
    throw new Error(`Health check failed: HTTP ${resp.status}`);
  }
  return resp.json();
}
```

- [ ] **Step 6: Typecheck + lint + build**

```bash
npx tsc -b
npm run lint
npm run build
```
Expected: all clean.

- [ ] **Step 7: Commit**

```bash
git add web/src/api/client.ts web/src/api/client.test.ts
git commit -m "fix(web): health check surfaces failures instead of masking them"
```

---

### Task 4: Settings validation (`validateEmbeddingsSettings`)

Fix #3. Empty/invalid numeric inputs must not serialize to `null`; block submit and show inline field errors instead.

**Files:**
- Modify: `web/src/api/client.ts` (add `SettingsValidation` + `validateEmbeddingsSettings`)
- Modify: `web/src/pages/SettingsPage.tsx` (import; `SettingsForm` error state, lines 154–163 and the two input blocks)
- Test: `web/src/api/client.test.ts` (append)

**Interfaces:**
- Consumes: `EmbeddingsSettingsUpdate` (already imported in `client.ts` types).
- Produces:
  - `export interface SettingsValidation { values: EmbeddingsSettingsUpdate | null; errors: { semanticWeight?: string; maxBatchSize?: string } }`
  - `export function validateEmbeddingsSettings(semanticWeightStr: string, maxBatchSizeStr: string): SettingsValidation`

- [ ] **Step 1: Write the failing tests**

Append to `web/src/api/client.test.ts`:
```ts
import { validateEmbeddingsSettings } from './client'

describe('validateEmbeddingsSettings', () => {
  it('accepts valid inputs', () => {
    const r = validateEmbeddingsSettings('50', '16')
    expect(r.values).toEqual({ semanticWeight: 50, maxBatchSize: 16 })
    expect(r.errors).toEqual({})
  })

  it('rejects empty semantic weight', () => {
    const r = validateEmbeddingsSettings('', '16')
    expect(r.values).toBeNull()
    expect(r.errors.semanticWeight).toBeTruthy()
  })

  it('rejects empty batch size', () => {
    const r = validateEmbeddingsSettings('50', '')
    expect(r.values).toBeNull()
    expect(r.errors.maxBatchSize).toBeTruthy()
  })

  it('rejects out-of-range semantic weight', () => {
    expect(validateEmbeddingsSettings('-1', '16').values).toBeNull()
    expect(validateEmbeddingsSettings('250', '16').values).toBeNull()
  })

  it('rejects out-of-range batch size', () => {
    expect(validateEmbeddingsSettings('50', '0').values).toBeNull()
    expect(validateEmbeddingsSettings('50', '65').values).toBeNull()
  })

  it('rejects non-integer batch size', () => {
    const r = validateEmbeddingsSettings('50', '3.5')
    expect(r.values).toBeNull()
    expect(r.errors.maxBatchSize).toBeTruthy()
  })

  it('accepts boundary values', () => {
    expect(validateEmbeddingsSettings('0', '1').values).toEqual({ semanticWeight: 0, maxBatchSize: 1 })
    expect(validateEmbeddingsSettings('200', '64').values).toEqual({ semanticWeight: 200, maxBatchSize: 64 })
  })
})
```

- [ ] **Step 2: Run the tests — verify they fail**

Run: `npx vitest run src/api/client.test.ts`
Expected: FAIL — `validateEmbeddingsSettings` not exported.

- [ ] **Step 3: Implement `validateEmbeddingsSettings`**

In `web/src/api/client.ts`, add above the `getEmbeddingsSettings` function (in the Embeddings section, around line 192):
```ts
export interface SettingsValidation {
  values: EmbeddingsSettingsUpdate | null;
  errors: { semanticWeight?: string; maxBatchSize?: string };
}

// Parse the two numeric settings strings with `Number` (stricter than
// parseFloat/parseInt — "12abc" and "" become NaN/0 and are rejected here
// rather than silently serialized as null). Ranges match the inputs' min/max.
export function validateEmbeddingsSettings(
  semanticWeightStr: string,
  maxBatchSizeStr: string,
): SettingsValidation {
  const errors: { semanticWeight?: string; maxBatchSize?: string } = {};

  const weight = Number(semanticWeightStr);
  if (semanticWeightStr.trim() === '' || !Number.isFinite(weight) || weight < 0 || weight > 200) {
    errors.semanticWeight = 'Enter a number between 0 and 200.';
  }

  const batch = Number(maxBatchSizeStr);
  if (maxBatchSizeStr.trim() === '' || !Number.isInteger(batch) || batch < 1 || batch > 64) {
    errors.maxBatchSize = 'Enter a whole number between 1 and 64.';
  }

  if (errors.semanticWeight || errors.maxBatchSize) {
    return { values: null, errors };
  }
  return { values: { semanticWeight: weight, maxBatchSize: batch }, errors: {} };
}
```

- [ ] **Step 4: Run the tests — verify they pass**

Run: `npx vitest run src/api/client.test.ts`
Expected: all passed.

- [ ] **Step 5: Wire `SettingsForm` to validate before submit**

In `web/src/pages/SettingsPage.tsx`, update the import on line 3 to add `validateEmbeddingsSettings`:
```ts
import { getEmbeddingsStatus, getEmbeddingsSettings, updateEmbeddingsSettings, reindexEmbeddings, validateEmbeddingsSettings } from '../api/client'
```
In the `SettingsForm` component, replace the two `useState` lines and `handleSave` (lines 154–163) with:
```tsx
  const [semanticWeight, setSemanticWeight] = useState(String(settings.semanticWeight))
  const [maxBatchSize, setMaxBatchSize] = useState(String(settings.maxBatchSize))
  const [fieldErrors, setFieldErrors] = useState<{ semanticWeight?: string; maxBatchSize?: string }>({})

  const handleSave = (e: React.FormEvent) => {
    e.preventDefault()
    const { values, errors } = validateEmbeddingsSettings(semanticWeight, maxBatchSize)
    if (!values) {
      setFieldErrors(errors)
      return
    }
    setFieldErrors({})
    onSave(values)
  }
```
Then add an inline error under the Semantic Weight input. The input block currently ends at line 182 (`/>`) followed by `</div>`. Insert the error line between the `/>` and the closing `</div>`:
```tsx
            />
            {fieldErrors.semanticWeight && (
              <p className="text-red-500 text-xs mt-1">{fieldErrors.semanticWeight}</p>
            )}
          </div>
```
Do the same for the Max Batch Size input (its `/>` is at line 194):
```tsx
            />
            {fieldErrors.maxBatchSize && (
              <p className="text-red-500 text-xs mt-1">{fieldErrors.maxBatchSize}</p>
            )}
          </div>
```

- [ ] **Step 6: Typecheck + lint + build**

```bash
npx tsc -b
npm run lint
npm run build
```
Expected: all clean.

- [ ] **Step 7: Commit**

```bash
git add web/src/api/client.ts web/src/api/client.test.ts web/src/pages/SettingsPage.tsx
git commit -m "fix(web): validate embeddings settings — no NaN→null, inline errors"
```

---

### Task 5: SSE final flush + response.body guard (`drainSse`)

Fixes #4 and #5, both in `streamChat`. The final buffered event must be emitted, and a null response body must throw a clear error instead of a bare `TypeError`.

**Files:**
- Modify: `web/src/api/client.ts` (add `drainSse`; rewrite the `streamChat` reader loop, lines 250–270)
- Test: `web/src/api/client.test.ts` (append)

**Interfaces:**
- Produces: `export function drainSse(prev: string, chunk: string, final: boolean): { data: string[]; buffer: string }`

- [ ] **Step 1: Write the failing tests**

Append to `web/src/api/client.test.ts`:
```ts
import { drainSse } from './client'

describe('drainSse', () => {
  it('buffers an incomplete line without emitting', () => {
    const r = drainSse('', 'data: hel', false)
    expect(r.data).toEqual([])
    expect(r.buffer).toBe('data: hel')
  })

  it('emits a line once completed across chunks', () => {
    const r = drainSse('data: hel', 'lo\n', false)
    expect(r.data).toEqual(['hello'])
    expect(r.buffer).toBe('')
  })

  it('emits the final line on flush even without a trailing newline', () => {
    const r = drainSse('', 'data: last', true)
    expect(r.data).toEqual(['last'])
    expect(r.buffer).toBe('')
  })

  it('ignores non-data lines', () => {
    const r = drainSse('', 'event: ping\ndata: x\n', false)
    expect(r.data).toEqual(['x'])
    expect(r.buffer).toBe('')
  })
})
```

- [ ] **Step 2: Run the tests — verify they fail**

Run: `npx vitest run src/api/client.test.ts`
Expected: FAIL — `drainSse` not exported.

- [ ] **Step 3: Implement `drainSse`**

In `web/src/api/client.ts`, add directly above the `streamChat` function (currently line 215):
```ts
// Accumulate `chunk` onto `prev`, split into complete `\n`-terminated lines,
// and return every `data: ` payload plus the leftover partial line. On the
// final call (`final: true`) the remaining buffer is treated as a complete
// line so a last event with no trailing newline is not dropped.
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

- [ ] **Step 4: Run the tests — verify they pass**

Run: `npx vitest run src/api/client.test.ts`
Expected: all passed.

- [ ] **Step 5: Rewrite the `streamChat` reader loop**

In `web/src/api/client.ts`, replace the block from `const reader = response.body!.getReader();` through the end of the `while` loop (lines 250–270) with:
```ts
  if (!response.body) {
    throw new Error('Streaming not supported: empty response body.');
  }
  const reader = response.body.getReader();
  const decoder = new TextDecoder();
  let buffer = '';

  const emit = (payloads: string[]) => {
    for (const d of payloads) {
      try {
        const event: ChatEvent = JSON.parse(d);
        onEvent(event);
      } catch { /* skip malformed lines */ }
    }
  };

  while (true) {
    const { done, value } = await reader.read();
    if (done) break;
    const r = drainSse(buffer, decoder.decode(value, { stream: true }), false);
    buffer = r.buffer;
    emit(r.data);
  }
  // Final decoder flush + drain so a last event without a trailing newline is delivered.
  const tail = drainSse(buffer, decoder.decode(), true);
  emit(tail.data);
```

- [ ] **Step 6: Typecheck + lint + build**

```bash
npx tsc -b
npm run lint
npm run build
```
Expected: all clean. Confirm no `response.body!` non-null assertion remains:
```bash
grep -n "response.body!" src/api/client.ts || echo "no non-null assertion — good"
```

- [ ] **Step 7: Commit**

```bash
git add web/src/api/client.ts web/src/api/client.test.ts
git commit -m "fix(web): flush final SSE event and guard null response body"
```

---

### Task 6: Empty-search clear + a11y labels

Fixes #6 and #7, both thin JSX with no extractable logic — verified by typecheck, lint, build, and a grep audit. No unit tests.

**Files:**
- Modify: `web/src/components/SearchBar.tsx` (handler at line 15; input `aria-label`)
- Modify: `web/src/pages/LoginPage.tsx` (input `aria-label`)
- Modify: `web/src/pages/AgentChatPage.tsx` (two selects, message input, send button)
- Modify: `web/src/pages/KeyManagementPage.tsx` (4 `label`/`input` `htmlFor`/`id` pairs)
- Modify: `web/src/pages/SettingsPage.tsx` (2 `label`/`input` `htmlFor`/`id` pairs)

**Interfaces:** none (JSX-only).

- [ ] **Step 1: Empty search clears (fix #6)**

In `web/src/components/SearchBar.tsx`, replace `handleSubmit` (lines 13–16) with:
```tsx
  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    onSearch(value.trim())
  }
```
(An empty submit now calls `onSearch('')`; the consumer's `enabled: !!searchQuery` disables the query and hides results — clearing the active search.)

- [ ] **Step 2: A11y — SearchBar input (fix #7)**

In the same file, add `aria-label={placeholder}` to the `<input>` (line 22), e.g. after the `type="text"` attribute:
```tsx
        <input
          type="text"
          aria-label={placeholder}
          value={value}
```

- [ ] **Step 3: A11y — LoginPage input**

In `web/src/pages/LoginPage.tsx`, add `aria-label="API key"` to the `<input>` (line 50), after `type="password"`:
```tsx
            <input
              type="password"
              aria-label="API key"
              value={key}
```

- [ ] **Step 4: A11y — AgentChatPage controls**

In `web/src/pages/AgentChatPage.tsx`:
- Store `<select>` (line 142): add `aria-label="Store"` after `value={store}`.
- Provider `<select>` (line 153): add `aria-label="Provider"` after `value={provider}`.
- Message `<input>` (line 206): add `aria-label="Message"` after `value={input}`.
- Send `<button>` (line 214): add `aria-label="Send message"` after `onClick={sendMessage}`.

Example for the store select:
```tsx
        <select
          value={store}
          aria-label="Store"
          onChange={(e) => setStore(e.target.value)}
```

- [ ] **Step 5: A11y — KeyManagementPage label associations**

In `web/src/pages/KeyManagementPage.tsx`, associate each `<label>` with its `<input>` via `htmlFor`/`id` (lines 194–230):
- `<label htmlFor="key-user-id" ...>User ID *</label>` + `<input id="key-user-id" ...>`
- `<label htmlFor="key-label" ...>Label</label>` + `<input id="key-label" ...>`
- `<label htmlFor="key-stores" ...>Stores (comma-sep) *</label>` + `<input id="key-stores" ...>`
- `<label htmlFor="key-permissions" ...>Permissions (comma-sep)</label>` + `<input id="key-permissions" ...>`

Example for the first pair:
```tsx
          <label htmlFor="key-user-id" className="text-xs text-gray-500">User ID *</label>
          <input
            id="key-user-id"
            type="text"
            value={userId}
```

- [ ] **Step 6: A11y — SettingsPage label associations**

In `web/src/pages/SettingsPage.tsx`, associate the two config labels (lines 173–194):
- `<label htmlFor="settings-semantic-weight" ...>Semantic Weight (0-200)</label>` + `<input id="settings-semantic-weight" ...>`
- `<label htmlFor="settings-max-batch-size" ...>Max Batch Size (1-64)</label>` + `<input id="settings-max-batch-size" ...>`

Example:
```tsx
            <label htmlFor="settings-semantic-weight" className="text-xs text-gray-500">Semantic Weight (0-200)</label>
            <input
              id="settings-semantic-weight"
              type="number"
              value={semanticWeight}
```

- [ ] **Step 7: Audit — every control has an accessible name**

Run (from `web/`):
```bash
grep -rnE "<(input|select)" src --include=*.tsx
```
Manually confirm each hit either has an `aria-label` or an `id` that matches a `<label htmlFor=...>`. Expected accessible-name coverage: SearchBar (aria-label), LoginPage (aria-label), AgentChat store/provider/message (aria-label), KeyManagement ×4 (htmlFor/id), Settings ×2 (htmlFor/id). The AgentChat send button is icon-only and carries `aria-label="Send message"`.

- [ ] **Step 8: Typecheck + lint + build + full test run**

```bash
npx tsc -b
npm run lint
npm run build
npm run test
```
Expected: all clean; full Vitest suite green.

- [ ] **Step 9: Commit**

```bash
git add web/src/components/SearchBar.tsx web/src/pages/LoginPage.tsx web/src/pages/AgentChatPage.tsx web/src/pages/KeyManagementPage.tsx web/src/pages/SettingsPage.tsx
git commit -m "fix(web): clear search on empty submit; add programmatic labels"
```

---

## Verification (whole branch)

From `web/`:
```bash
npm run test    # Vitest — validateApiKey, classifiers, validateEmbeddingsSettings, drainSse
npm run build   # tsc -b (type check) + vite build
npm run lint    # eslint .
```
All three must be clean before finishing the branch.

## Self-Review notes

- **Spec coverage:** Task 1 = harness; Task 2 = fix #1; Task 3 = fix #2; Task 4 = fix #3; Task 5 = fixes #4 + #5; Task 6 = fixes #6 + #7. All seven findings + harness covered.
- **Type consistency:** `validateApiKey`/`LoginOutcome`, `classifyDetailsStatus`/`HealthDetailsAction`/`classifyReadyStatus`, `validateEmbeddingsSettings`/`SettingsValidation`, and `drainSse` names/signatures are identical between their defining task and their consumer wiring.
- **Out of scope (per spec):** no server PUT `/settings` endpoint is added (pre-existing gap); no fail-open login; no jsx-a11y plugin or DOM tests.
