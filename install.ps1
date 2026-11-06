[CmdletBinding()]
param(
  [string]$InstallRoot = '',
  [switch]$SkipPlugin,
  [switch]$SkipLaunch
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

if ($InstallRoot) {
  $StateRoot = [IO.Path]::GetFullPath($InstallRoot)
  $OverlayExe = Join-Path $StateRoot 'bin\ClaudeUsageOverlay.exe'
}
if (-not $SkipPlugin) {
  $claude = Find-ClaudeCli
  if (-not $claude) { throw 'Claude Code was not found. Open the Code tab of Claude desktop once, then run this again.' }
  & $claude plugin validate (Join-Path $PSScriptRoot 'plugin\usage-monitor')
  if ($LASTEXITCODE -ne 0) { throw 'Plugin validation failed; the existing overlay is unchanged.' }
}

# Build first, so a failed build leaves any running overlay alone.
$built = "$OverlayExe.new"
& (Join-Path $PSScriptRoot 'scripts\build.ps1') -Output $built
if (-not $SkipPlugin) {
  $registry = Join-Path $env:USERPROFILE '.claude\plugins\known_marketplaces.json'
  $known = if (Test-Path -LiteralPath $registry) { Get-Content -LiteralPath $registry -Raw | ConvertFrom-Json } else { $null }
  if (-not $known -or $known.PSObject.Properties.Name -notcontains $MarketplaceName) {
    & $claude plugin marketplace add $PSScriptRoot
    if ($LASTEXITCODE -ne 0) { throw 'Adding the local plugin marketplace failed.' }
  }
  & $claude plugin marketplace update $MarketplaceName
  if ($LASTEXITCODE -ne 0) { throw 'Updating the local marketplace failed.' }
  & $claude plugin install $PluginId
  if ($LASTEXITCODE -ne 0) { throw 'Installing the plugin failed.' }
  & $claude plugin update $PluginId
  if ($LASTEXITCODE -ne 0) { throw 'Updating the plugin failed.' }
}
if (Test-Path -LiteralPath $OverlayExe) { Copy-Item -LiteralPath $OverlayExe -Destination "$OverlayExe.previous" -Force }
Stop-Overlay
try {
  Move-Item -LiteralPath $built -Destination $OverlayExe -Force
} catch {
  if (Test-Path -LiteralPath "$OverlayExe.previous") { Copy-Item -LiteralPath "$OverlayExe.previous" -Destination $OverlayExe -Force }
  if (-not $SkipLaunch) { Start-Process explorer.exe -ArgumentList ('"' + $OverlayExe + '"') -WindowStyle Hidden }
  throw
}

# Through explorer, so the overlay does not belong to this shell's process tree.
if (-not $SkipLaunch) { Start-Process explorer.exe -ArgumentList ('"' + $OverlayExe + '"') -WindowStyle Hidden }
Write-Host "Installed. The plugin reads from $PSScriptRoot, so keep this folder in place."
Write-Host 'The overlay is updated. Existing sessions may keep their hook until the next normal reload; legacy readings remain supported.'
