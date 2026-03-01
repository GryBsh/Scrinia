import { Link } from 'react-router'
import { FileText } from 'lucide-react'
import type { MemoryListItem } from '../api/types'

function formatBytes(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  if (bytes < 1048576) return `${(bytes / 1024).toFixed(1)} KB`
  return `${(bytes / 1048576).toFixed(1)} MB`
}

function formatDate(iso: string): string {
  return new Date(iso).toLocaleDateString(undefined, {
    month: 'short',
    day: 'numeric',
    hour: '2-digit',
    minute: '2-digit',
  })
}

export default function MemoryList({
  memories,
  store,
}: {
  memories: MemoryListItem[]
  store: string
}) {
  if (memories.length === 0) {
    return (
      <div className="text-center py-12 text-gray-500">
        <FileText className="w-12 h-12 mx-auto mb-3 opacity-40" />
        <p>No memories found.</p>
      </div>
    )
  }

  return (
    <div className="overflow-x-auto">
      <table className="w-full text-sm">
        <thead>
          <tr className="border-b text-left text-gray-500">
            <th className="px-4 py-2 font-medium">Name</th>
            <th className="px-4 py-2 font-medium">Scope</th>
            <th className="px-4 py-2 font-medium text-right">Chunks</th>
            <th className="px-4 py-2 font-medium text-right">Size</th>
            <th className="px-4 py-2 font-medium">Created</th>
            <th className="px-4 py-2 font-medium">Description</th>
          </tr>
        </thead>
        <tbody>
          {memories.map((m) => (
            <tr key={m.qualifiedName} className="border-b hover:bg-gray-50 transition-colors">
              <td className="px-4 py-2">
                <Link
                  to={`/stores/${store}/memories/${encodeURIComponent(m.qualifiedName)}`}
                  className="text-blue-600 hover:text-blue-800 font-mono text-xs"
                >
                  {m.qualifiedName}
                </Link>
              </td>
              <td className="px-4 py-2">
                <span className="inline-block px-2 py-0.5 rounded-full text-xs bg-gray-100 text-gray-600">
                  {m.scope}
                </span>
              </td>
              <td className="px-4 py-2 text-right font-mono">{m.chunkCount}</td>
              <td className="px-4 py-2 text-right font-mono">{formatBytes(m.originalBytes)}</td>
              <td className="px-4 py-2 text-gray-500 text-xs">{formatDate(m.createdAt)}</td>
              <td className="px-4 py-2 text-gray-600 text-xs max-w-xs truncate">
                {m.description}
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  )
}
