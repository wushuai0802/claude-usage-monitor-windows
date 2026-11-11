// Direct hook entry point, also exercised by offline tests. No Node/DOM APIs in the mod.
import { createRateTracker } from './rate-state.js'
const stopHeartbeat = state => { try { state.heartbeat?.cancel() } catch {} state.heartbeat = null }
const persistRate = async ($, state) => {
  try {
    const root = await $.env.get('LOCALAPPDATA')
    const sessionId = await $.session.id()
    if (!root || !/^[a-zA-Z0-9_-]{1,128}$/.test(sessionId)) return
    const at = await $.clock.now()
    const current = state.rate.snapshot(at)
    if (!current) return
    const sequence = ++state.sequence
    const snapshot = { schemaVersion: 1, kind: 'modelRate', pluginVersion: '1.1.5', sessionId,
      updatedAt: new Date(at).toISOString(), sequence, ...current, complete: true }
    const path = root + '\\ClaudeUsageMonitor\\rates\\' + sessionId + '.slot' + (sequence % 2) + '.json'
    state.writes = state.writes.catch(() => undefined).then(() => $.fs.write(path, JSON.stringify(snapshot)))
    await state.writes
  } catch { /* Optional observations never change the host stream. */ }
}
const startHeartbeat = ($, state) => {
  if (state.heartbeat) return
  try { state.heartbeat = $.clock.every(2000, () => { void persistRate($, state) }) } catch {}
}
/** @type {import('claude-code').Register} */
export const register = on => {
  let sequence = 0
  let limitsAt = null
  let contextAt = null
  let writes = Promise.resolve()
  const rate = createRateTracker()
  const sampling = { rate, sequence: 0, writes: Promise.resolve(), heartbeat: null }

  on('session.start', async ($, e, next) => {
    const result = await next(e)
    try {
      const root = await $.env.get('LOCALAPPDATA')
      if (!root) return result
      const overlay = root + '\\ClaudeUsageMonitor\\bin\\ClaudeUsageOverlay.exe'
      if (await $.fs.stat(overlay).then(() => true, () => false)) {
        // Explorer keeps the single-instance overlay outside the agent's process tree.
        await $.process.run(['explorer.exe', overlay], { timeoutMs: 5000 })
      }
    } catch { /* Optional monitor failures never change the host hook result. */ }
    return result
  })

  on('session.measure', async ($, e, next) => {
    const result = await next(e)
    // Serialize our own writes; another session writes a different pair of files.
    writes = writes.catch(() => undefined).then(async () => {
      const root = await $.env.get('LOCALAPPDATA')
      if (!root) return
      const sessionId = await $.session.id()
      if (!/^[a-zA-Z0-9_-]{1,128}$/.test(sessionId)) return
      const updatedAt = new Date(await $.clock.now()).toISOString()
      if (e.changed.includes('rateLimits')) limitsAt = updatedAt
      if (e.changed.includes('context')) contextAt = updatedAt
      const snapshot = {
        schemaVersion: 2, pluginVersion: '1.1.5', sessionId,
        updatedAt, sequence: ++sequence,
        rateLimitsAt: limitsAt,
        rateLimits: e.rateLimits.map(({ kind, percentUsed, resetsAt }) => ({ kind, percentUsed, resetsAt })),
        contextAt,
        context: { percent: e.context.percent ?? null, tokens: e.context.tokens ?? null, window: e.context.window },
        complete: true,
      }
      // The mod API offers write(), not rename(). Alternating slots retain a complete
      // previous snapshot across interrupted writes without launching a helper process.
      const path = root + '\\ClaudeUsageMonitor\\measurements\\' + sessionId + '.slot' + (sequence % 2) + '.json'
      await $.fs.write(path, JSON.stringify(snapshot))
    })
    await writes.catch(() => undefined)
    return result
  })

  on('turn.start', async ($, e, next) => {
    const result = await next(e)
    if (e.agentId == null) {
      try {
        rate.beginTurn(e.turnId, await $.clock.now())
        startHeartbeat($, sampling)
        void persistRate($, sampling)
      } catch {}
    }
    return result
  })

  on('turn.step', async function* ($, e, next) {
    // Exactly one request. Forward every original chunk and the original result.
    const stream = next(e)
    if (e.agentId != null) return yield* stream
    let timed = false
    let finished = false
    try {
      try {
        rate.beginStep(e, await $.clock.now())
        timed = true
        startHeartbeat($, sampling)
        void persistRate($, sampling)
      } catch {}
      for await (const chunk of stream) {
        if (timed && (chunk.kind === 'stop' || rate.phase === 'waiting')) {
          try {
            if (rate.observe(e, chunk, await $.clock.now())) void persistRate($, sampling)
          } catch {}
        }
        yield chunk
      }
      const result = await stream.result
      if (timed) {
        try {
          rate.completeStep(e, await $.clock.now(), result.usage, result.stopReason)
          void persistRate($, sampling)
        } catch {}
      }
      finished = true
      return result
    } finally {
      if (!finished && timed) {
        rate.interruptStep(e)
        stopHeartbeat(sampling)
        void persistRate($, sampling)
      }
    }
  })

  on('turn.complete', async ($, e, next) => {
    if (e.agentId == null) {
      rate.endTurn(e.turnId, e.reason)
      stopHeartbeat(sampling)
      void persistRate($, sampling)
    }
    return next(e)
  })

  on('session.end', async ($, e, next) => {
    stopHeartbeat(sampling)
    return next(e)
  })
}
