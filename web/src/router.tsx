import React, { useState, useEffect, Suspense } from 'react'
import { createBrowserRouter, Navigate } from 'react-router'
import { hasToken } from './api/client'
import Layout from './components/Layout'
import LoginPage from './pages/LoginPage'
import DashboardPage from './pages/DashboardPage'
import MemoryBrowserPage from './pages/MemoryBrowserPage'
import MemoryDetailPage from './pages/MemoryDetailPage'
import KeyManagementPage from './pages/KeyManagementPage'
import SettingsPage from './pages/SettingsPage'
import AgentChatPage from './pages/AgentChatPage'
import WorkflowsPage from './pages/WorkflowsPage'
import WorkflowDetailPage from './pages/WorkflowDetailPage'

const WorkflowEditorPage = React.lazy(() => import('./pages/WorkflowEditorPage'))

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

export const router = createBrowserRouter([
  {
    path: '/login',
    element: <LoginPage />,
  },
  {
    element: (
      <ProtectedRoute>
        <Layout />
      </ProtectedRoute>
    ),
    children: [
      { path: '/', element: <DashboardPage /> },
      { path: '/stores/:store', element: <MemoryBrowserPage /> },
      { path: '/stores/:store/memories/:name', element: <MemoryDetailPage /> },
      { path: '/chat', element: <AgentChatPage /> },
      { path: '/workflows', element: <WorkflowsPage /> },
      {
        path: '/workflows/editor',
        element: (
          <Suspense fallback={<div className="p-8 text-gray-500">Loading editor...</div>}>
            <WorkflowEditorPage />
          </Suspense>
        ),
      },
      {
        path: '/workflows/editor/:name',
        element: (
          <Suspense fallback={<div className="p-8 text-gray-500">Loading editor...</div>}>
            <WorkflowEditorPage />
          </Suspense>
        ),
      },
      { path: '/workflows/:goalId', element: <WorkflowDetailPage /> },
      { path: '/keys', element: <KeyManagementPage /> },
      { path: '/settings', element: <SettingsPage /> },
    ],
  },
])
