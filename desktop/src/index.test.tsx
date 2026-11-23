import { act, cleanup, render, screen, waitFor } from '@testing-library/react'
import type { ComponentType, ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { activate } from './index'

type Surface = ComponentType<{ context: Record<string, unknown>; children?: ReactNode }>

function setup(usage: Record<string, unknown>) {
  const surfaces = new Map<string, Surface>()
  const notifications = new Map<string, () => void>()
  let current = usage
  const request = vi.fn(async () => current)
  const host = {
    environment: { locale: 'en' },
    effect: (fn: () => unknown) => fn(),
    appServer: {
      request,
      onNotification: (method: string, listener: () => void) => {
        notifications.set(method, listener)
        return () => notifications.delete(method)
      }
    },
    ui: {
      wrap: (name: string, component: Surface) => surfaces.set(name, component),
      add: (name: string, component: Surface) => surfaces.set(name, component)
    }
  }
  void activate(host as never)
  return {
    request,
    hasSurface: (name: string) => surfaces.has(name),
    surface: (name: string) => surfaces.get(name)!,
    notify: (method: string) => notifications.get(method)?.(),
    update: (next: Record<string, unknown>) => { current = next }
  }
}

const context = (backendId: string | null) => ({ minimalChrome: false, provider: { id: 'ag', backendId } })

describe('Antigravity desktop module', () => {
  afterEach(cleanup)

  it('leaves composer.status.subscription to DotCraft and only activates limit notices for antigravity', () => {
    const plugin = setup({ available: true })
    expect(plugin.hasSurface('composer.status.subscription')).toBe(false)

    const Notice = plugin.surface('composer.before')
    render(<Notice context={context('other')} />)
    expect(screen.queryByRole('status')).toBeNull()
    expect(plugin.request).not.toHaveBeenCalled()
  })

  it('refreshes after a turn and renders a verification link when validation is required', async () => {
    const plugin = setup({
      available: true,
      values: { 'pool.gemini4.remaining': 90 },
      labels: { pool_order: 'gemini4', 'pool.gemini4.name': 'Gemini 4' }
    })
    const Notice = plugin.surface('composer.before')
    render(<Notice context={context('antigravity')} />)
    await waitFor(() => expect(plugin.request).toHaveBeenCalledWith('provider/auth/usage', { providerId: 'ag' }, 30_000))
    expect(screen.queryByRole('status')).toBeNull()

    plugin.update({
      available: true,
      limitReachedKind: 'validation_required',
      values: { 'pool.gemini4.remaining': 90 },
      labels: {
        pool_order: 'gemini4',
        'pool.gemini4.name': 'Gemini 4',
        validation_url: 'https://accounts.google.com/verify?session=abc'
      }
    })
    await act(async () => plugin.notify('turn/failed'))

    expect(await screen.findByRole('status')).toBeTruthy()
    const link = screen.getByRole('link', { name: 'Verify account' })
    expect(link.getAttribute('href')).toBe('https://accounts.google.com/verify?session=abc')
    expect(plugin.request).toHaveBeenCalledTimes(2)
  })
})
