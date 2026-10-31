[CmdletBinding()]
param(
  [Parameter(Mandatory)] [string]$Output,
  [string[]]$ExtraSource = @(),
  [string]$Main,
  [ValidateSet('winexe', 'exe')] [string]$Target = 'winexe'
)

$ErrorActionPreference = 'Stop'
# The .NET Framework compiler ships with Windows, so installing needs no SDK.
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$csc = Join-Path $framework 'csc.exe'
if (-not (Test-Path $csc)) { throw "C# compiler not found: $csc" }

$source = Join-Path $PSScriptRoot '..\overlay\ClaudeUsageOverlay.cs'
New-Item -ItemType Directory -Force -Path (Split-Path $Output) | Out-Null
$arguments = @(
  '/nologo', '/codepage:65001', "/target:$Target", '/optimize+', "/out:$Output",
  '/r:System.Windows.Forms.dll', '/r:System.Drawing.dll', '/r:System.Core.dll', '/r:System.Web.Extensions.dll',
  "/r:$framework\WPF\UIAutomationClient.dll", "/r:$framework\WPF\UIAutomationTypes.dll", "/r:$framework\WPF\WindowsBase.dll"
)
if ($Main) { $arguments += "/main:$Main" }
$arguments += $source
$arguments += $ExtraSource

& $csc @arguments
if ($LASTEXITCODE -ne 0) { throw "Build failed with exit code $LASTEXITCODE" }
