import { Outlet, Link, useLocation } from 'react-router'
import { useQuery } from '@tanstack/react-query'
import { Database, Key, LayoutDashboard, LogOut, Heart, Settings } from 'lucide-react'
import { getHealth, clearToken } from '../api/client'

export default function Layout() {
  const location = useLocation()
  const { data: health } = useQuery({
    queryKey: ['health'],
    queryFn: getHealth,
    refetchInterval: 30_000,
  })

  const isActive = (path: string) =>
    location.pathname === path || location.pathname.startsWith(path + '/')

  return (
    <div className="flex h-screen bg-gray-50 text-gray-900">
      {/* Sidebar */}
      <nav className="w-56 bg-gray-900 text-gray-100 flex flex-col">
        <div className="p-4 border-b border-gray-700">
          <h1 className="text-lg font-bold tracking-tight">Scrinium</h1>
          <div className="flex items-center gap-1.5 mt-1 text-xs text-gray-400">
            <Heart className="w-3 h-3" />
            <span className={health?.status === 'ok' ? 'text-green-400' : 'text-red-400'}>
              {health?.status ?? 'checking...'}
            </span>
          </div>
        </div>

        <div className="flex-1 overflow-y-auto p-2 space-y-1">
          <NavLink to="/" icon={<LayoutDashboard className="w-4 h-4" />} active={location.pathname === '/'}>
            Dashboard
          </NavLink>
          <NavLink to="/stores/default" icon={<Database className="w-4 h-4" />} active={isActive('/stores')}>
            Memories
          </NavLink>
          <NavLink to="/keys" icon={<Key className="w-4 h-4" />} active={isActive('/keys')}>
            API Keys
          </NavLink>
          <NavLink to="/settings" icon={<Settings className="w-4 h-4" />} active={isActive('/settings')}>
            Settings
          </NavLink>
        </div>

        <div className="p-2 border-t border-gray-700">
          <button
            onClick={() => { clearToken(); window.location.href = '/login' }}
            className="flex items-center gap-2 w-full px-3 py-2 text-sm text-gray-400 hover:text-white hover:bg-gray-800 rounded transition-colors"
          >
            <LogOut className="w-4 h-4" />
            Sign out
          </button>
        </div>
      </nav>

      {/* Main content */}
      <main className="flex-1 overflow-y-auto">
        <Outlet />
      </main>
    </div>
  )
}

function NavLink({
  to,
  icon,
  active,
  children,
}: {
  to: string
  icon: React.ReactNode
  active: boolean
  children: React.ReactNode
}) {
  return (
    <Link
      to={to}
      className={`flex items-center gap-2 px-3 py-2 text-sm rounded transition-colors ${
        active
          ? 'bg-gray-700 text-white'
          : 'text-gray-300 hover:text-white hover:bg-gray-800'
      }`}
    >
      {icon}
      {children}
    </Link>
  )
}
