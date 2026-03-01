import { useState } from 'react'
import { useQuery } from '@tanstack/react-query'
import { getChunk } from '../api/client'
import MemoryContent from './MemoryContent'

export default function ChunkViewer({
  store,
  name,
  totalChunks,
}: {
  store: string
  name: string
  totalChunks: number
}) {
  const [activeChunk, setActiveChunk] = useState(1)

  const { data, isLoading, error } = useQuery({
    queryKey: ['chunk', store, name, activeChunk],
    queryFn: () => getChunk(store, name, activeChunk),
  })

  return (
    <div>
      <div className="flex gap-1 mb-3 flex-wrap">
        {Array.from({ length: totalChunks }, (_, i) => i + 1).map((i) => (
          <button
            key={i}
            onClick={() => setActiveChunk(i)}
            className={`px-3 py-1 text-xs rounded font-mono transition-colors ${
              i === activeChunk
                ? 'bg-blue-600 text-white'
                : 'bg-gray-100 text-gray-600 hover:bg-gray-200'
            }`}
          >
            Chunk {i}
          </button>
        ))}
      </div>

      {isLoading && <p className="text-gray-500 text-sm">Loading chunk {activeChunk}...</p>}
      {error && <p className="text-red-500 text-sm">Error: {(error as Error).message}</p>}
      {data && <MemoryContent content={data.content} />}
    </div>
  )
}
