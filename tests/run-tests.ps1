$ErrorActionPreference = 'Stop'
$runner = Join-Path $env:TEMP "usage-overlay-tests-$PID.exe"
try {
  & (Join-Path $PSScriptRoot '..\scripts\build.ps1') -Output $runner -Target exe -Main CollectorTests -ExtraSource (Join-Path $PSScriptRoot 'CollectorTests.cs')
  & $runner
  $code = $LASTEXITCODE
} finally {
  Remove-Item $runner -ErrorAction SilentlyContinue
}
exit $code
