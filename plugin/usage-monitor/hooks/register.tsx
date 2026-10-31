import type { Register } from 'claude-code'

export const register: Register = on => {
  on('session.start', async ($, e, next) => {
    const result = await next(e)

    const localAppData = await $.env.get('LOCALAPPDATA')
    const overlay = `${localAppData}\\ClaudeUsageMonitor\\bin\\ClaudeUsageOverlay.exe`
    // Given a missing path, explorer would open a folder window instead of failing.
    const isInstalled = await $.fs.stat(overlay).then(() => true, () => false)

    if (localAppData && isInstalled) {
      // Through explorer, so the overlay does not belong to this session's process tree.
      // The overlay holds a single-instance mutex, and explorer reports failure by exit code alone.
      await $.process.run(['explorer.exe', overlay], { timeoutMs: 5000 }).catch(() => undefined)
    }

    return result
  })

  on('session.measure', async ($, e, next) => {
    const limits = e.rateLimits.map(({ kind, percentUsed, resetsAt }) => ({ kind, percentUsed, resetsAt }))

    const localAppData = await $.env.get('LOCALAPPDATA')
    if (localAppData && limits.length > 0 && e.changed.includes('rateLimits')) {
      const snapshot = JSON.stringify({ updatedAt: new Date(await $.clock.now()).toISOString(), rateLimits: limits })
      await $.fs.write(`${localAppData}\\ClaudeUsageMonitor\\limits.json`, snapshot)
    }
    if (localAppData && e.context.percent !== undefined && e.changed.includes('context')) {
      // One file per session: the overlay shows the context of the session whose transcript moved last.
      const sessionId = await $.session.id()
      await $.fs.write(`${localAppData}\\ClaudeUsageMonitor\\context\\${sessionId}.json`, JSON.stringify({ percent: e.context.percent }))
    }

    return next(e)
  })
}
