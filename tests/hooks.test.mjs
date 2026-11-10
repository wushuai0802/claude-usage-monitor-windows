import assert from 'node:assert/strict'
import { register } from '../plugin/usage-monitor/hooks/register.js'
const hooks = {}
register((name, handler) => { hooks[name] = handler })
const files = new Map()
let fail = false
let time = Date.now()
const $ = {
  env: { get: async () => 'C:\\fixture' },
  session: { id: async () => 'session-a' },
  clock: { now: async () => ++time },
  fs: {
    stat: async () => ({ kind: 'file' }),
    write: async (path, text) => { if (fail) throw new Error('fixture write failure'); files.set(path, text) },
  },
  process: { run: async () => { throw new Error('fixture launch failure') } },
}
const event = { changed: ['rateLimits', 'context'], rateLimits: [{ kind: 'five_hour', percentUsed: 25, resetsAt: new Date(time + 3600000).toISOString() }], context: { percent: 30, tokens: 30000, window: 100000 } }
let nextCalls = 0
const next = async () => { nextCalls++; return 'host-result' }
assert.equal(await hooks['session.start']($, {}, next), 'host-result')
assert.equal(await hooks['session.measure']($, event, next), 'host-result')
assert.equal(files.size, 1)
const first = [...files.values()][0]
assert.equal(JSON.parse(first).complete, true)
fail = true
assert.equal(await hooks['session.measure']($, event, next), 'host-result')
assert.equal([...files.values()][0], first, 'failed writes preserve the previous complete slot')
fail = false
await hooks['session.measure']($, { ...event, context: { window: 100000 }, rateLimits: [] }, next)
const third = JSON.parse([...files.values()][0])
assert.equal(third.context.percent, null, 'compaction clears the previous percentage')
assert.deepEqual(third.rateLimits, [], 'empty quota must invalidate a prior account reading')
await hooks['session.measure']($, event, next)
assert.equal(files.size, 2, 'writes alternate between two slots')
const originalNextCalls = nextCalls
await assert.rejects(hooks['session.measure']($, event, async () => { throw new Error('host-failure') }), /host-failure/)
assert.equal(nextCalls, originalNextCalls)
const before = [...files.entries()]
await hooks['session.measure']({ ...$, session: { id: async () => '..\\escape' } }, event, next)
assert.deepEqual([...files.entries()], before, 'session id cannot escape the snapshot directory')
await Promise.all(Array.from({ length: 8 }, () => hooks['session.measure']($, event, next)))
const values = [...files.values()].map(JSON.parse).sort((a, b) => a.sequence - b.sequence)
assert.equal(values[1].sequence - values[0].sequence, 1, 'concurrent hooks serialize their writes')
assert.equal(values[1].pluginVersion, '1.1.3')
assert.equal(values[1].sessionId, 'session-a')
console.log('PASS: hook error isolation, slot recovery, compaction, path validation and concurrent writes.')
