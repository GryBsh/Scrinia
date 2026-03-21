import { useState, useRef, useEffect, useCallback } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getHealth, getChatProviders, streamChat } from '../api/client'
import { Loader2, Search, BookOpen, Send, AlertCircle } from 'lucide-react'
import type { ChatMessage, ChatEvent } from '../api/types'

interface DisplayMessage {
  id: string
  role: 'user' | 'assistant'
  content: string
  toolActivity?: { name: string; status: 'searching' | 'recalling' | 'done'; result?: string }[]
}

export default function AgentChatPage() {
  const [store, setStore] = useState('default')
  const [provider, setProvider] = useState<string>('')
  const [messages, setMessages] = useState<DisplayMessage[]>([])
  const [input, setInput] = useState('')
  const [streaming, setStreaming] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const abortRef = useRef<AbortController | null>(null)
  const messagesEndRef = useRef<HTMLDivElement>(null)

  // Fetch available stores from health endpoint
  const { data: health } = useQuery({
    queryKey: ['health'],
    queryFn: getHealth,
    refetchInterval: 30_000,
  })

  const stores = health?.checks
    ?.filter((c) => c.name.startsWith('store:'))
    .map((c) => c.name.replace('store:', '')) ?? []

  // Fetch available providers for selected store
  const { data: providersData } = useQuery({
    queryKey: ['chat-providers', store],
    queryFn: () => getChatProviders(store),
    enabled: !!store,
    retry: false,
  })

  const providers = providersData?.providers ?? []

  // Auto-select first provider
  useEffect(() => {
    if (providers.length > 0 && !provider) setProvider(providers[0])
  }, [providers, provider])

  // Auto-select first store
  useEffect(() => {
    if (stores.length > 0 && store === 'default' && !stores.includes('default') && stores[0]) {
      setStore(stores[0])
    }
  }, [stores, store])

  // Auto-scroll
  useEffect(() => {
    messagesEndRef.current?.scrollIntoView({ behavior: 'smooth' })
  }, [messages])

  const sendMessage = useCallback(async () => {
    const text = input.trim()
    if (!text || streaming) return

    setInput('')
    setError(null)

    const userMsg: DisplayMessage = {
      id: `user-${Date.now()}`,
      role: 'user',
      content: text,
    }

    const assistantMsg: DisplayMessage = {
      id: `assistant-${Date.now()}`,
      role: 'assistant',
      content: '',
      toolActivity: [],
    }

    setMessages((prev) => [...prev, userMsg, assistantMsg])
    setStreaming(true)

    // Build conversation history for the API
    const history: ChatMessage[] = [
      ...messages.map((m) => ({ role: m.role, content: m.content })),
      { role: 'user', content: text },
    ]

    const controller = new AbortController()
    abortRef.current = controller

    try {
      await streamChat(store, history, provider || undefined, (event: ChatEvent) => {
        setMessages((prev) => {
          const updated = [...prev]
          const last = { ...updated[updated.length - 1] }

          switch (event.type) {
            case 'chunk':
              last.content += event.content ?? ''
              break

            case 'tool-start':
              last.toolActivity = [
                ...(last.toolActivity ?? []),
                {
                  name: event.toolName ?? 'unknown',
                  status: event.toolName === 'search_memory' ? 'searching' : 'recalling',
                },
              ]
              break

            case 'tool-result':
              last.toolActivity = (last.toolActivity ?? []).map((t) =>
                t.name === event.toolName && t.status !== 'done'
                  ? { ...t, status: 'done' as const, result: event.content }
                  : t,
              )
              break

            case 'error':
              setError(event.error ?? 'An error occurred')
              break
          }

          updated[updated.length - 1] = last
          return updated
        })
      }, controller.signal)
    } catch (err) {
      if ((err as Error).name !== 'AbortError') {
        setError((err as Error).message)
      }
    } finally {
      setStreaming(false)
      abortRef.current = null
    }
  }, [input, streaming, messages, store, provider])

  const noProviders = providersData && providers.length === 0

  return (
    <div className="flex flex-col h-full">
      {/* Header */}
      <div className="flex items-center gap-4 p-4 border-b bg-white">
        <h2 className="text-lg font-semibold">Agent Chat</h2>

        <select
          value={store}
          onChange={(e) => setStore(e.target.value)}
          className="text-sm border rounded px-2 py-1 bg-white"
        >
          {stores.map((s) => (
            <option key={s} value={s}>{s}</option>
          ))}
        </select>

        {providers.length > 1 && (
          <select
            value={provider}
            onChange={(e) => setProvider(e.target.value)}
            className="text-sm border rounded px-2 py-1 bg-white"
          >
            {providers.map((p) => (
              <option key={p} value={p}>{p}</option>
            ))}
          </select>
        )}

        {providers.length === 1 && (
          <span className="text-xs text-gray-400">{providers[0]}</span>
        )}
      </div>

      {/* Messages */}
      <div className="flex-1 overflow-y-auto p-4 space-y-4">
        {noProviders && (
          <div className="flex items-center gap-3 p-4 bg-amber-50 border border-amber-200 rounded-lg text-sm text-amber-800">
            <AlertCircle className="w-5 h-5 shrink-0" />
            <div>
              <p className="font-medium">No chat providers configured</p>
              <p className="text-xs mt-1">
                Configure <code className="bg-amber-100 px-1 rounded">Scrinia:Chat</code> in appsettings.json with an API key for Anthropic, OpenAI, or Gemini.
              </p>
            </div>
          </div>
        )}

        {messages.length === 0 && !noProviders && (
          <div className="text-center text-gray-400 text-sm py-12">
            Ask a question about the memories in <span className="font-mono">{store}</span>
          </div>
        )}

        {messages.map((msg) => (
          <MessageBubble key={msg.id} message={msg} streaming={streaming && msg.id === messages[messages.length - 1]?.id} />
        ))}

        {error && (
          <div className="flex items-center gap-2 text-sm text-red-600 bg-red-50 border border-red-200 rounded-lg p-3">
            <AlertCircle className="w-4 h-4 shrink-0" />
            {error}
          </div>
        )}

        <div ref={messagesEndRef} />
      </div>

      {/* Input */}
      <div className="p-4 border-t bg-white">
        <div className="flex gap-2">
          <input
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => e.key === 'Enter' && !e.shiftKey && sendMessage()}
            disabled={streaming || noProviders === true}
            placeholder={noProviders ? 'No providers configured' : 'Ask a question...'}
            className="flex-1 px-3 py-2 border rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 disabled:bg-gray-100 disabled:text-gray-400"
          />
          <button
            onClick={sendMessage}
            disabled={streaming || !input.trim() || noProviders === true}
            className="px-3 py-2 bg-blue-600 text-white rounded-lg hover:bg-blue-700 disabled:bg-gray-300 disabled:cursor-not-allowed transition-colors"
          >
            {streaming ? <Loader2 className="w-4 h-4 animate-spin" /> : <Send className="w-4 h-4" />}
          </button>
        </div>
      </div>
    </div>
  )
}

