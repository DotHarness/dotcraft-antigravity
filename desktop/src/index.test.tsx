import { act, cleanup, fireEvent, render, screen, waitFor } from '@testing-library/react'
import type { ComponentType, ReactNode } from 'react'
import { afterEach, describe, expect, it, vi } from 'vitest'
import { activate } from './index'

vi.mock('@dotcraft/plugin', () => ({ ActionTooltip: ({ children }: { children: ReactNode }) => children }))

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
    surface: (name: string) => surfaces.get(name)!,
    notify: (method: string) => notifications.get(method)?.(),
    update: (next: Record<string, unknown>) => { current = next }
  }
}

const context = (backendId: string | null) => ({ minimalChrome: false, provider: { id: 'ag', backendId } })

describe('Antigravity desktop module', () => {
  afterEach(cleanup)

  it('replaces the usage indicator only for the antigravity backend and renders multi-pool popover', async () => {
    const plugin = setup({
      available: true,
      planType: 'Google AI Pro',
      credits: { hasCredits: true, unlimited: false, balance: '420' },
      values: {
        'pool.gemini4.remaining': 80,
        'pool.claude.remaining': 35
      },
      labels: {
        pool_order: 'gemini4,claude',
        'pool.gemini4.name': 'Gemini 4',
        'pool.claude.name': 'Claude'
      }
    })
    const Usage = plugin.surface('composer.status.subscription')

    render(<Usage context={context('other')}><span data-testid="core-usage" /></Usage>)
    expect(screen.getByTestId('core-usage')).toBeTruthy()
    expect(plugin.request).not.toHaveBeenCalled()
    cleanup()

    render(<Usage context={context('antigravity')}><span data-testid="core-usage" /></Usage>)
    expect(screen.queryByTestId('core-usage')).toBeNull()
    await waitFor(() => expect(plugin.request).toHaveBeenCalledWith('provider/auth/usage', { providerId: 'ag' }, 30_000))
    const button = await screen.findByRole('button', { name: /Gemini 4 80% left.*Claude 35% left/ })
    fireEvent.click(button)
    expect(await screen.findByText('Google AI Pro')).toBeTruthy()
    expect(screen.getByText('AI credits: 420')).toBeTruthy()
  })

  it('refreshes after a turn and renders a verification link when validation is required', async () => {
    const plugin = setup({
      available: true,
      values: { 'pool.gemini4.remaining': 90 },
      labels: { pool_order: 'gemini4', 'pool.gemini4.name': 'Gemini 4' }
    })
    const Usage = plugin.surface('composer.status.subscription')
    const Notice = plugin.surface('composer.before')
    render(<><Notice context={context('antigravity')} /><Usage context={context('antigravity')} /></>)
    expect(await screen.findByRole('button', { name: /90% left/ })).toBeTruthy()
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
