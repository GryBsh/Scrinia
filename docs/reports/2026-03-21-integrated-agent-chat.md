# March Report: G-14 — Integrated Agent Chat

**Goal:** Add a server-side agent endpoint and web admin chat page, enabling users to query store memories via cloud LLM providers.

**Dates:** 2026-03-21
**Status:** Complete (both phases verified ALL_PASS)

---

## 1. Summary

Scrinia now includes a built-in agent chat feature. Users open the web admin, select a memory store and LLM provider, and ask questions. The server-side agent searches the store's memories, retrieves relevant content, and produces grounded answers — streamed in real-time via SSE. This was built fresh, inspired by the sciens browser MVP, but running server-side with cloud LLM providers (Anthropic, OpenAI, Gemini).

---

## 2. Changes

### Server — Agent Engine (Phase 01)
New `Chat/` directory in `Scrinia.Server`:

| File | Purpose |
|------|---------|
| `ChatOptions.cs` | Configuration POCO — provider API keys, models, base URLs, temperature, max tokens |
| `ChatModels.cs` | DTOs: ChatRequest, ChatMessage, ChatToolCall, ChatEvent, ChatProvidersResponse + ChatJsonContext (trimming-safe) |
| `IChatProvider.cs` | Interface + AgentToolDef record |
| `Providers/AnthropicChatProvider.cs` | Anthropic Messages API — system extraction, tool_use blocks, input_json_delta SSE |
| `Providers/OpenAiChatProvider.cs` | OpenAI Chat Completions — native format, index-based tool accumulation |
| `Providers/GeminiChatProvider.cs` | Gemini generateContent — parts/functionCall format, candidates SSE |
| `ChatProviderFactory.cs` | Creates IChatProvider from config (follows EmbeddingProviderFactory pattern) |
| `AgentLoop.cs` | Agentic tool loop — search_memory + recall_memory, max 5 rounds, IAsyncEnumerable streaming |

New endpoints:

| Endpoint | Purpose | Auth |
|----------|---------|------|
| `POST /api/v1/stores/{store}/chat` | Stream agent chat response (SSE) | `chat` permission |
| `GET /api/v1/stores/{store}/chat/providers` | List configured LLM providers | `chat` permission |

New infrastructure:
- `SseResult` IResult wrapper for `text/event-stream` responses
- `"Chat"` authorization policy for `chat` permission
- `Scrinia:Chat` configuration section in `appsettings.json`

### Web Admin — Chat UI (Phase 02)
| File | Purpose |
|------|---------|
| `pages/AgentChatPage.tsx` | Full chat page — store selector, provider picker, streaming display, tool badges |
| `api/types.ts` | ChatMessage, ChatToolCall, ChatEvent, ChatRequest, ChatProvidersResponse types |
| `api/client.ts` | `getChatProviders()` (JSON), `streamChat()` (fetch+ReadableStream SSE) |
| `App.tsx` | Route `/chat` added |
| `components/Layout.tsx` | Sidebar nav link with MessageSquare icon |

### Bug Fix
- `getHealth()` in web admin client now calls authenticated `/health/details` instead of unauthenticated `/health/ready`. The G-11 health endpoint hardening had silently broken the dashboard's store name extraction (checks array was null for unauthenticated callers). Caught during design interrogation.

### Infrastructure Fixes (same session)
- **Task dependency resolver:** `depends_on` now stores full task names (e.g., `depends_on:01-1-01`). Tasks scoped to goals via `goal:G-N` keyword. Prevents cross-goal collisions when phase numbers are reused.
- **API key salting:** Per-key 16-byte random salt, SHA-256, timing-safe comparison, backwards-compatible with unsalted legacy keys.
- **.gitignore:** Auto-created in `.scrinia/` on FileMemoryStore startup (ignores `.lock` files, `exports/`).
- **`health` permission:** Dedicated permission for `/health/details` endpoint — monitoring keys don't need read/write access.

---

## 3. Findings

No new audit findings were generated for G-14 (feature goal, not audit goal). Prior findings from the session's audit goals (G-9 through G-13) are tracked in `audit:findings-registry` with IDs SEC-001 through SEC-016, QAL-001 through QAL-008, DOC-001 through DOC-010.

---

## 4. Test Impact

| Suite | Before | After | Delta |
|-------|--------|-------|-------|
| Scrinia.Tests (CLI) | 673 | 673 | — |
| Scrinia.Server.Tests | 55 | 60 | +5 (chat endpoint tests) |
| Scrinia.Plugin.Embeddings.Tests | 12 | 12 | — |
| **Total** | **740** | **745** | **+5** |

New tests:
- `Chat_returns_401_without_auth`
- `Chat_returns_403_without_chat_permission`
- `Chat_returns_503_when_no_providers_configured`
- `Chat_providers_returns_empty_when_none_configured`
- `Chat_providers_returns_403_without_chat_permission`

---

## 5. Security Posture

- **Chat endpoint requires `chat` permission** — granular access control, separate from read/write
- **API keys not proxied** — LLM provider keys configured server-side only (appsettings.json), never sent by clients
- **No write access** — agent has read-only tools (search + show), cannot modify store contents
- **Provider created per-request** — no shared state between chat sessions
- **SSE errors sanitized** — internal errors logged server-side, generic message sent to client
- **Accepted risk:** LLM provider API keys stored in server config (same pattern as embedding provider keys)

---

## 6. Configuration Changes

### New: `Scrinia:Chat` section

| Setting | Default | Purpose |
|---------|---------|---------|
| `Providers` | `"none"` | Comma-separated providers to enable (anthropic, openai, gemini) |
| `AnthropicApiKey` | — | Anthropic API key |
| `AnthropicModel` | `claude-sonnet-4-20250514` | Anthropic model |
| `OpenAiApiKey` | — | OpenAI API key |
| `OpenAiModel` | `gpt-4o-mini` | OpenAI model |
| `GeminiApiKey` | — | Google Gemini API key |
| `GeminiModel` | `gemini-2.0-flash` | Gemini model |
| `MaxTokens` | `4096` | Max response tokens |
| `Temperature` | `0.7` | LLM temperature |

**To enable:** Set `Providers` to desired providers (e.g., `"anthropic"`) and add the corresponding API key. Feature returns 503 when no providers are configured.

### New permission: `chat`
Add `"chat"` to API key permissions to allow agent chat access. Monitoring keys should use `"health"` only.

### Breaking change: `getHealth()` in web admin
The web admin client now calls `/health/details` (authenticated) instead of `/health/ready` (unauthenticated). This requires the API key to have the `health` permission for the dashboard to display store names and health checks. The test factory key already includes this permission.

---

## Deferred

- **MCP tool exposure for agent chat** — allow CLI agents to invoke the agent as a tool (`ask_agent(store, question)`). Tracked in `project:future-agent-mcp-tool`.
- **WebLLM / local inference** — browser-side LLM not included in this phase (cloud-only).
- **Conversation persistence** — chat history is session-only (client-side state), not persisted.
- **Write tools** — agent is read-only (search + recall). Store/forget/append deferred.
