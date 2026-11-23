import type { DesktopPluginHost } from '@dotcraft/plugin'
import { useEffect, useState, useSyncExternalStore, type JSX } from 'react'
import { formatDuration, stringsFor, type Strings } from './i18n'
import type { AntigravityUsage, UsageFeed } from './usage'

function AntigravityMark({ size = 14 }: { size?: number }): JSX.Element {
  return (
    <svg width={size} height={size} viewBox="0 0 112 112" fill="none" aria-hidden="true" className="antigravity-mark">
      <g transform="translate(0.5 -1)">
        <path
          fill="currentColor"
          d="M89.6992 93.695C94.3659 97.195 101.366 94.8617 94.9492 88.445C75.6992 69.7783 79.7825 18.445 55.8659 18.445C31.9492 18.445 36.0325 69.7783 16.7825 88.445C9.78251 95.445 17.3658 97.195 22.0325 93.695C40.1159 81.445 38.9492 59.8617 55.8659 59.8617C72.7825 59.8617 71.6159 81.445 89.6992 93.695Z"
        />
      </g>
    </svg>
  )
}

function useUsage(feed: UsageFeed, providerId: string): AntigravityUsage | null {
  const [, setRevision] = useState(0)
  useEffect(() => feed.watch(providerId, () => setRevision((value) => value + 1)), [feed, providerId])
  return feed.get(providerId)
}

function useNow(intervalMs: number): number {
  return useSyncExternalStore(
    (notify) => {
      const id = setInterval(notify, intervalMs)
      return () => clearInterval(id)
    },
    () => Math.floor(Date.now() / intervalMs) * intervalMs
  )
}

function limitMessage(usage: AntigravityUsage, now: number, strings: Strings): string | null {
  if (usage.limitReachedKind === 'validation_required') return strings.validationRequired
  if (usage.limitReachedKind === 'credits_exhausted') return strings.creditsExhausted
  if (usage.limitReachedKind === 'quota_reached') {
    const resets = usage.soonestResetAtMs != null ? formatDuration(Math.max(0, usage.soonestResetAtMs - now), strings) : null
    return strings.quotaReached(resets)
  }
  return usage.limitReachedKind ? strings.quotaReached(null) : null
}

export function LimitNotice({ host, feed, providerId }: { host: DesktopPluginHost; feed: UsageFeed; providerId: string }): JSX.Element | null {
  const usage = useUsage(feed, providerId)
  const now = useNow(30_000)
  const strings = stringsFor(host.environment.locale)
  const message = usage ? limitMessage(usage, now, strings) : null
  if (!message) return null
  return (
    <div className="antigravity-limit-notice" role="status">
      <AntigravityMark />
      <span>{message}</span>
      {usage?.validationUrl ? (
        <a className="antigravity-limit-link" href={usage.validationUrl} target="_blank" rel="noreferrer">
          {strings.verifyAccount}
        </a>
      ) : null}
    </div>
  )
}
