import { useState } from 'react'
import { useQuery, useMutation, useQueryClient } from '@tanstack/react-query'
import { listKeys, createKey, revokeKey } from '../api/client'
import { Plus, Trash2, Copy, Check } from 'lucide-react'
import type { CreateKeyRequest } from '../api/types'

export default function KeyManagementPage() {
  const queryClient = useQueryClient()
  const [showCreate, setShowCreate] = useState(false)
  const [copiedId, setCopiedId] = useState<string | null>(null)
  const [newKey, setNewKey] = useState<string | null>(null)

  const { data: keys, isLoading, error } = useQuery({
    queryKey: ['keys'],
    queryFn: listKeys,
  })

  const revokeMutation = useMutation({
    mutationFn: revokeKey,
    onSuccess: () => queryClient.invalidateQueries({ queryKey: ['keys'] }),
  })

  const handleCopy = (text: string, id: string) => {
    navigator.clipboard.writeText(text)
    setCopiedId(id)
    setTimeout(() => setCopiedId(null), 2000)
  }

  return (
    <div className="p-6 max-w-4xl mx-auto">
      <div className="flex items-center justify-between mb-6">
        <h2 className="text-xl font-semibold">API Keys</h2>
        <button
          onClick={() => setShowCreate(true)}
          className="flex items-center gap-1.5 px-3 py-2 bg-blue-600 text-white text-sm rounded-lg hover:bg-blue-700 transition-colors"
        >
          <Plus className="w-4 h-4" />
          Create key
        </button>
      </div>

      {/* Newly created key banner */}
      {newKey && (
        <div className="bg-green-50 border border-green-200 rounded-lg p-4 mb-4">
          <p className="text-sm font-medium text-green-800 mb-1">Key created successfully</p>
          <p className="text-xs text-green-600 mb-2">Copy this key now. It won't be shown again.</p>
          <div className="flex items-center gap-2">
            <code className="flex-1 px-3 py-2 bg-white border rounded text-sm font-mono break-all">
              {newKey}
            </code>
            <button
              onClick={() => handleCopy(newKey, 'new-key')}
              className="p-2 hover:bg-green-100 rounded transition-colors"
            >
              {copiedId === 'new-key' ? (
                <Check className="w-4 h-4 text-green-600" />
              ) : (
                <Copy className="w-4 h-4 text-green-600" />
              )}
            </button>
          </div>
        </div>
      )}

      {/* Create key form */}
      {showCreate && (
        <CreateKeyForm
          onCreated={(rawKey) => {
            setNewKey(rawKey)
            setShowCreate(false)
            queryClient.invalidateQueries({ queryKey: ['keys'] })
          }}
          onCancel={() => setShowCreate(false)}
        />
      )}

      {/* Key list */}
      <div className="bg-white rounded-lg border">
        {isLoading && <p className="p-4 text-gray-500 text-sm">Loading...</p>}
        {error && (
          <p className="p-4 text-red-500 text-sm">
            Error: {(error as Error).message}
          </p>
        )}
        {keys && keys.length === 0 && (
          <p className="p-8 text-center text-gray-400 text-sm">No API keys found.</p>
        )}
        {keys && keys.length > 0 && (
          <table className="w-full text-sm">
            <thead>
              <tr className="border-b text-left text-gray-500">
                <th className="px-4 py-2 font-medium">User</th>
                <th className="px-4 py-2 font-medium">Label</th>
                <th className="px-4 py-2 font-medium">Stores</th>
                <th className="px-4 py-2 font-medium">Permissions</th>
                <th className="px-4 py-2 font-medium">Created</th>
                <th className="px-4 py-2 font-medium">Status</th>
                <th className="px-4 py-2"></th>
              </tr>
            </thead>
            <tbody>
              {keys.map((k) => (
                <tr key={k.id} className="border-b hover:bg-gray-50">
                  <td className="px-4 py-2 font-mono text-xs">{k.userId}</td>
                  <td className="px-4 py-2 text-gray-600">{k.label ?? '-'}</td>
                  <td className="px-4 py-2">
                    <div className="flex gap-1 flex-wrap">
                      {k.stores.map((s) => (
                        <span key={s} className="px-1.5 py-0.5 bg-blue-50 text-blue-700 text-xs rounded">
                          {s}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-2">
                    <div className="flex gap-1 flex-wrap">
                      {k.permissions.map((p) => (
                        <span key={p} className="px-1.5 py-0.5 bg-purple-50 text-purple-700 text-xs rounded">
                          {p}
                        </span>
                      ))}
                    </div>
                  </td>
                  <td className="px-4 py-2 text-gray-500 text-xs">
                    {new Date(k.createdAt).toLocaleDateString()}
                  </td>
                  <td className="px-4 py-2">
                    <span
                      className={`text-xs px-2 py-0.5 rounded-full ${
                        k.revoked
                          ? 'bg-red-50 text-red-600'
                          : 'bg-green-50 text-green-600'
                      }`}
                    >
                      {k.revoked ? 'Revoked' : 'Active'}
                    </span>
                  </td>
                  <td className="px-4 py-2">
                    {!k.revoked && (
                      <button
                        onClick={() => {
                          if (confirm(`Revoke key for "${k.userId}"?`))
                            revokeMutation.mutate(k.id)
                        }}
                        className="p-1 text-gray-400 hover:text-red-500 transition-colors"
                      >
                        <Trash2 className="w-4 h-4" />
                      </button>
                    )}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </div>
  )
}

function CreateKeyForm({
  onCreated,
  onCancel,
}: {
  onCreated: (rawKey: string) => void
  onCancel: () => void
}) {
  const [userId, setUserId] = useState('')
  const [stores, setStores] = useState('default')
  const [permissions, setPermissions] = useState('')
  const [label, setLabel] = useState('')

  const mutation = useMutation({
    mutationFn: (req: CreateKeyRequest) => createKey(req),
    onSuccess: (data) => onCreated(data.rawKey),
  })

  const handleSubmit = (e: React.FormEvent) => {
    e.preventDefault()
    mutation.mutate({
      userId: userId.trim(),
      stores: stores.split(',').map((s) => s.trim()).filter(Boolean),
      permissions: permissions ? permissions.split(',').map((s) => s.trim()).filter(Boolean) : undefined,
      label: label.trim() || undefined,
    })
  }

  return (
    <form onSubmit={handleSubmit} className="bg-white rounded-lg border p-4 mb-4 space-y-3">
      <h3 className="font-medium text-sm">Create API Key</h3>

      <div className="grid grid-cols-2 gap-3">
        <div>
          <label className="text-xs text-gray-500">User ID *</label>
          <input
            type="text"
            value={userId}
            onChange={(e) => setUserId(e.target.value)}
            required
            className="w-full mt-1 px-3 py-1.5 border rounded text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>
        <div>
          <label className="text-xs text-gray-500">Label</label>
          <input
            type="text"
            value={label}
            onChange={(e) => setLabel(e.target.value)}
            className="w-full mt-1 px-3 py-1.5 border rounded text-sm focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>
        <div>
          <label className="text-xs text-gray-500">Stores (comma-sep) *</label>
          <input
            type="text"
            value={stores}
            onChange={(e) => setStores(e.target.value)}
            required
            className="w-full mt-1 px-3 py-1.5 border rounded text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>
        <div>
          <label className="text-xs text-gray-500">Permissions (comma-sep)</label>
          <input
            type="text"
            value={permissions}
            onChange={(e) => setPermissions(e.target.value)}
            placeholder="manage_keys"
            className="w-full mt-1 px-3 py-1.5 border rounded text-sm font-mono focus:outline-none focus:ring-2 focus:ring-blue-500"
          />
        </div>
      </div>

      {mutation.error && (
        <p className="text-red-500 text-xs">{(mutation.error as Error).message}</p>
      )}

      <div className="flex gap-2 justify-end">
        <button
          type="button"
          onClick={onCancel}
          className="px-3 py-1.5 text-sm text-gray-600 hover:bg-gray-100 rounded transition-colors"
        >
          Cancel
        </button>
        <button
          type="submit"
          disabled={mutation.isPending}
          className="px-3 py-1.5 text-sm bg-blue-600 text-white rounded hover:bg-blue-700 transition-colors disabled:opacity-50"
        >
          {mutation.isPending ? 'Creating...' : 'Create'}
        </button>
      </div>
    </form>
  )
}
