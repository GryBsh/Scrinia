import { createBrowserRouter } from 'react-router'
import Layout from './components/Layout'
import ProtectedRoute from './components/ProtectedRoute'
import LoginPage from './pages/LoginPage'
import DashboardPage from './pages/DashboardPage'
import MemoryBrowserPage from './pages/MemoryBrowserPage'
import MemoryDetailPage from './pages/MemoryDetailPage'
import KeyManagementPage from './pages/KeyManagementPage'
import SettingsPage from './pages/SettingsPage'
import AgentChatPage from './pages/AgentChatPage'

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
      { path: '/keys', element: <KeyManagementPage /> },
      { path: '/settings', element: <SettingsPage /> },
    ],
  },
])
