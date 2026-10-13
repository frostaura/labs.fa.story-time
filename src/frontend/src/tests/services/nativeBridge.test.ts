import {
  APPLE_INTELLIGENCE_PROVIDER,
  createNativeBridge,
  isNativeHost,
  nativeNarrationSource,
  parseNativeNarrationSource,
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
    installHandler(async () => ({ appleIntelligence: 'available', dictation: true, narration: true }))
    const bridge = createNativeBridge()

    expect(isNativeHost()).toBe(true)
    await expect(bridge?.getCapabilities()).resolves.toEqual({
      appleIntelligence: 'available',
      dictation: true,
      narration: true,
    })
  })

  it('treats malformed capability replies as unavailable', async () => {
    installHandler(async () => ({ appleIntelligence: 'quantum', dictation: 'yes' }))
    await expect(createNativeBridge()?.getCapabilities()).resolves.toEqual({
      appleIntelligence: 'unavailable',
      dictation: false,
      narration: false,
    })

    installHandler(async () => null)
    await expect(createNativeBridge()?.getCapabilities()).resolves.toEqual({
      appleIntelligence: 'unavailable',
      dictation: false,
      narration: false,
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

  it('round-trips on-device narration rendering and loading', async () => {
    const postMessage = vi.fn(async (message: unknown) => {
      const action = (message as { action: string }).action
      if (action === 'narrateStory') {
        return { teaserDurationSeconds: 9.5, fullDurationSeconds: 182 }
      }
      return { dataUri: 'data:audio/mp4;base64,AAAA' }
    })
    installHandler(postMessage)
    const bridge = createNativeBridge()

    await expect(
      bridge?.narrateStory({ storyId: 'story-1', title: 'Moon Boat', scenes: ['One'], narrationStyle: 'calm' }),
    ).resolves.toEqual({ teaserDurationSeconds: 9.5, fullDurationSeconds: 182 })
    await expect(bridge?.loadNarration('story-1', 'full')).resolves.toBe('data:audio/mp4;base64,AAAA')
    expect(postMessage).toHaveBeenLastCalledWith({ action: 'loadNarration', storyId: 'story-1', part: 'full' })
  })

  it('rejects unusable narration replies', async () => {
    installHandler(async () => ({ teaserDurationSeconds: 0, fullDurationSeconds: 'long', dataUri: 'javascript:alert(1)' }))
    const bridge = createNativeBridge()

    await expect(bridge?.narrateStory({ storyId: 's', title: 't', scenes: ['x'] })).resolves.toBeNull()
    await expect(bridge?.loadNarration('s', 'teaser')).resolves.toBeNull()
  })

  it('builds and parses on-device narration references', () => {
    const source = nativeNarrationSource('story/1', 'teaser')

    expect(source).toBe('storytime-native:narration/story%2F1/teaser')
    expect(parseNativeNarrationSource(source)).toEqual({ storyId: 'story/1', part: 'teaser' })
    expect(parseNativeNarrationSource('data:audio/wav;base64,AAA=')).toBeNull()
    expect(parseNativeNarrationSource('storytime-native:narration/story-1/other')).toBeNull()
    expect(parseNativeNarrationSource('storytime-native:narration/story-1/full/extra')).toBeNull()
    expect(parseNativeNarrationSource(undefined)).toBeNull()
  })
})
