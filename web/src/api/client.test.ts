import { describe, it, expect, vi, afterEach } from 'vitest'
import { validateApiKey, classifyDetailsStatus, classifyReadyStatus } from './client'

afterEach(() => {
  vi.unstubAllGlobals()
})

describe('validateApiKey', () => {
  it('returns "invalid" on 401', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 401 })))
    expect(await validateApiKey('k')).toBe('invalid')
  })

  it('returns "valid" on 200', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response('{}', { status: 200 })))
    expect(await validateApiKey('k')).toBe('valid')
  })

  it('returns "valid" on 403 (valid non-admin key)', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 403 })))
    expect(await validateApiKey('k')).toBe('valid')
  })

  it('returns "unverifiable" on 500', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => new Response(null, { status: 500 })))
    expect(await validateApiKey('k')).toBe('unverifiable')
  })

  it('returns "unverifiable" on network error', async () => {
    vi.stubGlobal('fetch', vi.fn(async () => { throw new Error('network') }))
    expect(await validateApiKey('k')).toBe('unverifiable')
  })
})

describe('classifyDetailsStatus', () => {
  it('401 → redirect', () => expect(classifyDetailsStatus(401)).toBe('redirect'))
  it('403 → fallback', () => expect(classifyDetailsStatus(403)).toBe('fallback'))
  it('200 → use-body', () => expect(classifyDetailsStatus(200)).toBe('use-body'))
  it('503 → use-body (degraded body)', () => expect(classifyDetailsStatus(503)).toBe('use-body'))
  it('500 → error (not masked)', () => expect(classifyDetailsStatus(500)).toBe('error'))
})

describe('classifyReadyStatus', () => {
  it('200 → use-body', () => expect(classifyReadyStatus(200)).toBe('use-body'))
  it('503 → use-body', () => expect(classifyReadyStatus(503)).toBe('use-body'))
  it('502 → error', () => expect(classifyReadyStatus(502)).toBe('error'))
})
