import { useEffect, useMemo, useState } from 'react'
import { logClientWarning } from '../config/clientLogger'
import { createNativeBridge, parseNativeNarrationSource } from '../services/nativeBridge'

/**
 * Resolves an audio source for playback. Server narration (data URIs) passes through;
 * on-device narration references are loaded from the iOS host's cache on demand.
 */
export const useNarrationSource = (source: string | undefined): string | undefined => {
  const reference = useMemo(() => parseNativeNarrationSource(source), [source])
  const [resolved, setResolved] = useState<{ source: string; dataUri: string } | null>(null)

  useEffect(() => {
    if (!reference || !source) {
      return
    }

    const bridge = createNativeBridge()
    if (!bridge) {
      return
    }

    let cancelled = false
    bridge
      .loadNarration(reference.storyId, reference.part)
      .then((dataUri) => {
        if (!cancelled && dataUri) {
          setResolved({ source, dataUri })
        }
      })
      .catch((loadError: unknown) => {
        logClientWarning('On-device narration could not be loaded.', loadError)
      })

    return () => {
      cancelled = true
    }
  }, [reference, source])

  if (!reference) {
    return source
  }

  return resolved && resolved.source === source ? resolved.dataUri : undefined
}
