import type { Mode } from '../config/modes'
import type { StoryBible } from '../types/storyTime'

/**
 * Bridge to the iOS 27+ host app (src/ios). The host registers a
 * `WKScriptMessageHandlerWithReply` named `storyTime`, so every `postMessage`
 * call resolves with the native reply. Outside the host (plain browser/PWA)
 * the bridge is absent and the app keeps using server-side composition.
 */

export const NATIVE_BRIDGE_HANDLER = 'storyTime'
export const NATIVE_DICTATION_EVENT = 'storytime:dictation'
export const APPLE_INTELLIGENCE_PROVIDER = 'apple-foundation-models'

export type AppleIntelligenceAvailability =
  | 'available'
  | 'deviceNotEligible'
  | 'appleIntelligenceNotEnabled'
  | 'modelNotReady'
  | 'unavailable'

export type NativeCapabilities = {
  appleIntelligence: AppleIntelligenceAvailability
  dictation: boolean
}

export type OnDeviceDraftInput = {
  childName: string
  mode: Mode
  durationMinutes: number
  customization?: Record<string, string>
  storyBible?: StoryBible
}

export type OnDeviceDraft = {
  title: string
  scenes: string[]
  provider: typeof APPLE_INTELLIGENCE_PROVIDER
}

export type DictationEventDetail = {
  text: string
  isFinal: boolean
}

type NativeMessage =
  | { action: 'capabilities' }
  | { action: 'draftStory'; input: OnDeviceDraftInput }
  | { action: 'startDictation' }
  | { action: 'stopDictation' }

type MessageHandler = { postMessage: (message: NativeMessage) => Promise<unknown> }

type WebKitWindow = Window & {
  webkit?: { messageHandlers?: Record<string, MessageHandler | undefined> }
}

const unavailableCapabilities: NativeCapabilities = Object.freeze({
  appleIntelligence: 'unavailable',
  dictation: false,
})

const resolveHandler = (): MessageHandler | null => {
  if (typeof window === 'undefined') {
    return null
  }

  const handler = (window as WebKitWindow).webkit?.messageHandlers?.[NATIVE_BRIDGE_HANDLER]
  return handler && typeof handler.postMessage === 'function' ? handler : null
}

const isAvailability = (value: unknown): value is AppleIntelligenceAvailability =>
  value === 'available' ||
  value === 'deviceNotEligible' ||
  value === 'appleIntelligenceNotEnabled' ||
  value === 'modelNotReady' ||
  value === 'unavailable'

const normalizeCapabilities = (reply: unknown): NativeCapabilities => {
  if (!reply || typeof reply !== 'object') {
    return unavailableCapabilities
  }

  const candidate = reply as Partial<NativeCapabilities>
  return {
    appleIntelligence: isAvailability(candidate.appleIntelligence) ? candidate.appleIntelligence : 'unavailable',
    dictation: candidate.dictation === true,
  }
}

const normalizeDraft = (reply: unknown): OnDeviceDraft | null => {
  if (!reply || typeof reply !== 'object') {
    return null
  }

  const candidate = reply as { title?: unknown; scenes?: unknown }
  const scenes = Array.isArray(candidate.scenes)
    ? candidate.scenes.filter((scene): scene is string => typeof scene === 'string' && scene.trim().length > 0)
    : []
  if (scenes.length === 0) {
    return null
  }

  return {
    title: typeof candidate.title === 'string' ? candidate.title.trim() : '',
    scenes,
    provider: APPLE_INTELLIGENCE_PROVIDER,
  }
}

export const isNativeHost = (): boolean => resolveHandler() !== null

export const createNativeBridge = () => {
  const handler = resolveHandler()
  if (!handler) {
    return null
  }

  return Object.freeze({
    getCapabilities: async (): Promise<NativeCapabilities> =>
      normalizeCapabilities(await handler.postMessage({ action: 'capabilities' })),
    draftStory: async (input: OnDeviceDraftInput): Promise<OnDeviceDraft | null> =>
      normalizeDraft(await handler.postMessage({ action: 'draftStory', input })),
    startDictation: async (): Promise<string> => {
      const reply = await handler.postMessage({ action: 'startDictation' })
      const transcript = (reply as { transcript?: unknown } | null)?.transcript
      return typeof transcript === 'string' ? transcript.trim() : ''
    },
    stopDictation: async (): Promise<void> => {
      await handler.postMessage({ action: 'stopDictation' })
    },
  })
}

export type NativeBridge = NonNullable<ReturnType<typeof createNativeBridge>>
