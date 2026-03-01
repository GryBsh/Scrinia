import { useState } from 'react'
import { useNavigate } from 'react-router'
import { setToken } from '../api/client'
import { KeyRound } from 'lucide-react'

export default function LoginPage() {
  const [key, setKey] = useState('')
  const [error, setError] = useState('')
  const navigate = useNavigate()

  const handleSubmit = async (e: React.FormEvent) => {
    e.preventDefault()
    if (!key.trim()) return

    // Validate by trying the health endpoint with auth
    setToken(key.trim())
    try {
      const resp = await fetch('/api/v1/keys', {
        headers: { Authorization: `Bearer ${key.trim()}` },
      })
      if (resp.status === 401 || resp.status === 403) {
        setError('Invalid API key.')
        localStorage.removeItem('scrinia-api-key')
        return
      }
    } catch {
      // Network error — proceed anyway, key might still be valid
    }

    navigate('/', { replace: true })
  }

  return (
    <div className="min-h-screen flex items-center justify-center bg-gray-50">
      <div className="w-full max-w-sm p-8">
        <div className="text-center mb-8">
          <div className="inline-flex items-center justify-center w-12 h-12 bg-gray-900 text-white rounded-xl mb-4">
            <KeyRound className="w-6 h-6" />
          </div>
          <h1 className="text-2xl font-bold">Scrinia</h1>
          <p className="text-gray-500 text-sm mt-1">Enter your API key to continue</p>
        </div>

        <form onSubmit={handleSubmit} className="space-y-4">
          <div>
            <input
              type="password"
              value={key}
              onChange={(e) => { setKey(e.target.value); setError('') }}
              placeholder="sk-..."
              autoFocus
              className="w-full px-3 py-2 border rounded-lg text-sm focus:outline-none focus:ring-2 focus:ring-blue-500 focus:border-transparent font-mono"
            />
            {error && <p className="text-red-500 text-xs mt-1">{error}</p>}
          </div>

          <button
            type="submit"
            className="w-full py-2 bg-gray-900 text-white text-sm rounded-lg hover:bg-gray-800 transition-colors"
          >
            Sign in
          </button>
        </form>

      </div>
    </div>
  )
}
