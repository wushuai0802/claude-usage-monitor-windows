// Direct hook entry point, also exercised by offline tests. No Node/DOM APIs in the mod.
/** @type {import('claude-code').Register} */
export const register = on => {
  let sequence = 0
  let limitsAt = null
  let contextAt = null
  let writes = Promise.resolve()

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
        schemaVersion: 2, pluginVersion: '1.1.0', sessionId,
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
}
