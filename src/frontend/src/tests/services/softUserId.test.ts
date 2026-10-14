import { createSoftUserId } from '../../services/softUserId'

describe('createSoftUserId', () => {
  it('falls back to getRandomValues outside secure contexts (LAN device testing)', () => {
    const randomUUID = crypto.randomUUID
    Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: undefined })
    try {
      const generated = createSoftUserId()
      expect(generated).toMatch(/^[0-9a-f]{8}-[0-9a-f]{4}-4[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$/)
      expect(createSoftUserId()).not.toBe(generated)
    } finally {
      Object.defineProperty(crypto, 'randomUUID', { configurable: true, value: randomUUID })
    }
  })
})
