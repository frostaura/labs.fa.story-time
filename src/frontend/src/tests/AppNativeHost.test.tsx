import { act, render, screen, waitFor } from '@testing-library/react'
import userEvent from '@testing-library/user-event'
import App from '../App'
import { runtimeConfig } from '../config/runtime'
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

  it('records narration on-device and plays the cached audio instead of server clips', async () => {
    installFetch(() => undefined)
    const narrateInputs: Record<string, unknown>[] = []
    const postMessage = installHost(async (message) => {
      if (message.action === 'capabilities') {
        return { appleIntelligence: 'unavailable', dictation: false, narration: true }
      }
      if (message.action === 'narrateStory') {
        narrateInputs.push(message.input ?? {})
        return { teaserDurationSeconds: 8, fullDurationSeconds: 95 }
      }
      if (message.action === 'loadNarration') {
        const part = (message as unknown as { part: string }).part
        return { dataUri: `data:audio/mp4;base64,${part === 'full' ? 'RlVMTA==' : 'VEVBU0VS'}` }
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
    expect(narrateInputs[0]).toMatchObject({ storyId: 'native-story', title: 'Server Story', scenes: ['Server scene'] })

    const audio = await screen.findByLabelText('Full narration for Server Story')
    await waitFor(() => expect(audio).toHaveAttribute('src', 'data:audio/mp4;base64,RlVMTA=='))
    expect(postMessage).toHaveBeenCalledWith({ action: 'loadNarration', storyId: 'native-story', part: 'full' })

    const stored = JSON.parse(window.localStorage.getItem(runtimeConfig.storageKeys.storyArtifacts) ?? '[]') as Record<string, unknown>[]
    expect(stored[0]).toMatchObject({
      narrationSource: 'device',
      teaserAudio: 'storytime-native:narration/native-story/teaser',
      fullAudio: 'storytime-native:narration/native-story/full',
    })
  })

  it('keeps server narration when on-device narration fails', async () => {
    installFetch(() => undefined)
    installHost(async (message) => {
      if (message.action === 'capabilities') {
        return { appleIntelligence: 'unavailable', dictation: false, narration: true }
      }
      if (message.action === 'narrateStory') {
        throw new Error('no voices installed')
      }
      return null
    })

    render(<App />)
    await screen.findByRole('heading', { name: 'Quick Generate' })
    await act(async () => {
      await Promise.resolve()
    })
    await userEvent.click(screen.getByRole('button', { name: /generate/i }))

    const audio = await screen.findByLabelText('Full narration for Server Story')
    expect(audio).toHaveAttribute('src', 'data:audio/wav;base64,BBB=')
  })

  it('keeps on-device full narration locked until the API reports approval', async () => {
    let libraryCalls = 0
    vi.stubGlobal(
      'fetch',
      vi.fn(async (input: RequestInfo | URL) => {
        const url = typeof input === 'string' ? input : input.toString()
        if (url.endsWith('/api/home/status')) {
          return jsonResponse(homeStatus)
        }
        if (url.includes('/api/library/')) {
          libraryCalls += 1
          const approved = libraryCalls > 2
          return jsonResponse({
            recent: approved
              ? [{
                  storyId: 'gated-story',
                  title: 'Gated Story',
                  mode: 'series',
                  isFavorite: false,
                  fullAudioReady: true,
                  fullAudio: 'data:audio/wav;base64,U0VSVkVS',
                  createdAt: new Date().toISOString(),
                }]
              : [],
            favorites: [],
            kidShelfEnabled: false,
          })
        }
        if (url.includes('/api/stories/generate')) {
          return jsonResponse({
            storyId: 'gated-story',
            title: 'Gated Story',
            mode: 'series',
            recap: '',
            scenes: ['Scene'],
            sceneCount: 1,
            posterLayers: [],
            approvalRequired: true,
            teaserAudio: 'data:audio/wav;base64,AAA=',
            fullAudioReady: false,
            reducedMotion: false,
            generatedAt: new Date().toISOString(),
          })
        }
        return new Response('{}', { status: 404 })
      }),
    )
    installHost(async (message) => {
      if (message.action === 'capabilities') {
        return { appleIntelligence: 'unavailable', dictation: false, narration: true }
      }
      if (message.action === 'narrateStory') {
        return { teaserDurationSeconds: 8, fullDurationSeconds: 95 }
      }
      if (message.action === 'loadNarration') {
        const part = (message as unknown as { part: string }).part
        return { dataUri: `data:audio/mp4;base64,${part === 'full' ? 'RlVMTA==' : 'VEVBU0VS'}` }
      }
      return null
    })

    const { unmount } = render(<App />)
    await screen.findByRole('heading', { name: 'Quick Generate' })
    await act(async () => {
      await Promise.resolve()
    })
    await userEvent.click(screen.getByRole('button', { name: /generate/i }))

    const teaser = await screen.findByLabelText('Teaser narration for Gated Story')
    await waitFor(() => expect(teaser).toHaveAttribute('src', 'data:audio/mp4;base64,VEVBU0VS'))
    expect(screen.queryByLabelText('Full narration for Gated Story')).not.toBeInTheDocument()

    await waitFor(() => expect(libraryCalls).toBe(2))
    unmount()

    // A fresh library sync reports the parent-approved story: the on-device full narration unlocks, not the server clip.
    render(<App />)
    const full = await screen.findByLabelText('Full narration for Gated Story')
    await waitFor(() => expect(full).toHaveAttribute('src', 'data:audio/mp4;base64,RlVMTA=='))
  })
})
