[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'scripts\common.ps1')

$claude = Find-ClaudeCli
if (-not $claude) { throw 'Claude Code was not found. Open the Code tab of Claude desktop once, then run this again.' }

# Build first, so a failed build leaves any running overlay alone.
$built = "$OverlayExe.new"
& (Join-Path $PSScriptRoot 'scripts\build.ps1') -Output $built
Stop-Overlay
Move-Item -Force $built $OverlayExe

& $claude plugin marketplace add $PSScriptRoot
if ($LASTEXITCODE -ne 0) { throw 'Adding the local plugin marketplace failed.' }
& $claude plugin marketplace update $MarketplaceName
& $claude plugin install $PluginId
if ($LASTEXITCODE -ne 0) { throw 'Installing the plugin failed.' }
& $claude plugin update $PluginId

# Through explorer, so the overlay does not belong to this shell's process tree.
Start-Process explorer.exe $OverlayExe
Write-Host "Installed. The plugin reads from $PSScriptRoot, so keep this folder in place."
Write-Host 'Restart open Claude sessions; 5h, 7d and context fill in after their next response.'
