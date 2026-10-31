param([string]$MsysRoot = 'C:\msys64', [switch]$RemotePi)
$ErrorActionPreference = 'Stop'
$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
foreach ($relative in @('build/windows/player/DatvRx.Player.exe', 'build/windows/datvrx.exe', 'build/windows/rtl_iq.exe')) {
    if (!(Test-Path -LiteralPath (Join-Path $projectRoot $relative))) { throw "Missing $relative. Build the receiver and player first; see docs/PLAYER.md." }
}
if (!$RemotePi -and (Get-Process rtl_sdr,rtl_iq,datvrx -ErrorAction SilentlyContinue)) {
    throw 'An RTL-SDR receiver is already running. Close the live beacon window or other receiver first.'
}
$env:DATVRX_MSYS_ROOT = $MsysRoot
$playerArguments = @('"' + $projectRoot + '"')
if ($RemotePi) { $playerArguments += '--pi' }
Start-Process -FilePath (Join-Path $projectRoot 'build/windows/player/DatvRx.Player.exe') -ArgumentList $playerArguments -WorkingDirectory $projectRoot | Out-Null
