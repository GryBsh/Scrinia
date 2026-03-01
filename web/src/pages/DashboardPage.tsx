import { useQuery } from '@tanstack/react-query'
import { Link } from 'react-router'
import { getHealth, listMemories } from '../api/client'
import { Database, CheckCircle, AlertCircle } from 'lucide-react'

export default function DashboardPage() {
  const { data: health } = useQuery({
    queryKey: ['health'],
    queryFn: getHealth,
    refetchInterval: 30_000,
  })

  // Extract store names from health checks
  const stores = health?.checks
    ?.filter((c) => c.name.startsWith('store:'))
    .map((c) => c.name.replace('store:', '')) ?? []

  return (
    <div className="p-6 max-w-4xl mx-auto">
      <h2 className="text-xl font-semibold mb-6">Dashboard</h2>

      {/* Health status */}
      <div className="bg-white rounded-lg border p-4 mb-6">
        <h3 className="text-sm font-medium text-gray-500 mb-3">System Health</h3>
        <div className="grid grid-cols-2 sm:grid-cols-3 gap-3">
          {health?.checks?.map((check) => (
            <div
              key={check.name}
              className="flex items-center gap-2 text-sm"
            >
              {check.status === 'ok' ? (
                <CheckCircle className="w-4 h-4 text-green-500" />
              ) : (
                <AlertCircle className="w-4 h-4 text-red-500" />
              )}
              <span className="font-mono text-xs">{check.name}</span>
            </div>
          ))}
        </div>
      </div>

      {/* Store cards */}
      <h3 className="text-sm font-medium text-gray-500 mb-3">Stores</h3>
      <div className="grid grid-cols-1 sm:grid-cols-2 gap-4">
        {stores.map((store) => (
          <StoreCard key={store} store={store} />
        ))}
        {stores.length === 0 && (
          <p className="text-gray-400 text-sm">No stores found.</p>
        )}
      </div>
    </div>
  )
}

function StoreCard({ store }: { store: string }) {
  const { data } = useQuery({
    queryKey: ['memories', store],
    queryFn: () => listMemories(store),
  })

  return (
    <Link
      to={`/stores/${store}`}
      className="bg-white rounded-lg border p-4 hover:border-blue-300 hover:shadow-sm transition-all block"
    >
      <div className="flex items-center gap-3">
        <div className="p-2 bg-blue-50 rounded-lg">
          <Database className="w-5 h-5 text-blue-600" />
        </div>
        <div>
          <h4 className="font-medium">{store}</h4>
          <p className="text-sm text-gray-500">
            {data ? `${data.total} memories` : 'Loading...'}
          </p>
        </div>
      </div>
    </Link>
  )
}
