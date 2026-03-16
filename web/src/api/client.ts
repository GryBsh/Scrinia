import type {
  ListResponse,
  ShowResponse,
  ChunkResponse,
  SearchResponse,
  StoreRequest,
  StoreResponse,
  AppendResponse,
  HealthResponse,
  CreateKeyRequest,
  CreateKeyResponse,
  KeySummaryDto,
  ErrorResponse,
  EmbeddingsStatus,
  EmbeddingsSettings,
  EmbeddingsSettingsUpdate,
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

// ── Health ───────────────────────────────────────────────────────────────────

export async function getHealth(): Promise<HealthResponse> {
  const resp = await fetch('/health/ready');
  return resp.json();
}

// ── Memories ─────────────────────────────────────────────────────────────────

export function listMemories(store: string, scopes?: string) {
  const params = scopes ? `?scopes=${encodeURIComponent(scopes)}` : '';
  return apiFetch<ListResponse>(`${API_BASE}/stores/${store}/memories${params}`);
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
  return apiFetch<AppendResponse>(
    `${API_BASE}/stores/${store}/memories/${encodeURIComponent(name)}/append`,
    { method: 'POST', body: JSON.stringify({ content }) }
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

export function getEmbeddingsStatus() {
  return apiFetch<EmbeddingsStatus>(`${API_BASE}/plugins/embeddings/status`);
}

export function getEmbeddingsSettings() {
  return apiFetch<EmbeddingsSettings>(`${API_BASE}/plugins/embeddings/settings`);
}

export function updateEmbeddingsSettings(update: EmbeddingsSettingsUpdate) {
  return apiFetch<{ message: string }>(`${API_BASE}/plugins/embeddings/settings`, {
    method: 'PUT',
    body: JSON.stringify(update),
  });
}

export function reindexEmbeddings() {
  return apiFetch<{ message: string }>(`${API_BASE}/plugins/embeddings/reindex`, {
    method: 'POST',
  });
}
