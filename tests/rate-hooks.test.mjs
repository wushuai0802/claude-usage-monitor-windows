import assert from 'node:assert/strict'
import { createRateTracker } from '../plugin/usage-monitor/hooks/rate-state.js'
import { register } from '../plugin/usage-monitor/hooks/register.js'

const tracker = createRateTracker()
const a = { turnId: 'turn-a', index: 0 }
tracker.beginTurn(a.turnId, 1000)
tracker.beginStep(a, 1000)
tracker.observe(a, { kind: 'text', text: 'DO_NOT_SAVE' }, 2000)
assert.equal(tracker.phase, 'generating')
assert.equal(tracker.snapshot(2000).sample, null, 'chunks are not counted as tokens')
tracker.observe(a, { kind: 'tool', name: 'DO_NOT_SAVE' }, 2500)
assert.equal(tracker.phase, 'generating', 'writing tool arguments is still model generation')
tracker.observe(a, { kind: 'stop', usage: { output_tokens: 3418 }, stopReason: 'tool_use' }, 6000)
assert.equal(tracker.phase, 'tools')
assert.deepEqual(tracker.snapshot(7000).sample, { outputTokens: 3418, durationMs: 5000, completedAt: new Date(6000).toISOString(), stopReason: 'tool_use', index: 0 })
tracker.completeStep(a, 9000, { output_tokens: 3418 }, 'tool_use')
assert.equal(tracker.snapshot(9000).sample.durationMs, 5000, 'finishing the hook must not add tool time to the request')
const b = { turnId: 'turn-a', index: 1 }
tracker.beginStep(b, 12000)
assert.equal(tracker.snapshot(13000).sample.outputTokens, 3418, 'same-turn waiting retains the last request sample')
tracker.completeStep(b, 14000, { output_tokens: 100 }, 'end_turn')
tracker.completeStep(a, 15000, { output_tokens: 999999 }, 'tool_use')
assert.equal(tracker.snapshot(15000).sample.outputTokens, 100, 'late step cannot overwrite a newer sample')
tracker.endTurn('turn-a', 'aborted')
assert.equal(tracker.phase, 'interrupted')
tracker.beginTurn('turn-b', 16000)
assert.equal(tracker.snapshot(16000).sample, null)
tracker.beginStep({ turnId: 'turn-b', index: 0 }, 16000)
tracker.completeStep({ turnId: 'turn-b', index: 0 }, 16000, { output_tokens: 10 }, 'end_turn')
assert.equal(tracker.snapshot(16000).sample, null, 'zero-duration samples must not manufacture a speed')
tracker.beginStep({ turnId: 'turn-b', index: 1 }, 17000)
tracker.completeStep({ turnId: 'turn-b', index: 1 }, 18000, { output_tokens: 0 }, 'end_turn')
assert.equal(tracker.snapshot(18000).sample.outputTokens, 0, 'an authoritative zero-output response is a valid zero sample')
tracker.beginStep({ turnId: 'turn-b', index: 2 }, 19000)
tracker.completeStep({ turnId: 'turn-b', index: 2 }, 20000, null, null)
assert.equal(tracker.phase, 'error', 'a request with no response is not a completed model sample')

