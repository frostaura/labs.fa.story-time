import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App'
import { NATIVE_DICTATION_EVENT } from '../services/nativeBridge'

type WebKitTestWindow = Window & { webkit?: unknown }
type NativeMessage = { action: string; input?: Record<string, unknown> }

const homeStatus = {
  quickGenerateVisible: true,
  durationSliderVisible: true,
  durationMinMinutes: 5,
  durationMaxMinutes: 15,
  durationDefaultMinutes: 6,
  defaultChildName: 'Child',
  parentControlsEnabled: true,
  defaultTier: 'Trial',
}

const jsonResponse = (body: unknown) =>
  new Response(JSON.stringify(body), { status: 200, headers: { 'Content-Type': 'application/json' } })

const installFetch = (onGenerate: (body: Record<string, unknown>) => void) => {
  vi.stubGlobal(
    'fetch',
    vi.fn(async (input: RequestInfo | URL, init?: RequestInit) => {
      const url = typeof input === 'string' ? input : input.toString()
      if (url.endsWith('/api/home/status')) {
        return jsonResponse(homeStatus)
      }
      if (url.includes('/api/library/')) {
        return jsonResponse({ recent: [], favorites: [], kidShelfEnabled: false })
      }
      if (url.includes('/api/stories/generate')) {
        const body = JSON.parse(String(init?.body)) as Record<string, unknown>
        onGenerate(body)
        const draft = body.onDeviceDraft as { title: string; scenes: string[] } | undefined
        return jsonResponse({
          storyId: 'native-story',
          title: draft?.title ?? 'Server Story',
          mode: body.mode,
          recap: '',
          scenes: draft?.scenes ?? ['Server scene'],
          sceneCount: draft?.scenes.length ?? 1,
          posterLayers: [],
          approvalRequired: false,
          teaserAudio: 'data:audio/wav;base64,AAA=',
          fullAudio: 'data:audio/wav;base64,BBB=',
          fullAudioReady: true,
          reducedMotion: false,
          generatedAt: new Date().toISOString(),
        })
      }
      return new Response('{}', { status: 404 })
    }),
  )
}

const installHost = (handle: (message: NativeMessage) => Promise<unknown>) => {
  const postMessage = vi.fn(handle)
  ;(window as WebKitTestWindow).webkit = { messageHandlers: { storyTime: { postMessage } } }
  return postMessage
}

describe('App inside the iOS host', () => {
  beforeEach(() => {
    window.localStorage.clear()
    Element.prototype.scrollIntoView = vi.fn()
  })

  afterEach(() => {
    delete (window as WebKitTestWindow).webkit
    vi.unstubAllGlobals()
  })

  it('drafts the story on-device with Apple Intelligence and sends the draft to the API', async () => {
    let generateBody: Record<string, unknown> | null = null
    installFetch((body) => {
      generateBody = body
    })
    const postMessage = installHost(async (message) => {
      if (message.action === 'capabilities') {
        return { appleIntelligence: 'available', dictation: false }
      }
      if (message.action === 'draftStory') {
        return { title: 'Ari and the Lantern Fox', scenes: ['A soft glow appears.', 'The fox hums goodnight.'] }
      }
      return null
    })

    render(<App />)
    await screen.findByRole('heading', { name: 'Quick Generate' })
    await waitFor(() => expect(postMessage).toHaveBeenCalledWith({ action: 'capabilities' }))

    await userEvent.clear(screen.getByLabelText('Child name'))
    await userEvent.type(screen.getByLabelText('Child name'), 'Ari')
    await userEvent.click(screen.getByRole('button', { name: /generate/i }))

    await screen.findByText('Ari and the Lantern Fox')
    expect(postMessage).toHaveBeenCalledWith(
      expect.objectContaining({
        action: 'draftStory',
        input: expect.objectContaining({ childName: 'Ari', durationMinutes: 6 }),
      }),
    )
    expect(generateBody).toMatchObject({
      childName: 'Ari',
      onDeviceDraft: {
        title: 'Ari and the Lantern Fox',
        scenes: ['A soft glow appears.', 'The fox hums goodnight.'],
        provider: 'apple-foundation-models',
      },
    })
  })

  it.each([
    ['unavailable', 'modelNotReady', false],
    ['failing', 'available', true],
  ])('falls back to server composition when Apple Intelligence is %s', async (_label, availability, expectDraftCall) => {
    let generateBody: Record<string, unknown> | null = null
    installFetch((body) => {
      generateBody = body
    })
    const postMessage = installHost(async (message) => {
      if (message.action === 'capabilities') {
        return { appleIntelligence: availability, dictation: false }
      }
      if (message.action === 'draftStory') {
        throw new Error('guardrail violation')
      }
      return null
    })

    render(<App />)
    await screen.findByRole('heading', { name: 'Quick Generate' })
    await waitFor(() => expect(postMessage).toHaveBeenCalledWith({ action: 'capabilities' }))
    await act(async () => {
      await Promise.resolve()
    })
    await userEvent.click(screen.getByRole('button', { name: /generate/i }))

    await screen.findByText('Server Story')
    expect(postMessage.mock.calls.some(([message]) => message.action === 'draftStory')).toBe(expectDraftCall)
    expect(generateBody).not.toHaveProperty('onDeviceDraft')
  })

  it('fills fields from native on-device dictation, including live partial results', async () => {
    installFetch(() => undefined)
    let finishDictation: (reply: unknown) => void = () => undefined
    const postMessage = installHost(async (message) => {
      if (message.action === 'capabilities') {
        return { appleIntelligence: 'unavailable', dictation: true }
      }
      if (message.action === 'startDictation') {
        return new Promise((resolve) => {
          finishDictation = resolve
        })
      }
      if (message.action === 'stopDictation') {
        finishDictation({ transcript: 'Luna' })
      }
      return null
    })

    render(<App />)
    const dictateButton = await screen.findByRole('button', { name: 'Dictate Child name' })
    await userEvent.click(dictateButton)

    expect(screen.getByRole('button', { name: 'Stop dictating Child name' })).toHaveAttribute('aria-pressed', 'true')
    act(() => {
      window.dispatchEvent(new CustomEvent(NATIVE_DICTATION_EVENT, { detail: { text: 'Lu', isFinal: false } }))
    })
    expect(screen.getByLabelText('Child name')).toHaveValue('Lu')

    await userEvent.click(screen.getByRole('button', { name: 'Stop dictating Child name' }))

    await waitFor(() => expect(screen.getByLabelText('Child name')).toHaveValue('Luna'))
    expect(postMessage).toHaveBeenCalledWith({ action: 'stopDictation' })
    expect(screen.getByRole('button', { name: 'Dictate Child name' })).toHaveAttribute('aria-pressed', 'false')
  })

  it('shows calm guidance when native dictation fails', async () => {
    installFetch(() => undefined)
    installHost(async (message) => {
      if (message.action === 'capabilities') {
        return { appleIntelligence: 'unavailable', dictation: true }
      }
      if (message.action === 'startDictation') {
        throw new Error('microphone denied')
      }
      return null
    })

    render(<App />)
    await userEvent.click(await screen.findByRole('button', { name: 'Dictate Child name' }))

    expect(await screen.findByText("Dictation isn't available right now. You can keep typing.")).toBeInTheDocument()
  })
})
