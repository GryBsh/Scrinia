import { useState, useEffect } from 'react'
import { Routes, Route, Navigate } from 'react-router'
import { hasToken } from './api/client'
import Layout from './components/Layout'
import LoginPage from './pages/LoginPage'
import DashboardPage from './pages/DashboardPage'
import MemoryBrowserPage from './pages/MemoryBrowserPage'
import MemoryDetailPage from './pages/MemoryDetailPage'
import KeyManagementPage from './pages/KeyManagementPage'
import SettingsPage from './pages/SettingsPage'

function ProtectedRoute({ children }: { children: React.ReactNode }) {
  const [authed, setAuthed] = useState<boolean | null>(null)

  useEffect(() => {
    setAuthed(hasToken())
  }, [])

  if (authed === null) {
    return (
      <div className="min-h-screen flex items-center justify-center bg-gray-50">
        <div className="text-gray-500 text-sm">Loading...</div>
      </div>
    )
  }

  if (!authed) return <Navigate to="/login" replace />
  return <>{children}</>
}

export default function App() {
  return (
    <Routes>
      <Route path="/login" element={<LoginPage />} />
      <Route
        element={
          <ProtectedRoute>
            <Layout />
          </ProtectedRoute>
        }
      >
        <Route path="/" element={<DashboardPage />} />
        <Route path="/stores/:store" element={<MemoryBrowserPage />} />
        <Route path="/stores/:store/memories/:name" element={<MemoryDetailPage />} />
        <Route path="/keys" element={<KeyManagementPage />} />
        <Route path="/settings" element={<SettingsPage />} />
      </Route>
    </Routes>
  )
}
