import { useParams, useNavigate, Link } from 'react-router'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { showMemory, forgetMemory } from '../api/client'
import MemoryContent from '../components/MemoryContent'
import ChunkViewer from '../components/ChunkViewer'
import { ArrowLeft, Trash2 } from 'lucide-react'

export default function MemoryDetailPage() {
  const { store = 'default', name = '' } = useParams()
  const decodedName = decodeURIComponent(name)
  const navigate = useNavigate()
  const queryClient = useQueryClient()

  const { data, isLoading, error } = useQuery({
    queryKey: ['memory', store, decodedName],
    queryFn: () => showMemory(store, decodedName),
  })

  const deleteMutation = useMutation({
    mutationFn: () => forgetMemory(store, decodedName),
    onSuccess: () => {
      queryClient.invalidateQueries({ queryKey: ['memories', store] })
      navigate(`/stores/${store}`, { replace: true })
    },
  })

  const handleDelete = () => {
    if (confirm(`Delete memory "${decodedName}"?`)) {
      deleteMutation.mutate()
    }
  }

  return (
    <div className="p-6 max-w-4xl mx-auto">
      {/* Header */}
      <div className="flex items-center justify-between mb-6">
        <div className="flex items-center gap-3">
          <Link
            to={`/stores/${store}`}
            className="p-1.5 rounded hover:bg-gray-100 transition-colors"
          >
            <ArrowLeft className="w-5 h-5 text-gray-500" />
          </Link>
          <div>
            <h2 className="text-lg font-semibold font-mono">{decodedName}</h2>
            <p className="text-sm text-gray-500">Store: {store}</p>
          </div>
        </div>

        <button
          onClick={handleDelete}
          disabled={deleteMutation.isPending}
          className="flex items-center gap-1.5 px-3 py-1.5 text-sm text-red-600 hover:bg-red-50 rounded-lg transition-colors"
        >
          <Trash2 className="w-4 h-4" />
          Delete
        </button>
      </div>

      {/* Metadata */}
      {data && (
        <div className="bg-white rounded-lg border p-4 mb-4">
          <div className="grid grid-cols-3 gap-4 text-sm">
            <div>
              <span className="text-gray-500">Chunks</span>
              <p className="font-mono">{data.chunkCount}</p>
            </div>
            <div>
              <span className="text-gray-500">Original size</span>
              <p className="font-mono">{formatBytes(data.originalBytes)}</p>
            </div>
            <div>
              <span className="text-gray-500">Est. tokens</span>
              <p className="font-mono">~{Math.round(data.originalBytes / 4)}</p>
            </div>
          </div>
        </div>
      )}

      {/* Content */}
      {isLoading && <p className="text-gray-500 text-sm">Loading...</p>}
      {error && <p className="text-red-500 text-sm">Error: {(error as Error).message}</p>}
      {deleteMutation.error && (
        <p className="text-red-500 text-sm mb-4">
          Delete failed: {(deleteMutation.error as Error).message}
        </p>
      )}

      {data && (
        <div className="bg-white rounded-lg border p-4">
          <h3 className="text-sm font-medium text-gray-500 mb-3">Content</h3>
          {data.chunkCount > 1 ? (
            <ChunkViewer store={store} name={decodedName} totalChunks={data.chunkCount} />
          ) : (
            <MemoryContent content={data.content} />
          )}
        </div>
      )}
    </div>
  )
}

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1048576).toFixed(1)} MB`
}
