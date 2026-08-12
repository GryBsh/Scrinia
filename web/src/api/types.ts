// ── Memory CRUD ──────────────────────────────────────────────────────────────

export interface StoreRequest {
  content: string[];
  name: string;
  description?: string;
  attributes?: Record<string, string[]>;
  keywords?: string[];
}

export interface StoreResponse {
  name: string;
  qualifiedName: string;
  chunkCount: number;
  originalBytes: number;
  message: string;
}

export interface AppendRequest {
  content: string;
}

export interface AppendResponse {
  name: string;
  chunkCount: number;
  originalBytes: number;
  message: string;
}

export interface ListResponse {
  memories: MemoryEntry[];
  total: number;
}

// The one complete entry shape — always complete; a null/absent field means genuinely absent,
// never "not requested". Mirrors Scrinia.Server.Models.MemoryEntry.
export interface MemoryEntry {
  name: string;
  qualifiedName: string;
  scope: string;
  chunkCount: number;
  originalBytes: number;
  createdAt: string;
  updatedAt?: string;
  description: string;
  attributes?: Record<string, string[]>;
  keywords?: string[];
  termFrequencies?: Record<string, number>;
  chunkEntries?: ChunkEntryDto[];
  fileHashes?: Record<string, string>;
}

export interface ChunkEntryDto {
  chunkIndex: number;
  contentPreview?: string;
  keywords?: string[];
  termFrequencies?: Record<string, number>;
}

// chunkCount is deleted, not retained: it is content.length, and a derivable field that can
// disagree with its payload is the defect this shape fixes.
export interface ShowResponse {
  name: string;
  content: string[];
  originalBytes: number;
}

export interface SearchResponse {
  results: SearchResultItem[];
}

export interface SearchResultItem {
  type: string;
  name: string;
  score: number;
  description?: string;
  chunkIndex?: number;
  totalChunks?: number;
  // Absent for a "topic" result — a topic is a scope label, not a memory, so it has no real byte
  // count or creation time. Present for "entry" and "chunk" results.
  originalBytes?: number;
  createdAt?: string;
}

export interface ChunkResponse {
  content: string;
  chunkIndex: number;
  totalChunks: number;
}

export interface CopyRequest {
  destination: string;
  overwrite?: boolean;
}

export interface ExportRequest {
  topics: string[];
  filename?: string;
}

export interface ErrorResponse {
  error: string;
}

// ── Health ───────────────────────────────────────────────────────────────────

export interface HealthResponse {
  status: string;
  checks?: HealthCheck[];
}

export interface HealthCheck {
  name: string;
  status: string;
  error?: string;
}

// ── Key management ───────────────────────────────────────────────────────────

export interface CreateKeyRequest {
  userId: string;
  stores: string[];
  permissions?: string[];
  label?: string;
}

export interface CreateKeyResponse {
  rawKey: string;
  keyId: string;
  userId: string;
  stores: string[];
  permissions: string[];
}

export interface KeySummaryDto {
  id: string;
  userId: string;
  stores: string[];
  permissions: string[];
  label?: string;
  createdAt: string;
  lastUsedAt?: string;
  revoked: boolean;
}

// ── Embeddings ──────────────────────────────────────────────────────────────

export interface EmbeddingsStatus {
  provider: string;
  hardware: string;
  available: boolean;
  dimensions: number;
  semanticWeight: number;
  vectorCount: number;
}

export interface EmbeddingsSettings {
  provider: string;
  hardware: string;
  semanticWeight: number;
  maxBatchSize: number;
  ollamaBaseUrl: string;
  ollamaModel: string;
  openAiModel: string;
  openAiBaseUrl: string;
}

export interface EmbeddingsSettingsUpdate {
  semanticWeight?: number;
  maxBatchSize?: number;
}

// ── Chat ────────────────────────────────────────────────────────────────────

export interface ChatMessage {
  role: string;
  content?: string | null;
  toolCalls?: ChatToolCall[];
  toolCallId?: string;
}

export interface ChatToolCall {
  id: string;
  name: string;
  arguments: string;
}

export interface ChatEvent {
  type: 'chunk' | 'tool-start' | 'tool-result' | 'done' | 'error';
  content?: string;
  toolName?: string;
  toolCallId?: string;
  error?: string;
}

export interface ChatRequest {
  messages: ChatMessage[];
  provider?: string;
}

export interface ChatProvidersResponse {
  providers: string[];
}

