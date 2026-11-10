$ErrorActionPreference = 'Stop'
$runner = Join-Path $env:TEMP "usage-overlay-tests-$PID.exe"
try {
  & (Join-Path $PSScriptRoot '..\scripts\build.ps1') -Output $runner -Target exe -Main RegressionTests -ExtraSource (Join-Path $PSScriptRoot 'RegressionTests.cs')
  & $runner
  $code = $LASTEXITCODE
  if ($code -ne 0) { throw "Regression tests failed with exit code $code" }
  & (Join-Path $PSScriptRoot '..\scripts\build.ps1') -Output $runner -Target exe -Main RateRegressionTests -ExtraSource (Join-Path $PSScriptRoot 'RateRegressionTests.cs')
  & $runner
  if ($LASTEXITCODE -ne 0) { throw 'Rate sampling regression tests failed.' }
  $node = Get-Command node -ErrorAction SilentlyContinue
  if (-not $node) { throw 'Node.js is required for development hook tests, but not for installation.' }
  & $node.Source (Join-Path $PSScriptRoot 'hooks.test.mjs')
  if ($LASTEXITCODE -ne 0) { throw 'Hook regression tests failed.' }
  & $node.Source (Join-Path $PSScriptRoot 'rate-hooks.test.mjs')
  if ($LASTEXITCODE -ne 0) { throw 'Streaming rate hook tests failed.' }
} finally {
  Remove-Item $runner -ErrorAction SilentlyContinue
}
exit $code
