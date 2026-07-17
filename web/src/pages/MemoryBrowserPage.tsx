import { useEffect, useMemo, useState } from 'react'
import { useParams } from 'react-router'
import { useInfiniteQuery, useQuery } from '@tanstack/react-query'
import {
  MEMORY_LIST_PAGE_SIZE,
  listMemories,
  searchMemories,
} from '../api/client'
import MemoryList from '../components/MemoryList'
import SearchBar from '../components/SearchBar'
import type { SearchResultItem } from '../api/types'
import { Link } from 'react-router'

export default function MemoryBrowserPage() {
  const { store = 'default' } = useParams()
  const [searchQuery, setSearchQuery] = useState('')
  const [scopeFilter, setScopeFilter] = useState<string>('')

  const {
    data,
    isLoading,
    error,
    fetchNextPage,
    hasNextPage,
    isFetchingNextPage,
  } = useInfiniteQuery({
    queryKey: ['memories', store, scopeFilter],
    queryFn: ({ pageParam }) =>
      listMemories(store, scopeFilter || undefined, pageParam, MEMORY_LIST_PAGE_SIZE),
    initialPageParam: 0,
    getNextPageParam: (lastPage, allPages) => {
      const loaded = allPages.reduce((n, p) => n + p.memories.length, 0)
      return loaded < lastPage.total ? loaded : undefined
    },
  })

  const memories = useMemo(
    () => data?.pages.flatMap((p) => p.memories) ?? [],
    [data],
  )
  const total = data?.pages[0]?.total ?? 0

  const { data: searchResults } = useQuery({
    queryKey: ['search', store, searchQuery],
    queryFn: () => searchMemories(store, searchQuery),
    enabled: !!searchQuery,
  })

  // Accumulate every scope ever seen across loads, so the tab bar (and the
  // "All" tab in particular) doesn't collapse once the active filter narrows
  // the loaded page down to a single scope. Scopes are added, never removed.
  const [knownScopes, setKnownScopes] = useState<Set<string>>(new Set())
  useEffect(() => {
    // Switching stores invalidates any scopes accumulated for the previous one.
    setKnownScopes(new Set())
  }, [store])
  useEffect(() => {
    setKnownScopes((prev) => {
      let changed = false
      const next = new Set(prev)
      for (const m of memories) {
        if (!next.has(m.scope)) {
          next.add(m.scope)
          changed = true
        }
      }
      return changed ? next : prev
    })
  }, [memories])
  const scopes = useMemo(
    () => [...knownScopes].sort(),
    [knownScopes],
  )

  return (
    <div className="p-6">
      <div className="flex items-center justify-between mb-6">
        <div>
          <h2 className="text-xl font-semibold">Memories</h2>
          <p className="text-sm text-gray-500">
            Store: <span className="font-mono">{store}</span>
            {data && (
              <span className="ml-2">
                ({memories.length} of {total})
              </span>
            )}
          </p>
        </div>
      </div>

      <div className="mb-4">
        <SearchBar onSearch={setSearchQuery} />
      </div>

      {/* Scope filter tabs. "All" is always shown once any scope has been seen,
          so a selected filter can always be cleared. Per-scope tabs only
          appear once we know of more than one scope to choose between. */}
      {scopes.length > 0 && (
        <div className="flex gap-1 mb-4">
          <ScopeTab
            label="All"
            active={scopeFilter === ''}
            onClick={() => setScopeFilter('')}
          />
          {scopes.length > 1 &&
            scopes.map((s) => (
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
        {data && <MemoryList memories={memories} store={store} />}
        {hasNextPage && (
          <div className="border-t p-3 text-center">
            <button
              onClick={() => fetchNextPage()}
              disabled={isFetchingNextPage}
              className="text-sm text-blue-600 hover:text-blue-700 disabled:text-gray-400"
            >
              {isFetchingNextPage
                ? 'Loading…'
                : `Load more (${total - memories.length} remaining)`}
            </button>
          </div>
        )}
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