const hooks = {}
register((name, handler) => { hooks[name] = handler })
const files = new Map()
const timers = []
let at = Date.now()
let fail = false
const $ = {
  env: { get: async () => 'C:\\fixture' },
  session: { id: async () => 'session-main' },
  clock: {
    now: async () => ++at,
    every: (ms, fn) => { const timer = { ms, fn, cancelled: false, cancel() { this.cancelled = true } }; timers.push(timer); return timer },
  },
  fs: { write: async (path, content) => { if (fail) throw new Error('disk-failure'); files.set(path, content) } },
}
const flush = () => new Promise(resolve => setImmediate(resolve))
const latest = () => [...files.values()].map(JSON.parse).filter(v => v.kind === 'modelRate').sort((x, y) => x.sequence - y.sequence).at(-1)
let requests = 0
let streamClosed = 0
const chunks = [
  { kind: 'engine', ref: 1 },
  { kind: 'thinking', text: 'DO_NOT_SAVE', index: 0, ref: 2 },
  { kind: 'text', text: 'DO_NOT_SAVE', index: 1, ref: 3 },
  { kind: 'tool', name: 'DO_NOT_SAVE', id: 'fixture', index: 2, ref: 4 },
  { kind: 'input', json: '{"secret":"DO_NOT_SAVE"}', index: 2, ref: 5 },
  { kind: 'stop', usage: { output_tokens: 250 }, stopReason: 'tool_use', ref: 6 },
]
const result = Object.freeze({ turnId: 'main-turn', index: 0, answer: 'DO_NOT_SAVE', toolUses: [], usage: { output_tokens: 250 }, stopReason: 'tool_use' })
function streamFor(items = chunks, answer = result, error = null) {
  const stream = (async function* () {
    try {
      for (const item of items) { at += 1000; yield item }
      if (error) throw error
      return answer
    } finally { streamClosed++ }
  })()
  stream.result = Promise.resolve(answer)
  return stream
}
async function collect(stream) {
  const output = []
  while (true) { const r = await stream.next(); if (r.done) return { output, result: r.value }; output.push(r.value) }
}
const step = { turnId: 'main-turn', index: 0, model: 'fixture', messageCount: 1 }
const startResult = { turnId: step.turnId }
assert.equal(await hooks['turn.start']($, { turnId: step.turnId }, async () => startResult), startResult)
const streamed = await collect(hooks['turn.step']($, step, () => { requests++; return streamFor() }))
await flush()
assert.equal(requests, 1, 'observer must never create a second model request')
assert.equal(streamed.result, result, 'host result identity is preserved')
assert.equal(streamed.output.length, chunks.length)
streamed.output.forEach((chunk, index) => assert.equal(chunk, chunks[index], 'every original chunk is forwarded unchanged'))
assert.equal(latest().phase, 'tools')
assert.equal(latest().sample.outputTokens, 250)
assert.ok(latest().sample.durationMs >= 6000)
assert.ok(![...files.values()].some(v => v.includes('DO_NOT_SAVE')), 'content and arguments must never be persisted')
at += 28000
timers.at(-1).fn()
await flush()
assert.equal(latest().sample.outputTokens, 250, 'tool heartbeat preserves the model sample')
assert.equal(latest().phase, 'tools')
const callsBeforeAgent = requests
const writtenBeforeAgent = files.size
await collect(hooks['turn.step']($, { ...step, agentId: 'child' }, () => { requests++; return streamFor() }))
await flush()
assert.equal(requests, callsBeforeAgent + 1)
assert.equal(files.size, writtenBeforeAgent, 'subagents do not write parent rate snapshots')
const completeResult = { text: 'DO_NOT_SAVE' }
assert.equal(await hooks['turn.complete']($, { turnId: step.turnId, reason: 'answer' }, async () => completeResult), completeResult)
await flush()
assert.equal(latest().phase, 'completed')
assert.ok(timers.every(t => t.cancelled), 'terminal turns cancel their heartbeat')
await hooks['turn.start']($, { turnId: 'next-turn' }, async () => ({ turnId: 'next-turn' }))
await flush()
assert.equal(latest().sample, null, 'new turn starts without the previous sample')
fail = true
const diskFailure = await collect(hooks['turn.step']($, { ...step, turnId: 'next-turn' }, () => streamFor()))
assert.equal(diskFailure.result, result, 'optional disk failure cannot break a response')
fail = false
const originalError = new Error('host-stream-error')
await assert.rejects(collect(hooks['turn.step']($, { ...step, turnId: 'next-turn', index: 1 }, () => streamFor([], result, originalError))), error => error === originalError)
await flush()
assert.equal(latest().phase, 'interrupted')
const countBeforeCancel = streamClosed
const cancelled = hooks['turn.step']($, { ...step, turnId: 'next-turn', index: 2 }, () => streamFor())
await cancelled.next()
await cancelled.return()
await flush()
assert.equal(streamClosed, countBeforeCancel + 1, 'cancelling the observer closes the original stream once')
assert.equal(latest().phase, 'interrupted')
assert.ok(timers.every(t => t.cancelled))
console.log('PASS: request timing, tool retention, original stream/result identity, single request, privacy, errors and cancellation.')
