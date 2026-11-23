import type { DesktopPluginComposerSurfaceContext, DesktopPluginHost } from '@dotcraft/plugin'

const BACKEND_ID = 'antigravity'
const REFRESH_INTERVAL_MS = 10 * 60_000

export interface AntigravityUsage {
  readonly soonestResetAtMs: number | null
  readonly limitReachedKind: string | null
  readonly validationUrl: string | null
}

export function antigravityProviderId({ provider }: DesktopPluginComposerSurfaceContext): string | null {
  return provider?.backendId === BACKEND_ID ? provider.id : null
}

export interface UsageFeed {
  get(providerId: string): AntigravityUsage | null
  watch(providerId: string, listener: () => void): () => void
}

interface RawUsageResult {
  available?: boolean
  limitReachedKind?: string | null
  primary?: { resetAt: string } | null
  values?: Record<string, number> | null
  labels?: Record<string, string> | null
}

function parseUsageResult(result: RawUsageResult): AntigravityUsage | null {
  if (!result.available) return null
  const values = result.values ?? {}
  const labels = result.labels ?? {}
  const order = (labels.pool_order ?? '')
    .split(',')
    .map((item) => item.trim())
    .filter(Boolean)

  const resetTimes: number[] = []
  for (const key of order) {
    const resetEpoch = values[`pool.${key}.reset_epoch_ms`]
    if (typeof resetEpoch === 'number' && Number.isFinite(resetEpoch)) resetTimes.push(resetEpoch)
  }

  if (resetTimes.length === 0 && result.primary) {
    const resetMs = Date.parse(result.primary.resetAt)
    if (Number.isFinite(resetMs)) resetTimes.push(resetMs)
  }

  const validationUrl = typeof labels.validation_url === 'string' && labels.validation_url.startsWith('https://accounts.google.com/')
    ? labels.validation_url
    : null

  return {
    soonestResetAtMs: resetTimes.length > 0 ? Math.min(...resetTimes) : null,
    limitReachedKind: result.limitReachedKind ?? null,
    validationUrl
  }
}

export function startUsageFeed(host: DesktopPluginHost): UsageFeed & { dispose(): void } {
  const usage = new Map<string, AntigravityUsage | null>()
  const listeners = new Map<string, Set<() => void>>()
  const pending = new Set<string>()

  async function refresh(providerId: string): Promise<void> {
    if (pending.has(providerId)) return
    pending.add(providerId)
    try {
      const result = await host.appServer.request('provider/auth/usage', { providerId }, 30_000) as RawUsageResult
      usage.set(providerId, parseUsageResult(result))
      for (const listener of listeners.get(providerId) ?? []) listener()
    } catch {
      return
    } finally {
      pending.delete(providerId)
    }
  }

  const refreshWatched = (): void => {
    for (const providerId of listeners.keys()) void refresh(providerId)
  }
  const stops = [
    host.appServer.onNotification('turn/completed', refreshWatched),
    host.appServer.onNotification('turn/failed', refreshWatched)
  ]
  const timer = setInterval(refreshWatched, REFRESH_INTERVAL_MS)

  return {
    get: (providerId) => usage.get(providerId) ?? null,
    watch(providerId, listener) {
      const set = listeners.get(providerId) ?? new Set()
      listeners.set(providerId, set)
      set.add(listener)
      if (set.size === 1) void refresh(providerId)
      return () => {
        set.delete(listener)
        if (set.size === 0) listeners.delete(providerId)
      }
    },
    dispose() {
      clearInterval(timer)
      for (const stop of stops) stop()
      listeners.clear()
    }
  }
}
