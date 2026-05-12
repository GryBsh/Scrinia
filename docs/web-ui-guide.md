# Web UI Developer Guide

The Scrinium web UI is a React 19 SPA built with Vite, Tailwind CSS 4, React Router 7, and TanStack Query 5. It provides a browser-based interface for managing memories, API keys, and embeddings settings.

## Quick Start

```bash
cd web

# Development (with HMR, proxies API to :5000)
npm install
npm run dev          # Vite dev server on http://localhost:5173

# Production build (outputs to src/Scrinia.Server/wwwroot/)
npm ci
npm run build        # Type-checks then builds
```

The dev server proxies `/api`, `/health`, and `/mcp` to `http://localhost:5000` — start the API server first:

```bash
dotnet run --project src/Scrinia.Server
```

## Project Structure

```
web/
  src/
    main.tsx                 Entry point (QueryClient + BrowserRouter)
    App.tsx                  Route definitions + ProtectedRoute wrapper
    index.css                Tailwind import (@import "tailwindcss")
    api/
      client.ts              API functions + token management (apiFetch)
      types.ts               TypeScript request/response interfaces
    pages/
      LoginPage.tsx           API key entry + validation
      DashboardPage.tsx       Health status + store overview
      MemoryBrowserPage.tsx   List/search memories with scope tabs
      MemoryDetailPage.tsx    View content + chunks + delete
      KeyManagementPage.tsx   Create/list/revoke API keys
      SettingsPage.tsx        Embeddings config + reindex
    components/
      Layout.tsx              Sidebar nav + outlet
      SearchBar.tsx           Reusable search input
      MemoryList.tsx          Memory table component
      MemoryContent.tsx       Single-chunk content viewer
      ChunkViewer.tsx         Multi-chunk tab navigator
  vite.config.ts             Build output + dev proxy config
  package.json               Dependencies + scripts
  tsconfig.json              TypeScript config
```

## Dependencies

| Package | Version | Purpose |
|---------|---------|---------|
| `react` | 19.2 | UI framework |
| `react-router` | 7.13 | Client-side routing |
| `@tanstack/react-query` | 5.90 | Data fetching + caching |
| `lucide-react` | 0.575 | Icons |
| `tailwindcss` | 4.2 | Utility-first CSS |
| `vite` | 7.3 | Build tool + dev server |

## Routing

Defined in `App.tsx` using React Router v7:

```
/login                              → LoginPage (public)
/ (ProtectedRoute)
  ├── /                             → DashboardPage
  ├── /stores/:store                → MemoryBrowserPage
  ├── /stores/:store/memories/:name → MemoryDetailPage
  ├── /chat                         → AgentChatPage
  ├── /keys                         → KeyManagementPage
  └── /settings                     → SettingsPage
```

`ProtectedRoute` checks `hasToken()` and redirects to `/login` if unauthenticated.

## Authentication Flow

1. User enters API key on `/login`
2. Key validated via request to `/api/v1/keys` with `Authorization: Bearer {key}`
3. On success, key stored in `localStorage['scrinia-api-key']`
4. All subsequent API calls include the Bearer token via `apiFetch()`
5. On 401 response, token is cleared and user redirected to `/login`

No refresh tokens or session timeout — keys are long-lived. Logout clears localStorage.

## API Client

`src/api/client.ts` provides typed functions for all server endpoints:

**Memory operations:** `listMemories`, `showMemory`, `getChunk`, `searchMemories`, `storeMemory`, `appendMemory`, `forgetMemory`, `copyMemory`

**Key management:** `listKeys`, `createKey`, `revokeKey`

**Embeddings:** `getEmbeddingsStatus`, `getEmbeddingsSettings`, `updateEmbeddingsSettings`, `reindexEmbeddings`

**Health:** `getHealth`

The internal `apiFetch()` helper adds auth headers, handles 401 redirects, and validates JSON responses (guards against HTML SPA fallback on 404).

## State Management

**TanStack Query** handles all server state:

```typescript
// QueryClient configured in main.tsx
const queryClient = new QueryClient({
  defaultOptions: {
    queries: { retry: 1, staleTime: 30_000 }  // 30s cache
  }
})
```

Patterns used:
- `useQuery()` for reads with automatic caching and 30s stale time
- `useMutation()` for writes with `onSuccess` cache invalidation
- `refetchInterval: 30_000` for polling (health checks, embeddings status)
- `enabled: !!searchQuery` for conditional queries

No Redux, Context, or global state — all UI state is component-local via `useState()`.

## Styling

Tailwind CSS v4 with the Vite plugin (`@tailwindcss/vite`). No custom theme or CSS modules — all styling uses Tailwind utility classes directly.

Common patterns:
- **Cards:** `bg-white rounded-lg border p-4`
- **Buttons:** `px-3 py-2 text-sm rounded-lg hover:bg-opacity-90 transition-colors disabled:opacity-50`
- **Inputs:** `px-3 py-1.5 border rounded text-sm focus:outline-none focus:ring-2 focus:ring-blue-500`
- **Sidebar:** Dark theme (`bg-gray-900 text-white`)
- **Content:** Light theme (`bg-gray-50`)

## Vite Configuration

```typescript
export default defineConfig({
  plugins: [react(), tailwindcss()],
  build: {
    outDir: '../src/Scrinia.Server/wwwroot',
    emptyOutDir: true
  },
  server: {
    proxy: {
      '/api': 'http://localhost:5000',
      '/health': 'http://localhost:5000',
      '/mcp': 'http://localhost:5000',
    },
  },
})
```

Production builds output directly to the ASP.NET Core `wwwroot/` directory. The server's `UseDefaultFiles` + `UseStaticFiles` serves the SPA, with `MapFallbackToFile("index.html")` for client-side routing.

## Pages

### DashboardPage
Shows server health status (auto-refreshes every 30s) and store cards with memory counts. Stores are discovered from the health check response.

### MemoryBrowserPage
Lists memories in a table with scope filter tabs. Supports real-time search via `SearchBar` — search results show type badges and relevance scores.

### MemoryDetailPage
Displays full memory content with metadata (chunks, size, tokens). Multi-chunk memories use `ChunkViewer` with tab navigation — chunks are lazy-loaded. Includes delete with confirmation.

### AgentChatPage
Interactive chat with the cloud LLM providers configured under `Scrinia:Chat` (Anthropic, OpenAI, Gemini — whichever have API keys set). The page auto-selects the first available store from the health check, then queries `/api/v1/stores/{store}/chat/providers` to populate the provider dropdown. Messages stream from `POST /api/v1/stores/{store}/chat/` and the page surfaces in-flight `memory('search')` and `memory('recall')` tool calls inline so the user can see what the agent is consulting. Requires the `chat` permission on the API key. Hidden gracefully if no providers are configured.

### KeyManagementPage
Create keys with userId, stores, permissions, and label. Lists existing keys with status. Copy-to-clipboard for new keys. Revoke with confirmation.

### SettingsPage
Embeddings status display (provider, availability, dimensions, vector count). Update semantic weight and batch size. Reindex action. Gracefully handles missing embeddings plugin.
