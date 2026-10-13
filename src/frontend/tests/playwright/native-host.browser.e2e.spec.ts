import { expect, test, type Page } from '@playwright/test'
import { browserStorageKeys } from './support/runtimeStorage'
import {
  buildGeneratedStoryPayload,
  fulfillJson,
  installCommonMockRoutes,
  mockEmptyLibrary,
} from './support/storyMocks'

const backendHost = process.env.PLAYWRIGHT_API_HOST ?? '127.0.0.1'
const backendPort = Number.parseInt(process.env.PLAYWRIGHT_API_PORT ?? '19082', 10)
const backendBaseUrl = process.env.PLAYWRIGHT_API_BASE_URL ?? `http://${backendHost}:${backendPort}`
const liveBackendExpectationTimeout = 15_000

type FakeHostOptions = {
  appleIntelligence: string
  dictation: boolean
  draft?: { title: string; scenes: string[] }
  transcript?: string
}

/**
 * Simulates the iOS 27+ host (src/ios) by installing the same
 * `window.webkit.messageHandlers.storyTime` reply handler the WKWebView exposes.
 */
const installFakeIosHost = async (page: Page, options: FakeHostOptions) => {
  await page.addInitScript((hostOptions: FakeHostOptions) => {
    let finishDictation: ((reply: unknown) => void) | null = null
    const hostWindow = window as Window & {
      webkit?: unknown
      __storyTimeHostMessages?: unknown[]
    }
    hostWindow.__storyTimeHostMessages = []
    hostWindow.webkit = {
      messageHandlers: {
        storyTime: {
          postMessage: (message: { action: string }) => {
            hostWindow.__storyTimeHostMessages?.push(message)
            switch (message.action) {
              case 'capabilities':
                return Promise.resolve({
                  appleIntelligence: hostOptions.appleIntelligence,
                  dictation: hostOptions.dictation,
                })
              case 'draftStory':
                return Promise.resolve(hostOptions.draft ?? null)
              case 'startDictation':
                window.setTimeout(() => {
                  window.dispatchEvent(
                    new CustomEvent('storytime:dictation', {
                      detail: { text: hostOptions.transcript?.slice(0, 2) ?? '', isFinal: false },
                    }),
                  )
                }, 50)
                return new Promise((resolve) => {
                  finishDictation = resolve
                })
              case 'stopDictation':
                finishDictation?.({ transcript: hostOptions.transcript ?? '' })
                finishDictation = null
                return Promise.resolve(null)
              default:
                return Promise.resolve(null)
            }
          },
        },
      },
    }
  }, options)
}

test('UC-001 iOS host drafts with Apple Intelligence and posts the on-device draft', async ({ page }) => {
  await installCommonMockRoutes(page)
  await mockEmptyLibrary(page)
  await installFakeIosHost(page, {
    appleIntelligence: 'available',
    dictation: false,
    draft: { title: 'Ari and the Lantern Fox', scenes: ['A soft glow appears.', 'The fox hums goodnight.'] },
  })

  let generateBody: Record<string, unknown> | null = null
  await page.route('**/api/stories/generate', async (route) => {
    generateBody = route.request().postDataJSON() as Record<string, unknown>
    await fulfillJson(
      route,
      200,
      buildGeneratedStoryPayload({ title: 'Ari and the Lantern Fox', scenes: ['A soft glow appears.'] }),
    )
  })

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Quick Generate' })).toBeVisible()
  await page.getByRole('button', { name: 'Generate story' }).click()

  await expect(page.getByTestId('recent-story-playwright-story-1')).toContainText('Ari and the Lantern Fox')
  expect(generateBody).toMatchObject({
    onDeviceDraft: {
      title: 'Ari and the Lantern Fox',
      scenes: ['A soft glow appears.', 'The fox hums goodnight.'],
      provider: 'apple-foundation-models',
    },
  })
})

test('UC-001 iOS host on-device dictation fills the child name', async ({ page }) => {
  await installCommonMockRoutes(page)
  await mockEmptyLibrary(page)
  await installFakeIosHost(page, { appleIntelligence: 'unavailable', dictation: true, transcript: 'Luna' })

  await page.goto('/')
  await page.getByRole('button', { name: 'Dictate Child name' }).click()
  await expect(page.getByRole('button', { name: 'Stop dictating Child name' })).toHaveAttribute('aria-pressed', 'true')
  await expect(page.getByLabel('Child name', { exact: true })).toHaveValue('Lu')

  await page.getByRole('button', { name: 'Stop dictating Child name' }).click()
  await expect(page.getByLabel('Child name', { exact: true })).toHaveValue('Luna')
  await expect(page.getByRole('button', { name: 'Dictate Child name' })).toHaveAttribute('aria-pressed', 'false')
})

test('UC-001 browser without the iOS host shows no dictation controls', async ({ page }) => {
  await installCommonMockRoutes(page)
  await mockEmptyLibrary(page)

  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Quick Generate' })).toBeVisible()
  await expect(page.getByRole('button', { name: /^Dictate / })).toHaveCount(0)
})

test('UC-001 live backend accepts the Apple Intelligence draft end-to-end', async ({ page }) => {
  const softUserId = `playwright-on-device-${Date.now()}`
  await page.addInitScript(
    ({ apiBaseUrl, userId, softUserIdStorageKey }) => {
      const storyTimeWindow = window as Window & { __STORYTIME_API_BASE_URL__?: string }
      storyTimeWindow.__STORYTIME_API_BASE_URL__ = apiBaseUrl
      localStorage.setItem(softUserIdStorageKey, userId)
    },
    { apiBaseUrl: backendBaseUrl, userId: softUserId, softUserIdStorageKey: browserStorageKeys.softUserId },
  )
  await installFakeIosHost(page, {
    appleIntelligence: 'available',
    dictation: false,
    draft: { title: 'Ari and the Sleepy Comet', scenes: ['Ari waves at a sleepy comet.', 'The comet tucks the stars in.'] },
  })

  await page.goto('/')
  await expect(page.getByTestId('tier-badge')).toHaveText(/trial/i, { timeout: liveBackendExpectationTimeout })
  await page.getByRole('button', { name: 'Generate story' }).click()

  await expect(page.getByText('Ari and the Sleepy Comet').first()).toBeVisible({
    timeout: liveBackendExpectationTimeout,
  })
})
