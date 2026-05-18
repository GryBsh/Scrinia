import { useState } from 'react'
import type { ReactNode } from 'react'
import { Navigate } from 'react-router'
import { hasToken } from '../api/client'

export default function ProtectedRoute({ children }: { children: ReactNode }) {
  const [authed] = useState<boolean>(() => hasToken())

  if (!authed) return <Navigate to="/login" replace />
  return <>{children}</>
}
