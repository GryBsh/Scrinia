import type {
  ListResponse,
  ShowResponse,
  ChunkResponse,
  SearchResponse,
  StoreRequest,
  StoreResponse,
  AppendRequest,
  AppendResponse,
  HealthResponse,
  CreateKeyRequest,
  CreateKeyResponse,
  KeySummaryDto,
  ErrorResponse,
  EmbeddingsStatus,
  EmbeddingsSettings,
  ChatMessage,
  ChatEvent,
  ChatRequest,
  ChatProvidersResponse,
} from './types';

const API_BASE = '/api/v1';

function getToken(): string | null {
  return localStorage.getItem('scrinia-api-key');
}

export function setToken(key: string) {
  localStorage.setItem('scrinia-api-key', key);
}

export function clearToken() {
  localStorage.removeItem('scrinia-api-key');
}

export function hasToken(): boolean {
  return !!getToken();
}

async function apiFetch<T>(path: string, init?: RequestInit): Promise<T> {
  const token = getToken();
  const headers: Record<string, string> = {
    ...(init?.headers as Record<string, string>),
  };

  if (token) {
    headers['Authorization'] = `Bearer ${token}`;
  }

  if (init?.body && !headers['Content-Type']) {
    headers['Content-Type'] = 'application/json';
  }

  const response = await fetch(path, {
    ...init,
    headers,
    credentials: 'same-origin',
  });

  if (response.status === 401) {
    clearToken();
    window.location.href = '/login';
    throw new Error('Unauthorized');
  }

  if (!response.ok) {
    let msg = `HTTP ${response.status}`;
    try {
      const err: ErrorResponse = await response.json();
      msg = err.error || msg;
    } catch { /* ignore */ }
    throw new Error(msg);
  }

  // Guard against SPA fallback returning HTML for missing API routes
  const contentType = response.headers.get('content-type') ?? '';
  if (!contentType.includes('application/json')) {
    throw new Error(`Expected JSON but got ${contentType || 'unknown content type'} — endpoint may not exist`);
  }

  return response.json();
}

export function isAuthenticated(): boolean {
  return hasToken();
}

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

// ── Health ───────────────────────────────────────────────────────────────────

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

// ── Memories ─────────────────────────────────────────────────────────────────

export const MEMORY_LIST_PAGE_SIZE = 200;

export function listMemories(
  store: string,
  scopes?: string,
  offset = 0,
  limit = MEMORY_LIST_PAGE_SIZE,
) {
  const params = new URLSearchParams();
  if (scopes) params.set('scopes', scopes);
  params.set('offset', String(offset));
  params.set('limit', String(limit));
  return apiFetch<ListResponse>(`${API_BASE}/stores/${store}/memories?${params}`);
}

export function showMemory(store: string, name: string) {
  return apiFetch<ShowResponse>(
    `${API_BASE}/stores/${store}/memories/${encodeURIComponent(name)}`
  );
}

export function getChunk(store: string, name: string, index: number) {
  return apiFetch<ChunkResponse>(
    `${API_BASE}/stores/${store}/memories/${encodeURIComponent(name)}/chunks/${index}`
  );
}

export function searchMemories(store: string, query: string, scopes?: string, limit = 20) {
  const params = new URLSearchParams({ q: query, limit: String(limit) });
  if (scopes) params.set('scopes', scopes);
  return apiFetch<SearchResponse>(`${API_BASE}/stores/${store}/search?${params}`);
}

export function storeMemory(store: string, req: StoreRequest) {
  return apiFetch<StoreResponse>(`${API_BASE}/stores/${store}/memories`, {
    method: 'POST',
    body: JSON.stringify(req),
  });
}

export function appendMemory(store: string, name: string, content: string) {
  // Typed as the DTO rather than an inline object literal: the request grew optional
  // description/attributes/keywords fields, and an untyped body would keep compiling if the
  // required field were ever renamed out from under it.
  const body: AppendRequest = { content };
  return apiFetch<AppendResponse>(
    `${API_BASE}/stores/${store}/memories/${encodeURIComponent(name)}/append`,
    { method: 'POST', body: JSON.stringify(body) }
  );
}

export function forgetMemory(store: string, name: string) {
  return apiFetch<{ message: string }>(
    `${API_BASE}/stores/${store}/memories/${encodeURIComponent(name)}`,
    { method: 'DELETE' }
  );
}

export function copyMemory(store: string, name: string, destination: string, overwrite = false) {
  return apiFetch<{ message: string }>(
    `${API_BASE}/stores/${store}/memories/${encodeURIComponent(name)}/copy`,
    { method: 'POST', body: JSON.stringify({ destination, overwrite }) }
  );
}

// ── Keys ─────────────────────────────────────────────────────────────────────

export function listKeys() {
  return apiFetch<KeySummaryDto[]>(`${API_BASE}/keys`);
}

export function createKey(req: CreateKeyRequest) {
  return apiFetch<CreateKeyResponse>(`${API_BASE}/keys`, {
    method: 'POST',
    body: JSON.stringify(req),
  });
}

export function revokeKey(keyId: string) {
  return apiFetch<{ message: string }>(`${API_BASE}/keys/${keyId}`, {
    method: 'DELETE',
  });
}

// ── Embeddings ──────────────────────────────────────────────────────────────

export function getEmbeddingsStatus(store: string) {
  return apiFetch<EmbeddingsStatus>(`${API_BASE}/stores/${store}/embeddings/status`);
}

export function getEmbeddingsSettings(store: string) {
  return apiFetch<EmbeddingsSettings>(`${API_BASE}/stores/${store}/embeddings/settings`);
}

export function reindexEmbeddings(store: string) {
  return apiFetch<{ message: string }>(`${API_BASE}/stores/${store}/embeddings/reindex`, {
    method: 'POST',
  });
}

// ── Chat ────────────────────────────────────────────────────────────────────

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

export function getChatProviders(store: string) {
  return apiFetch<ChatProvidersResponse>(`${API_BASE}/stores/${store}/chat/providers`);
}

export async function streamChat(
  store: string,
  messages: ChatMessage[],
  provider: string | undefined,
  onEvent: (event: ChatEvent) => void,
  signal?: AbortSignal,
): Promise<void> {
  const token = getToken();
  const headers: Record<string, string> = { 'Content-Type': 'application/json' };
  if (token) headers['Authorization'] = `Bearer ${token}`;

  const body: ChatRequest = { messages, provider };

  const response = await fetch(`${API_BASE}/stores/${store}/chat/`, {
    method: 'POST',
    headers,
    body: JSON.stringify(body),
    signal,
  });

  if (response.status === 401) {
    clearToken();
    window.location.href = '/login';
    throw new Error('Unauthorized');
  }

  if (!response.ok) {
    let msg = `HTTP ${response.status}`;
    try {
      const err = await response.json();
      msg = err.error || msg;
    } catch { /* ignore */ }
    throw new Error(msg);
  }

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
}

