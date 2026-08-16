import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getHealth, getEmbeddingsStatus, getEmbeddingsSettings, reindexEmbeddings } from '../api/client'
import { RefreshCw } from 'lucide-react'

export default function SettingsPage() {
  const queryClient = useQueryClient()

  const { data: health } = useQuery({
    queryKey: ['health'],
    queryFn: getHealth,
    refetchInterval: 30_000,
  })

  const stores = health?.checks
    ?.filter((c) => c.name.startsWith('store:'))
    .map((c) => c.name.replace('store:', '')) ?? []

  const store = stores.length === 0 || stores.includes('default') ? 'default' : stores[0]!

  const { data: status, isLoading: statusLoading, error: statusError } = useQuery({
    queryKey: ['embeddings-status', store],
    queryFn: () => getEmbeddingsStatus(store),
    refetchInterval: 30_000,
  })

  const { data: settings, isLoading: settingsLoading, error: settingsError } = useQuery({
    queryKey: ['embeddings-settings', store],
    queryFn: () => getEmbeddingsSettings(store),
  })

  const reindexMutation = useMutation({
    mutationFn: () => reindexEmbeddings(store),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['embeddings-status', store] })
    },
  })

  const isLoading = statusLoading || settingsLoading
  const error = statusError || settingsError
  const pluginNotInstalled = error && (
    (error as Error).message.includes('endpoint may not exist') ||
    (error as Error).message.includes('HTTP 404')
  )

  return (
    <div className="p-6 max-w-4xl mx-auto">
      <h2 className="text-xl font-semibold mb-6">Settings</h2>

      <h3 className="text-lg font-medium mb-4">Embeddings</h3>

      {isLoading && <p className="text-gray-500 text-sm">Loading...</p>}
      {pluginNotInstalled && (
        <div className="bg-gray-50 border rounded-lg p-4 mb-4">
          <p className="text-gray-600 text-sm">
            Embeddings plugin is not installed. Semantic search uses built-in BM25 scoring.
          </p>
          <p className="text-gray-400 text-xs mt-1">
            Install the embeddings plugin to enable vector-based semantic search.
          </p>
        </div>
      )}
      {error && !pluginNotInstalled && (
        <p className="text-red-500 text-sm mb-4">
          Error: {(error as Error).message}
        </p>
      )}

      {/* Status card */}
      {status && (
        <div className="bg-white rounded-lg border mb-6">
          <div className="px-4 py-3 border-b">
            <h4 className="text-sm font-medium text-gray-700">Status</h4>
          </div>
          <table className="w-full text-sm">
            <tbody>
              <StatusRow label="Provider" value={status.provider} />
              <StatusRow label="Hardware" value={status.hardware} />
              <StatusRow label="Available">
                <span className={`inline-flex items-center gap-1.5 text-xs px-2 py-0.5 rounded-full ${
                  status.available
                    ? 'bg-green-50 text-green-600'
                    : 'bg-red-50 text-red-600'
                }`}>
                  <span className={`w-1.5 h-1.5 rounded-full ${
                    status.available ? 'bg-green-500' : 'bg-red-500'
                  }`} />
                  {status.available ? 'Yes' : 'No'}
                </span>
              </StatusRow>
              <StatusRow label="Dimensions" value={String(status.dimensions)} />
              <StatusRow label="Vectors" value={status.vectorCount.toLocaleString()} />
              {settings && (
                <StatusRow label="Semantic Weight" value={String(settings.semanticWeight)} />
              )}
            </tbody>
          </table>
          <p className="text-gray-500 text-xs px-4 pb-3">
            Embedding settings are read from configuration. Change them with{' '}
            <code>scri config Scrinia:Embeddings:*</code> and restart the server.
          </p>
        </div>
      )}

      {/* Actions */}
      <div className="bg-white rounded-lg border">
        <div className="px-4 py-3 border-b">
          <h4 className="text-sm font-medium text-gray-700">Actions</h4>
        </div>
        <div className="p-4">
          <div className="flex items-center justify-between">
            <div>
              <p className="text-sm font-medium">Reindex All Memories</p>
              <p className="text-xs text-gray-500">Re-embeds all memories in the current store.</p>
            </div>
            <button
              onClick={() => reindexMutation.mutate()}
              disabled={reindexMutation.isPending}
              className="flex items-center gap-1.5 px-3 py-2 bg-gray-100 text-gray-700 text-sm rounded-lg hover:bg-gray-200 transition-colors disabled:opacity-50"
            >
              <RefreshCw className={`w-4 h-4 ${reindexMutation.isPending ? 'animate-spin' : ''}`} />
              {reindexMutation.isPending ? 'Reindexing...' : 'Reindex'}
            </button>
          </div>
          {reindexMutation.error && (
            <p className="text-red-500 text-xs mt-2">{(reindexMutation.error as Error).message}</p>
          )}
          {reindexMutation.isSuccess && (
            <p className="text-green-600 text-xs mt-2">
              {(reindexMutation.data as { message: string }).message}
            </p>
          )}
        </div>
      </div>
    </div>
  )
}

function StatusRow({ label, value, children }: { label: string; value?: string; children?: React.ReactNode }) {
  return (
    <tr className="border-b last:border-b-0">
      <td className="px-4 py-2 text-gray-500 font-medium w-36">{label}</td>
      <td className="px-4 py-2">{children ?? value}</td>
    </tr>
  )
}
