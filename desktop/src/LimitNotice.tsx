import type { DesktopPluginHost } from '@dotcraft/plugin'
import type { JSX } from 'react'
import { stringsFor } from './i18n'
import { AntigravityMark, limitMessage, useNow, useUsage } from './Subscription'
import type { UsageFeed } from './usage'

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
