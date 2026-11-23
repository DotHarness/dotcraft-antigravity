import { ActionTooltip, type DesktopPluginHost } from '@dotcraft/plugin'
import { useEffect, useRef, useState, useSyncExternalStore, type JSX } from 'react'
import { formatDuration, stringsFor, type Strings } from './i18n'
import type { AntigravityUsage, QuotaPool, UsageFeed } from './usage'

export function AntigravityMark({ size = 14 }: { size?: number }): JSX.Element {
  return (
    <svg width={size} height={size} viewBox="0 0 64 64" aria-hidden="true" className="antigravity-mark">
      <path fill="currentColor" d="M32 8L54 52H42.5L32 30.5L21.5 52H10L32 8Z" />
      <circle cx="32" cy="46" r="5" fill="currentColor" />
    </svg>
  )
}

export function useUsage(feed: UsageFeed, providerId: string): AntigravityUsage | null {
  const [, setRevision] = useState(0)
  useEffect(() => feed.watch(providerId, () => setRevision((value) => value + 1)), [feed, providerId])
  return feed.get(providerId)
}

export function useNow(intervalMs: number): number {
  return useSyncExternalStore(
    (notify) => {
      const id = setInterval(notify, intervalMs)
      return () => clearInterval(id)
    },
    () => Math.floor(Date.now() / intervalMs) * intervalMs
  )
}

export function limitMessage(usage: AntigravityUsage, now: number, strings: Strings): string | null {
  if (usage.limitReachedKind === 'validation_required') return strings.validationRequired
  if (usage.limitReachedKind === 'credits_exhausted') return strings.creditsExhausted
  if (usage.limitReachedKind === 'quota_reached') {
    const soonest = usage.pools.map((pool) => pool.resetAtMs).filter((v): v is number => v !== null).sort((a, b) => a - b)[0]
    const resets = soonest != null ? formatDuration(Math.max(0, soonest - now), strings) : null
    return strings.quotaReached(resets)
  }
  return usage.limitReachedKind ? strings.quotaReached(null) : null
}

function resetLabel(pool: QuotaPool, now: number, strings: Strings): string | null {
  if (pool.resetAtMs === null) return null
  return strings.resetsIn(formatDuration(Math.max(0, pool.resetAtMs - now), strings))
}

export function Subscription({ host, feed, providerId }: { host: DesktopPluginHost; feed: UsageFeed; providerId: string }): JSX.Element | null {
  const usage = useUsage(feed, providerId)
  const now = useNow(30_000)
  const strings = stringsFor(host.environment.locale)
  const [open, setOpen] = useState(false)
  const root = useRef<HTMLDivElement | null>(null)

  useEffect(() => {
    if (!open) return
    const close = (event: Event): void => {
      if (event instanceof KeyboardEvent ? event.key === 'Escape' : !root.current?.contains(event.target as Node)) setOpen(false)
    }
    window.addEventListener('pointerdown', close, true)
    window.addEventListener('keydown', close)
    return () => {
      window.removeEventListener('pointerdown', close, true)
      window.removeEventListener('keydown', close)
    }
  }, [open])

  if (!usage) return null
  const left = usage.pools.length > 0 ? Math.min(...usage.pools.map((pool) => pool.remainingPercent)) : 100
  const blocked = usage.limitReachedKind !== null
  const tone = blocked ? 'error' : toneFor(left)
  const label = [strings.title, ...usage.pools.map((pool) => `${pool.name} ${strings.left(pool.remainingPercent)}`)].join(' – ')
  const message = limitMessage(usage, now, strings)
  const tooltip = usage.pools.map((pool) => `${pool.name} ${pool.remainingPercent}%`).join(', ') || strings.unavailable

  return (
    <div ref={root} className="antigravity-usage">
      <ActionTooltip label={open ? '' : tooltip}>
        <button
          type="button"
          className="dc-composer-pill antigravity-usage-pill"
          data-tone={tone}
          aria-label={label}
          aria-haspopup="dialog"
          aria-expanded={open}
          onClick={() => setOpen((value) => !value)}
        >
          <AntigravityMark />
          <span className="antigravity-usage-track" aria-hidden="true">
            <span className="antigravity-usage-fill" style={{ width: `${blocked ? 0 : left}%` }} />
          </span>
        </button>
      </ActionTooltip>
      {open ? (
        <div className="antigravity-usage-popover" role="dialog" aria-label={strings.title}>
          <div className="antigravity-usage-header">
            <span className="antigravity-usage-title">
              {strings.title}
              {usage.planType ? <span className="antigravity-usage-plan"> {usage.planType}</span> : null}
            </span>
            {usage.fetchedAt ? (
              <span className="antigravity-usage-dim">
                {strings.fetched(new Date(usage.fetchedAt).toLocaleTimeString(host.environment.locale, { hour: '2-digit', minute: '2-digit' }))}
              </span>
            ) : null}
          </div>
          <div className="antigravity-usage-divider" />
          {usage.pools.length === 0 && !message ? <div className="antigravity-usage-dim">{strings.unavailable}</div> : null}
          {usage.pools.map((pool) => {
            const reset = resetLabel(pool, now, strings)
            return (
              <div key={pool.key} className="antigravity-usage-row">
                <span className="antigravity-usage-label">{pool.name}</span>
                <span className="antigravity-usage-bar" data-tone={toneFor(pool.remainingPercent)} aria-hidden="true">
                  <span className="antigravity-usage-fill" style={{ width: `${pool.remainingPercent}%` }} />
                </span>
                <div className="antigravity-usage-row-foot" data-tone={toneFor(pool.remainingPercent)}>
                  <span className="antigravity-usage-percent">{strings.left(pool.remainingPercent)}</span>
                  {reset ? <span>{reset}</span> : null}
                </div>
              </div>
            )
          })}
          {usage.creditsBalance ? (
            <div className="antigravity-usage-dim">{strings.credits(usage.creditsBalance)}</div>
          ) : null}
          {message ? (
            <div className="antigravity-usage-alert" role="alert">
              <span>{message}</span>
              {usage.validationUrl ? (
                <a className="antigravity-usage-link" href={usage.validationUrl} target="_blank" rel="noreferrer">
                  {strings.verifyAccount}
                </a>
              ) : null}
            </div>
          ) : null}
        </div>
      ) : null}
    </div>
  )
}

function toneFor(left: number): 'normal' | 'warning' | 'error' {
  return left < 20 ? 'error' : left < 40 ? 'warning' : 'normal'
}
