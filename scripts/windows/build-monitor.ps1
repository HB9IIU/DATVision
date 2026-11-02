$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Get-Command dotnet -ErrorAction Stop | Out-Null
& dotnet publish (Join-Path $projectRoot 'ui/DatvRx.Player/DatvRx.Player.csproj') -c Release --no-self-contained --artifacts-path (Join-Path $projectRoot 'build/dotnet') -o (Join-Path $projectRoot 'build/windows/monitor')
if ($LASTEXITCODE -ne 0) { throw 'Windows monitor build failed. See the build output for details.' }
Write-Host 'Built. Open DATVision.cmd from the project root.'
