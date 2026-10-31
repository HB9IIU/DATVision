$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
Get-Command dotnet -ErrorAction Stop | Out-Null
& dotnet publish (Join-Path $projectRoot 'ui/DatvRx.Player/DatvRx.Player.csproj') -c Release --no-self-contained --artifacts-path (Join-Path $projectRoot 'build/dotnet') -o (Join-Path $projectRoot 'build/windows/player')
if ($LASTEXITCODE -ne 0) { throw 'Player build failed. Install the .NET 10 SDK.' }
Write-Host 'Player built. Start RTL DATV Player.cmd from the project root.'
