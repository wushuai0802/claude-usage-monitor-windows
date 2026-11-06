$MarketplaceName = 'usage-monitor-local'
$PluginId = "usage-monitor@$MarketplaceName"
$StateRoot = Join-Path $env:LOCALAPPDATA 'ClaudeUsageMonitor'
$OverlayExe = Join-Path $StateRoot 'bin\ClaudeUsageOverlay.exe'

function Find-ClaudeCli {
  $command = Get-Command claude -ErrorAction SilentlyContinue
  if ($command) { return $command.Source }
  # The desktop app keeps its own Claude Code outside PATH, in a versioned folder.
  $candidates = foreach ($root in @((Join-Path $env:LOCALAPPDATA 'Claude-Data'), (Join-Path $env:APPDATA 'Claude'))) {
    if (Test-Path $root) { Get-ChildItem $root -Recurse -Filter claude.exe -ErrorAction SilentlyContinue }
  }
  $newest = $candidates | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  if ($newest) { return $newest.FullName }
  return $null
}

function Stop-Overlay {
  # Stop only this installation, never a preview or another user's copy.
  Get-Process ClaudeUsageOverlay -ErrorAction SilentlyContinue | Where-Object {
    $_.Path -and [string]::Equals($_.Path, $OverlayExe, [StringComparison]::OrdinalIgnoreCase)
  } | Stop-Process -Force -Confirm:$false
  Start-Sleep -Milliseconds 500
}
