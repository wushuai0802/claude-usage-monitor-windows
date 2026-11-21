$ErrorActionPreference = 'SilentlyContinue'
if (-not $env:LOCALAPPDATA -or -not $env:CLAUDE_CONFIG_DIR) { return }

# The isolated Desktop runtime may load ordinary hooks without starting the mod.
$profiles = Join-Path $env:LOCALAPPDATA 'Claude-3p\local-agent-mode-sessions'
if (-not $env:CLAUDE_CONFIG_DIR.StartsWith($profiles + '\', [StringComparison]::OrdinalIgnoreCase)) { return }

$overlay = Join-Path $env:LOCALAPPDATA 'ClaudeUsageMonitor\bin\ClaudeUsageOverlay.exe'
if (Test-Path -LiteralPath $overlay -PathType Leaf) {
    Start-Process explorer.exe -ArgumentList ('"' + $overlay + '"') -WindowStyle Hidden
}
