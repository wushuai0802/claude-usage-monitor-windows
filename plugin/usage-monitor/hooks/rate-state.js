// Pure timing state. Never reads or stores chunk text, thinking, arguments or tool names.
export const createRateTracker = () => {
  let turnId = null
  let phase = 'idle'
  let turnStartedAt = null
  let request = null
  let sample = null
  const iso = value => new Date(value).toISOString()
  const validTime = value => Number.isFinite(value) && value > 0
  const beginTurn = (id, at) => {
    turnId = id
    turnStartedAt = at
    phase = 'waiting'
    request = null
    sample = null
  }
  const beginStep = (event, at) => {
    if (turnId !== event.turnId) beginTurn(event.turnId, at)
    request = { index: event.index, startedAt: at, finished: false }
    phase = 'waiting'
  }
  const completeStep = (event, at, usage, stopReason) => {
    if (turnId !== event.turnId || !request || request.index !== event.index || request.finished) return
    request.finished = true
    if (stopReason == null) { phase = 'error'; return }
    const outputTokens = usage?.output_tokens
    const durationMs = at - request.startedAt
    if (Number.isSafeInteger(outputTokens) && outputTokens >= 0 && durationMs >= 50 && durationMs <= 86400000) {
      sample = { outputTokens, durationMs, completedAt: iso(at), stopReason: stopReason ?? null, index: event.index }
    }
    phase = stopReason === 'tool_use' ? 'tools' : 'completed'
  }
  return {
    beginTurn,
    beginStep,
    observe(event, chunk, at) {
      if (turnId !== event.turnId || !request || request.index !== event.index) return false
      if (chunk.kind === 'stop') {
        completeStep(event, at, chunk.usage, chunk.stopReason)
        return true
      }
      if (!request.finished && phase === 'waiting' && ['text', 'thinking', 'input', 'tool'].includes(chunk.kind)) {
        phase = 'generating'
        return true
      }
      return false
    },
    completeStep,
    interruptStep(event) {
      if (turnId === event.turnId && request?.index === event.index) phase = 'interrupted'
    },
    endTurn(id, reason) {
      if (turnId === id) phase = reason === 'answer' ? 'completed' : reason === 'aborted' ? 'interrupted' : 'error'
    },
    snapshot(at) {
      if (!turnId || !validTime(at)) return null
      return { turnId, turnStartedAt: iso(turnStartedAt), phase, requestIndex: request?.index ?? null, sample: sample ? { ...sample } : null }
    },
    get phase() { return phase },
    get turnId() { return turnId },
  }
}
