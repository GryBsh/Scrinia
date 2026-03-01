import { useState, useEffect } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { getEmbeddingsStatus, getEmbeddingsSettings, updateEmbeddingsSettings, reindexEmbeddings } from '../api/client'
import { RefreshCw, Save } from 'lucide-react'

export default function SettingsPage() {
  const queryClient = useQueryClient()

  const { data: status, isLoading: statusLoading, error: statusError } = useQuery({
    queryKey: ['embeddings-status'],
    queryFn: getEmbeddingsStatus,
    refetchInterval: 30_000,
  })

  const { data: settings, isLoading: settingsLoading, error: settingsError } = useQuery({
    queryKey: ['embeddings-settings'],
    queryFn: getEmbeddingsSettings,
  })

  const [semanticWeight, setSemanticWeight] = useState('')
  const [maxBatchSize, setMaxBatchSize] = useState('')

  useEffect(() => {
    if (settings) {
      setSemanticWeight(String(settings.semanticWeight))
      setMaxBatchSize(String(settings.maxBatchSize))
    }
  }, [settings])

  const saveMutation = useMutation({
    mutationFn: updateEmbeddingsSettings,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['embeddings-settings'] })
      queryClient.invalidateQueries({ queryKey: ['embeddings-status'] })
    },
  })

  const reindexMutation = useMutation({
    mutationFn: reindexEmbeddings,
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['embeddings-status'] })
    },
  })

  const handleSave = (e: React.FormEvent) => {
    e.preventDefault()
    saveMutation.mutate({
      semanticWeight: parseFloat(semanticWeight),
      maxBatchSize: parseInt(maxBatchSize, 10),
    })
  }

  const isLoading = statusLoading || settingsLoading
  const error = statusError || settingsError

  return (
    <div className="p-6 max-w-4xl mx-auto">
      <h2 className="text-xl font-semibold mb-6">Settings</h2>

      <h3 className="text-lg font-medium mb-4">Embeddings</h3>

      {isLoading && <p className="text-gray-500 text-sm">Loading...</p>}
      {error && (
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
            </tbody>
          </table>
        </div>
      )}

      {/* Settings form */}
      {settings && (
        <form onSubmit={handleSave} className="bg-white rounded-lg border mb-6">
          <div className="px-4 py-3 border-b">
            <h4 className="text-sm font-medium text-gray-700">Configuration</h4>
          </div>
          <div className="p-4 space-y-4">
            <div className="grid grid-cols-2 gap-4">
              <div>
                <label className="text-xs text-gray-500">Semantic Weight (0-200)</label>
                <input
                  type="number"
                  value={semanticWeight}
                  onChange={(e) => setSemanticWeight(e.target.value)}
                  min="0"
                  max="200"
                  step="0.1"
                  className="w-full mt-1 px-3 py-1.5 border rounded text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
              <div>
                <label className="text-xs text-gray-500">Max Batch Size (1-64)</label>
                <input
                  type="number"
                  value={maxBatchSize}
                  onChange={(e) => setMaxBatchSize(e.target.value)}
                  min="1"
                  max="64"
                  step="1"
                  className="w-full mt-1 px-3 py-1.5 border rounded text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
                />
              </div>
            </div>

            {saveMutation.error && (
              <p className="text-red-500 text-xs">{(saveMutation.error as Error).message}</p>
            )}
            {saveMutation.isSuccess && (
              <p className="text-green-600 text-xs">Settings updated.</p>
            )}

            <div className="flex justify-end">
              <button
                type="submit"
                disabled={saveMutation.isPending}
                className="flex items-center gap-1.5 px-3 py-2 bg-blue-600 text-white text-sm rounded-lg hover:bg-blue-700 transition-colors disabled:opacity-50"
              >
                <Save className="w-4 h-4" />
                {saveMutation.isPending ? 'Saving...' : 'Save Settings'}
              </button>
            </div>
          </div>
        </form>
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
