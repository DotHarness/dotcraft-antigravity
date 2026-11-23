import type { DesktopPluginActivate } from '@dotcraft/plugin'
import { LimitNotice } from './LimitNotice'
import { antigravityProviderId, startUsageFeed } from './usage'
import './styles/index.css'

export const activate: DesktopPluginActivate = (host) => {
  const feed = startUsageFeed(host)
  host.effect(() => feed.dispose)

  host.ui.add('composer.before', ({ context }) => {
    const providerId = antigravityProviderId(context)
    return providerId && !context.minimalChrome ? <LimitNotice host={host} feed={feed} providerId={providerId} /> : null
  })
}
