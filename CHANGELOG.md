# Changelog

## 1.1.2 — 2026-11-10

- Observe main-session model requests through turn.step without rewriting chunks, results, models or tool calls, or issuing additional inference requests.
- Show the latest completed model request's average output rate, including the initial wait. Retain it through the same turn's tool/wait/generation phases and label the source and phase in details; clear the strip after a finished turn or a new user prompt.
- Keep valid output from responses ending in tool_use. Log fallback distinguishes tool results from a new human prompt and ignores repeated records of a completed response for timing.
- Add independent paired timing snapshots, active-turn heartbeats with cancellation, interruption/error handling and stale-observer fallback.
- Add regression checks for the formerly zero tool-bound response, repeated JSONL records, request timing, heartbeat freshness, stream/result identity, single request execution, content privacy and cancellation.

## 1.1.1 — 2026-11-10

- Measure the largest actual gap between composer toolbar controls, with six-DIP clearance on both sides. Re-read cached control bounds as labels and window layout change.
- Keep the status strip on one line and measure it independently of the previous window width, avoiding wrapped text clipped above and below.
- Reserve two physical pixels for rounding, recompute on DPI changes, and hide lower-priority metrics when they cannot fit without covering controls.
- Include geometry and selected metric IDs in local status diagnostics. Add regression coverage for the observed toolbar, extra controls, reset text, narrow widths and scaled layout.

## 1.1.0 — 2026-11-06

- Transparent WPF status strip with DirectWrite text, a light/dark detail card, saved display selections and narrow-window priorities.
- Cached directory index, file notifications, bounded incremental transcript reads and low-frequency reconciliation.
- Suspend scanning and dispose the file watcher when Claude desktop exits. Resume from local records when it returns. No login startup entry is added.
- Parent-session speed stays separate from child-agent activity; new requests and tool calls clear old speed, samples expire after five seconds.
- Optional hook I/O errors are isolated. Alternating snapshots per session preserve complete readings on partial writes and invalidate obsolete context after compaction.
- Hide around composer menus; retain hide-during-drag behavior; add per-monitor DPI geometry and localized model-family fallback.
- Add offline collection, hook, recovery, settings and DPI tests plus a Windows CI workflow.
- Current-page mapping is not confirmed: speed and context remain explicitly attributed to the most recently active parent session. Physical mixed-DPI validation remains necessary.

## 1.0.3

- Hide the overlay while the Claude window moves or resizes.
