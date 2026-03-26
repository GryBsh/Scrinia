// ── Memory CRUD ──────────────────────────────────────────────────────────────

export interface StoreRequest {
  content: string[];
  name: string;
  description?: string;
  tags?: string[];
  keywords?: string[];
  reviewAfter?: string;
  reviewWhen?: string;
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
  memories: MemoryListItem[];
  total: number;
}

export interface MemoryListItem {
  name: string;
  qualifiedName: string;
  scope: string;
  chunkCount: number;
  originalBytes: number;
  createdAt: string;
  updatedAt?: string;
  description: string;
  tags?: string[];
}

export interface ShowResponse {
  name: string;
  content: string;
  chunkCount: number;
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

// ── Workflow dashboard ──────────────────────────────────────────────────────

export interface WorkflowSummary {
  name: string;
  seedActivityCount: number;
  gateActivityCount: number;
  isBuiltIn: boolean;
}

export interface WorkflowListResponse {
  workflows: WorkflowSummary[];
}

export interface WorkflowContent {
  name: string;
  yamlContent: string;
}

export interface GoalSummary {
  id: string;
  description: string;
  status: string;
  workflowRef: string | null;
  progressPercent: number;
}

export interface GoalListResponse {
  goals: GoalSummary[];
}

export interface TaskSummary {
  name: string;
  status: string;
  wave: number;
  skill: string | null;
  dependsOn: string[];
  gateType: string | null;
  description: string | null;
}

export interface GoalDetailResponse {
  id: string;
  description: string;
  status: string;
  workflowRef: string | null;
  progressPercent: number;
  phases: PhaseGroup[];
}

export interface PhaseGroup {
  phaseId: string;
  tasks: TaskSummary[];
}

export interface TaskListResponse {
  tasks: TaskSummary[];
}

export interface TaskEvent {
  goalId: string;
  taskName: string;
  oldStatus: string;
  newStatus: string;
  timestamp: string;
}
