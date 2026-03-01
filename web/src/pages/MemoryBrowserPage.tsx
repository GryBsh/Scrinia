import { useState } from 'react'
import { useParams } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { listMemories, searchMemories } from '../api/client'
import MemoryList from '../components/MemoryList'
import SearchBar from '../components/SearchBar'
import type { SearchResultItem } from '../api/types'
import { Link } from 'react-router'

export default function MemoryBrowserPage() {
  const { store = 'default' } = useParams()
  const [searchQuery, setSearchQuery] = useState('')
  const [scopeFilter, setScopeFilter] = useState<string>('')

  const { data, isLoading, error } = useQuery({
    queryKey: ['memories', store, scopeFilter],
    queryFn: () => listMemories(store, scopeFilter || undefined),
  })

  const { data: searchResults } = useQuery({
    queryKey: ['search', store, searchQuery],
    queryFn: () => searchMemories(store, searchQuery),
    enabled: !!searchQuery,
  })

  // Extract unique scopes for filter tabs
  const scopes = data?.memories
    ? [...new Set(data.memories.map((m) => m.scope))]
    : []

  return (
    <div className="p-6">
      <div className="flex items-center justify-between mb-6">
        <div>
          <h2 className="text-xl font-semibold">Memories</h2>
          <p className="text-sm text-gray-500">
            Store: <span className="font-mono">{store}</span>
            {data && <span className="ml-2">({data.total} total)</span>}
          </p>
        </div>
      </div>

      <div className="mb-4">
        <SearchBar onSearch={setSearchQuery} />
      </div>

      {/* Scope filter tabs */}
      {scopes.length > 1 && (
        <div className="flex gap-1 mb-4">
          <ScopeTab
            label="All"
            active={scopeFilter === ''}
            onClick={() => setScopeFilter('')}
          />
          {scopes.map((s) => (
            <ScopeTab
              key={s}
              label={s}
              active={scopeFilter === s}
              onClick={() => setScopeFilter(s)}
            />
          ))}
        </div>
      )}

      {/* Search results */}
      {searchQuery && searchResults && (
        <div className="mb-6">
          <h3 className="text-sm font-medium text-gray-500 mb-2">
            Search results for "{searchQuery}"
          </h3>
          <SearchResults results={searchResults.results} store={store} />
        </div>
      )}

      {/* Memory list */}
      <div className="bg-white rounded-lg border">
        {isLoading && <p className="p-4 text-gray-500 text-sm">Loading...</p>}
        {error && (
          <p className="p-4 text-red-500 text-sm">Error: {(error as Error).message}</p>
        )}
        {data && <MemoryList memories={data.memories} store={store} />}
      </div>
    </div>
  )
}

function ScopeTab({
  label,
  active,
  onClick,
}: {
  label: string
  active: boolean
  onClick: () => void
}) {
  return (
    <button
      onClick={onClick}
      className={`px-3 py-1 text-xs rounded-full transition-colors ${
        active
          ? 'bg-blue-600 text-white'
          : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
      }`}
    >
      {label}
    </button>
  )
}

function SearchResults({
  results,
  store,
}: {
  results: SearchResultItem[]
  store: string
}) {
  if (results.length === 0) {
    return <p className="text-gray-500 text-sm">No matches found.</p>
  }

  return (
    <div className="bg-white rounded-lg border divide-y">
      {results.map((r, i) => (
        <Link
          key={`${r.name}-${i}`}
          to={`/stores/${store}/memories/${encodeURIComponent(r.name)}`}
          className="block px-4 py-3 hover:bg-gray-50 transition-colors"
        >
          <div className="flex items-center gap-2">
            <span className="text-xs px-1.5 py-0.5 rounded bg-gray-100 text-gray-500">
              {r.type}
            </span>
            <span className="font-mono text-sm text-blue-600">{r.name}</span>
            <span className="text-xs text-gray-400 ml-auto">
              score: {r.score.toFixed(0)}
            </span>
          </div>
          {r.description && (
            <p className="text-xs text-gray-500 mt-1 truncate">{r.description}</p>
          )}
        </Link>
      ))}
    </div>
  )
}
