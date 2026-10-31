[CmdletBinding()]
param(
  # Also delete readings and the log. Claude's own data is never touched.
  [switch]$RemoveData
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

Stop-Overlay
$claude = Find-ClaudeCli
if ($claude) {
  & $claude plugin uninstall $PluginId
  & $claude plugin marketplace remove $MarketplaceName
} else {
  Write-Warning 'Claude Code was not found, so the plugin entry was left in place.'
}

$bin = Split-Path $OverlayExe
if (Test-Path $bin) { Remove-Item -Recurse -Force -Confirm:$false $bin }
if ($RemoveData -and (Test-Path $StateRoot)) { Remove-Item -Recurse -Force -Confirm:$false $StateRoot }
Write-Host 'Uninstalled. Restart open Claude sessions to unload the plugin.'
