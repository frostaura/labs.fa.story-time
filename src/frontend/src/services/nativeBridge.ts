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
export const NATIVE_NARRATION_PREFIX = 'storytime-native:narration/'

export type AppleIntelligenceAvailability =
  | 'available'
  | 'deviceNotEligible'
  | 'appleIntelligenceNotEnabled'
  | 'modelNotReady'
  | 'unavailable'

export type NativeCapabilities = {
  appleIntelligence: AppleIntelligenceAvailability
  dictation: boolean
  narration: boolean
}

export type NarrationPart = 'teaser' | 'full'

export type NarrateStoryInput = {
  storyId: string
  title: string
  recap?: string
  scenes: string[]
  narrationStyle?: string
}

export type NativeNarration = {
  teaserDurationSeconds: number
  fullDurationSeconds: number
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
  | { action: 'narrateStory'; input: NarrateStoryInput }
  | { action: 'loadNarration'; storyId: string; part: NarrationPart }

type MessageHandler = { postMessage: (message: NativeMessage) => Promise<unknown> }

type WebKitWindow = Window & {
  webkit?: { messageHandlers?: Record<string, MessageHandler | undefined> }
}

const unavailableCapabilities: NativeCapabilities = Object.freeze({
  appleIntelligence: 'unavailable',
  dictation: false,
  narration: false,
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
    narration: candidate.narration === true,
  }
}

const isPositiveNumber = (value: unknown): value is number =>
  typeof value === 'number' && Number.isFinite(value) && value > 0

const normalizeNarration = (reply: unknown): NativeNarration | null => {
  const candidate = reply as Partial<NativeNarration> | null
  if (!candidate || !isPositiveNumber(candidate.teaserDurationSeconds) || !isPositiveNumber(candidate.fullDurationSeconds)) {
    return null
  }

  return {
    teaserDurationSeconds: candidate.teaserDurationSeconds,
    fullDurationSeconds: candidate.fullDurationSeconds,
  }
}

/** Small persisted reference to narration audio cached by the iOS host (audio is too large for LocalStorage). */
export const nativeNarrationSource = (storyId: string, part: NarrationPart): string =>
  `${NATIVE_NARRATION_PREFIX}${encodeURIComponent(storyId)}/${part}`

export const parseNativeNarrationSource = (
  source: string | undefined | null,
): { storyId: string; part: NarrationPart } | null => {
  if (!source || !source.startsWith(NATIVE_NARRATION_PREFIX)) {
    return null
  }

  const [encodedStoryId, part, ...rest] = source.slice(NATIVE_NARRATION_PREFIX.length).split('/')
  if (!encodedStoryId || rest.length > 0 || (part !== 'teaser' && part !== 'full')) {
    return null
  }

  return { storyId: decodeURIComponent(encodedStoryId), part }
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
    narrateStory: async (input: NarrateStoryInput): Promise<NativeNarration | null> =>
      normalizeNarration(await handler.postMessage({ action: 'narrateStory', input })),
    loadNarration: async (storyId: string, part: NarrationPart): Promise<string | null> => {
      const reply = await handler.postMessage({ action: 'loadNarration', storyId, part })
      const dataUri = (reply as { dataUri?: unknown } | null)?.dataUri
      return typeof dataUri === 'string' && dataUri.startsWith('data:audio/') ? dataUri : null
    },
  })
}

export type NativeBridge = NonNullable<ReturnType<typeof createNativeBridge>>
