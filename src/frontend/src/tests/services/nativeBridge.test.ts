import {
  APPLE_INTELLIGENCE_PROVIDER,
  createNativeBridge,
  isNativeHost,
} from '../../services/nativeBridge'

type WebKitTestWindow = Window & { webkit?: unknown }

const installHandler = (postMessage: (message: unknown) => Promise<unknown>) => {
  ;(window as WebKitTestWindow).webkit = { messageHandlers: { storyTime: { postMessage } } }
}

describe('nativeBridge', () => {
  afterEach(() => {
    delete (window as WebKitTestWindow).webkit
  })

  it('is absent outside the iOS host', () => {
    expect(isNativeHost()).toBe(false)
    expect(createNativeBridge()).toBeNull()
  })

  it('normalizes capability replies from the host', async () => {
    installHandler(async () => ({ appleIntelligence: 'available', dictation: true }))
    const bridge = createNativeBridge()

    expect(isNativeHost()).toBe(true)
    await expect(bridge?.getCapabilities()).resolves.toEqual({ appleIntelligence: 'available', dictation: true })
  })

  it('treats malformed capability replies as unavailable', async () => {
    installHandler(async () => ({ appleIntelligence: 'quantum', dictation: 'yes' }))
    await expect(createNativeBridge()?.getCapabilities()).resolves.toEqual({
      appleIntelligence: 'unavailable',
      dictation: false,
    })

    installHandler(async () => null)
    await expect(createNativeBridge()?.getCapabilities()).resolves.toEqual({
      appleIntelligence: 'unavailable',
      dictation: false,
    })
  })

  it('sends draft input and tags the reply with the Apple Intelligence provider', async () => {
    const postMessage = vi.fn(async () => ({ title: ' Moon Boat ', scenes: ['One', ' ', 2, 'Two'] }))
    installHandler(postMessage)

    const draft = await createNativeBridge()?.draftStory({ childName: 'Ari', mode: 'one-shot', durationMinutes: 5 })

    expect(postMessage).toHaveBeenCalledWith({
      action: 'draftStory',
      input: { childName: 'Ari', mode: 'one-shot', durationMinutes: 5 },
    })
    expect(draft).toEqual({ title: 'Moon Boat', scenes: ['One', 'Two'], provider: APPLE_INTELLIGENCE_PROVIDER })
  })

  it('returns null when the draft has no usable scenes', async () => {
    installHandler(async () => ({ title: 'Empty', scenes: [] }))
    await expect(
      createNativeBridge()?.draftStory({ childName: 'Ari', mode: 'series', durationMinutes: 5 }),
    ).resolves.toBeNull()

    installHandler(async () => 'not-an-object')
    await expect(
      createNativeBridge()?.draftStory({ childName: 'Ari', mode: 'series', durationMinutes: 5 }),
    ).resolves.toBeNull()
  })

  it('round-trips dictation start and stop', async () => {
    const postMessage = vi.fn(async (message: unknown) =>
      (message as { action: string }).action === 'startDictation' ? { transcript: '  Luna  ' } : null,
    )
    installHandler(postMessage)
    const bridge = createNativeBridge()

    await expect(bridge?.startDictation()).resolves.toBe('Luna')
    await bridge?.stopDictation()
    expect(postMessage).toHaveBeenLastCalledWith({ action: 'stopDictation' })

    installHandler(async () => ({}))
    await expect(createNativeBridge()?.startDictation()).resolves.toBe('')
  })
})
