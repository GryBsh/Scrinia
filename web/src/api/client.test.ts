import { describe, it, expect, vi, afterEach } from 'vitest'
import { validateApiKey, classifyDetailsStatus, classifyReadyStatus, validateEmbeddingsSettings } from './client'

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

describe('validateEmbeddingsSettings', () => {
  it('accepts valid inputs', () => {
    const r = validateEmbeddingsSettings('50', '16')
    expect(r.values).toEqual({ semanticWeight: 50, maxBatchSize: 16 })
    expect(r.errors).toEqual({})
  })

  it('rejects empty semantic weight', () => {
    const r = validateEmbeddingsSettings('', '16')
    expect(r.values).toBeNull()
    expect(r.errors.semanticWeight).toBeTruthy()
  })

  it('rejects empty batch size', () => {
    const r = validateEmbeddingsSettings('50', '')
    expect(r.values).toBeNull()
    expect(r.errors.maxBatchSize).toBeTruthy()
  })

  it('rejects out-of-range semantic weight', () => {
    expect(validateEmbeddingsSettings('-1', '16').values).toBeNull()
    expect(validateEmbeddingsSettings('250', '16').values).toBeNull()
  })

  it('rejects out-of-range batch size', () => {
    expect(validateEmbeddingsSettings('50', '0').values).toBeNull()
    expect(validateEmbeddingsSettings('50', '65').values).toBeNull()
  })

  it('rejects non-integer batch size', () => {
    const r = validateEmbeddingsSettings('50', '3.5')
    expect(r.values).toBeNull()
    expect(r.errors.maxBatchSize).toBeTruthy()
  })

  it('accepts boundary values', () => {
    expect(validateEmbeddingsSettings('0', '1').values).toEqual({ semanticWeight: 0, maxBatchSize: 1 })
    expect(validateEmbeddingsSettings('200', '64').values).toEqual({ semanticWeight: 200, maxBatchSize: 64 })
  })
})