function MessageBubble({ message, streaming }: { message: DisplayMessage; streaming: boolean }) {
  const isUser = message.role === 'user'

  return (
    <div className={`flex ${isUser ? 'justify-end' : 'justify-start'}`}>
      <div
        className={`max-w-[80%] rounded-lg px-4 py-3 text-sm ${
          isUser
            ? 'bg-blue-600 text-white'
            : 'bg-white border text-gray-900'
        }`}
      >
        {/* Tool activity badges */}
        {!isUser && message.toolActivity && message.toolActivity.length > 0 && (
          <div className="flex flex-wrap gap-1.5 mb-2">
            {message.toolActivity.map((tool, i) => (
              <ToolBadge key={i} tool={tool} />
            ))}
          </div>
        )}

        {/* Message content */}
        <div className="whitespace-pre-wrap break-words">
          {message.content}
          {streaming && !isUser && (
            <span className="inline-block w-1.5 h-4 bg-gray-400 ml-0.5 animate-pulse rounded-sm" />
          )}
        </div>
      </div>
    </div>
  )
}

function ToolBadge({ tool }: { tool: { name: string; status: string } }) {
  const isSearching = tool.status === 'searching'
  const isRecalling = tool.status === 'recalling'
  const isDone = tool.status === 'done'

  const Icon = tool.name === 'search_memory' ? Search : BookOpen
  const label = isSearching ? 'Searching...' : isRecalling ? 'Recalling...' : tool.name === 'search_memory' ? 'Searched' : 'Recalled'

  return (
    <span
      className={`inline-flex items-center gap-1 px-2 py-0.5 rounded-full text-xs ${
        isDone
          ? 'bg-green-50 text-green-700 border border-green-200'
          : 'bg-blue-50 text-blue-700 border border-blue-200'
      }`}
    >
      {isDone ? (
        <Icon className="w-3 h-3" />
      ) : (
        <Loader2 className="w-3 h-3 animate-spin" />
      )}
      {label}
    </span>
  )
}
